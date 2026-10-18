// The Northwind deal desk: agents negotiate a customer's order, on RavenDB's Northwind sample data.
//   read        - a RavenDB AI agent reads the email and the attached PO (RavenAIAgent.RunAsync<DealAsk>, a PDF attachment)
//   4 checks    - in parallel: stock + substitutes (vector search), customer value (map-reduce index), margin (code),
//                 similar past deals (vector search)
//   debate      - the sales rep, finance and operations argue it out (a MAF group chat of RavenDB AI agents)
//   rule        - the deal lead follows the pricing policy, a skill document (ChatClientAgent + RavenAgentSkillsSource)
//   negotiate   - counter-offers loop back to the customer (up to 3 rounds); discounts above the rep's authority wait
//                 for the rep's manager (Employees.ReportsTo); a won deal becomes a real Orders document
// The workflow is checkpointed in RavenDB and driven by a data subscription; the page follows the deals live through
// the RavenDB Changes API (Server-Sent Events). Open the URL it prints.
//
//   RAVEN_URL              licensed RavenDB server; when unset, an embedded server is started (needs RAVEN_LICENSE or
//                          RAVEN_License_Path, since AI agents are a licensed feature)
//   OPENAI_API_KEY         OpenAI key                         | or Azure OpenAI:  AZURE_OPENAI_ENDPOINT
//   OPENAI_MODEL           chat model (default gpt-4.1-mini)  |                   AZURE_OPENAI_API_KEY
//                                                             |                   AZURE_OPENAI_DEPLOYMENT

using System.Globalization;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Agents.AI.Hosting;
using Microsoft.AspNetCore.Mvc;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.Identities;
using RavenDB.Agents.AI;

// Northwind prices are in dollars.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");

var store = await Setup.OpenDocumentStoreAsync();
await Setup.ProvisionAsync(store);

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(store);
builder.Services.AddSingleton(Setup.CreateChatClient());
builder.Services.AddSingleton<DealDesk>();
builder.Services.AddHostedService<DealWorker>();

// The writer is registered the MAF way, with its sessions in RavenDB: one per deal (a back-office host, no isolation).
builder.Services.AddAIAgent(DealDesk.WriterAgent, (_, name) => new RavenAIAgent(store, DealDesk.WriterAgent, options: new RavenAIAgentOptions { Name = name }))
    .WithRavenSessionStore(withIsolation: false);

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/config", () => new { Studio = $"{store.Urls[0]}/studio/index.html#databases/edit?database={store.Database}&id=" });

app.MapGet("/companies", async () =>
{
    using var session = store.OpenAsyncSession();
    return await session.Query<Company>().OrderBy(c => c.Name).Select(c => new { c.Id, c.Name }).Take(100).ToListAsync();
});

app.MapGet("/deals", async () =>
{
    using var session = store.OpenAsyncSession();
    return await session.Query<DealRequest>().OrderByDescending(d => d.Id).Take(50).ToListAsync();
});

// A new deal: the email, and the customer's purchase order as a RavenDB attachment. The subscription picks it up.
app.MapPost("/deals", async ([FromForm] string company, [FromForm] string email, IFormFile? po) =>
{
    using var session = store.OpenAsyncSession();
    var customer = await session.LoadAsync<Company>(company);
    var deal = new DealRequest { Company = company, CompanyName = customer.Name, Email = email };

    // DealRequests/1, 2, ...: the id is taken up front, so the deal and its PO are saved together.
    var number = await store.Maintenance.SendAsync(new NextIdentityForOperation("DealRequests"));
    await session.StoreAsync(deal, $"DealRequests/{number}");
    if (po is not null)
    {
        session.Advanced.Attachments.Store(deal, "po.pdf", po.OpenReadStream(), "application/pdf");
    }

    await session.SaveChangesAsync();
    return deal;
}).DisableAntiforgery();

// The customer's reply and the manager's decision go on the deal; the subscription resumes its run.
app.MapPost("/deals/{n:int}/reply", async (int n, Reply reply) =>
{
    using var session = store.OpenAsyncSession();
    var deal = await session.LoadAsync<DealRequest>($"DealRequests/{n}");
    if (deal?.Status != DealStatus.WaitingForCustomer)
    {
        return Results.Conflict("The deal isn't waiting for the customer.");
    }

    deal.Messages.Add(new DealMessage("customer", deal.Round, reply.Text));
    deal.Status = DealStatus.CustomerReplied;
    await session.SaveChangesAsync();
    return Results.Ok();
});

app.MapPost("/deals/{n:int}/decision", async (int n, Decision decision) =>
{
    using var session = store.OpenAsyncSession();
    var deal = await session.LoadAsync<DealRequest>($"DealRequests/{n}");
    if (deal?.Status != DealStatus.WaitingForManager)
    {
        return Results.Conflict("The deal isn't waiting for a manager.");
    }

    deal.Escalation!.Approved = decision.Approved;
    deal.Escalation.Note = decision.Note;
    deal.Status = DealStatus.ManagerDecided;
    await session.SaveChangesAsync();
    return Results.Ok();
});

// The negotiation's timeline, from the deal's revisions.
app.MapGet("/deals/{n:int}/history", async (int n) =>
{
    using var session = store.OpenAsyncSession();
    var revisions = await session.Advanced.Revisions.GetForAsync<DealRequest>($"DealRequests/{n}", pageSize: 128);
    foreach (var revision in revisions)
    {
        revision.LastModified = session.Advanced.GetLastModifiedFor(revision);
    }

    return revisions;
});

// The deal lead's notes on the deal's customer (a file it keeps in RavenDB).
app.MapGet("/deals/{n:int}/notes", async (int n) =>
{
    using var session = store.OpenAsyncSession();
    var deal = await session.LoadAsync<DealRequest>($"DealRequests/{n}");
    return await new RavenAgentFileStore(store, root: DealDesk.NotesRoot).ReadAsync(DealDesk.NotesFile(deal.Company)) ?? string.Empty;
});

app.MapGet("/events", (DealDesk desk, CancellationToken cancellationToken) => TypedResults.ServerSentEvents(WatchDealsAsync(store, desk, cancellationToken)));

app.Run();

// Every change to a deal, pushed by the RavenDB Changes API, goes to the page as the deal document; the writer's
// emails go as they stream.
static async IAsyncEnumerable<SseItem<object>> WatchDealsAsync(IDocumentStore store, DealDesk desk, [EnumeratorCancellation] CancellationToken cancellationToken)
{
    var events = Channel.CreateUnbounded<object>();
    using var subscription = store.Changes().ForDocumentsInCollection<DealRequest>().Subscribe(change => events.Writer.TryWrite(change.Id));
    Action<Draft> drafting = draft => events.Writer.TryWrite(draft);
    desk.Drafting += drafting;
    try
    {
        await foreach (var item in events.Reader.ReadAllAsync(cancellationToken))
        {
            if (item is Draft draft)
            {
                yield return new SseItem<object>(draft, "draft");
                continue;
            }

            using var session = store.OpenAsyncSession();
            if (await session.LoadAsync<DealRequest>((string)item, cancellationToken) is { } deal)
            {
                yield return new SseItem<object>(deal, "deal");
            }
        }
    }
    finally
    {
        desk.Drafting -= drafting;
    }
}

internal sealed record Reply(string Text);

internal sealed record Decision(bool Approved, string? Note);
