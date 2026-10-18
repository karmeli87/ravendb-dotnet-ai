// A store-support agent that runs inside RavenDB (a RavenDB AI Agent) and is driven from MAF through RavenAIAgent.
// RavenDB calls the model, runs the agent's database queries and keeps the conversation; MAF supplies a client-side tool,
// asks a human to approve refunds, and gets its session from a session store so the next run resumes the same conversation.
//
//   RAVEN_URL              licensed RavenDB server; when unset, an embedded server is started (needs RAVEN_LICENSE or
//                          RAVEN_License_Path, since AI agents are a licensed feature)
//   OPENAI_API_KEY         OpenAI key                         | or Azure OpenAI:  AZURE_OPENAI_ENDPOINT
//   OPENAI_MODEL           chat model (default gpt-4.1-mini)  |                   AZURE_OPENAI_API_KEY
//                                                             |                   AZURE_OPENAI_DEPLOYMENT

using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.AI;
using Raven.Client.Documents.Operations.AI.Agents;
using Raven.Client.Documents.Operations.ConnectionStrings;
using Raven.Client.Exceptions;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Raven.Embedded;
using RavenDB.Agents.AI;

const string AgentId = "store-support";
const string CompanyId = "companies/1";

using var store = await OpenDocumentStoreAsync("AgentDemo");
await SeedOrdersAsync(store);

// A client-side implementation of the agent's send-email action: RavenDB asks for it mid-turn, MAF runs it in place.
// The same tool defines the action on the RavenDB agent (ToAgentToolAction), so the two can't drift apart.
var sendEmail = AIFunctionFactory.Create(
    ([Description("The email subject")] string subject, [Description("The email body")] string body) =>
    {
        Console.WriteLine($"  [email sent] {subject}");
        return "Email sent.";
    },
    "send-email",
    "Sends an email to the customer");

var configuration = await CreateRavenAgentAsync(store, sendEmail);

// issue-refund has no client-side implementation, so it comes back to us as a function call: a human approves it below.
AIAgent agent = configuration.ToRavenAIAgent(store, [sendEmail]);

// The session store keeps the session (the conversation id and parameters) under the session key, so the next run finds
// and continues the conversation; the key's partitions are the conversation's parameters: here the agent's "company".
var sessions = new RavenAgentSessionStore(store);
var sessionKey = new AgentSessionStoreKey("store-support-chat", new Dictionary<string, string> { ["company"] = CompanyId });
var session = await sessions.GetOrCreateSessionAsync(agent, sessionKey);

Console.WriteLine("Ask about your orders, ask for an email summary, or ask for a refund (empty line to exit).");
while (Console.ReadLine() is { Length: > 0 } input)
{
    Console.WriteLine($"> {input}");
    var calls = await StreamAsync(agent, [new ChatMessage(ChatRole.User, input)], session);

    // Caller-side actions: answer each one, then let the agent continue the same turn.
    while (calls.Count > 0)
    {
        List<AIContent> results = [];
        foreach (var call in calls)
        {
            Console.Write($"  Approve {call.Name} {string.Join(", ", call.Arguments?.Select(a => $"{a.Key}={a.Value}") ?? [])}? (y/n) ");
            var approved = Console.ReadLine()?.Trim().StartsWith('y') == true;
            Console.WriteLine(approved ? "y" : "n");
            results.Add(new FunctionResultContent(call.CallId, approved ? "Refund issued." : "The manager declined the refund."));
        }

        calls = await StreamAsync(agent, [new ChatMessage(ChatRole.Tool, results)], session);
    }

    await sessions.SaveSessionAsync(agent, sessionKey, session);
}

Console.WriteLine($"[RavenDB] conversation {((RavenAIAgentSession)session).ConversationId}");

// Streams the reply to the console and returns the actions the agent left for the caller.
static async Task<List<FunctionCallContent>> StreamAsync(AIAgent agent, IEnumerable<ChatMessage> messages, AgentSession session)
{
    List<FunctionCallContent> calls = [];
    await foreach (var update in agent.RunStreamingAsync(messages, session))
    {
        Console.Write(update.Text);
        calls.AddRange(update.Contents.OfType<FunctionCallContent>());
    }

    if (calls.Count == 0)
    {
        Console.WriteLine(); // a turn that ends with actions has no text
    }

    return calls;
}

// The agent lives in RavenDB: its model connection, instructions, the query it may run and the actions it may request.
static async Task<AiAgentConfiguration> CreateRavenAgentAsync(IDocumentStore store, AIFunction sendEmail)
{
    await store.Maintenance.SendAsync(new PutConnectionStringOperation<AiConnectionString>(CreateConnectionString()));

    var configuration = new AiAgentConfiguration(
        "Store support",
        "llm",
        "You are a support agent for an online store. Answer in one or two short sentences. " +
        "Use the get-orders query to answer questions about the customer's orders. " +
        "When the customer asks to be emailed, call the send-email action. " +
        "When the customer asks for a refund, call the issue-refund action with the order id; tell them the outcome.")
    {
        Identifier = AgentId,
        Parameters = [new AiAgentParameter("company", "The id of the customer's company")],
        Queries =
        [
            new AiAgentToolQuery("get-orders", "Lists the customer's orders", "from Orders where Company = $company select id() as OrderId, Product, Total")
            {
                ParametersSampleObject = "{}", // uses only the agent-level $company parameter
            },
        ],
        Actions =
        [
            sendEmail.ToAgentToolAction(),
            new AiAgentToolAction("issue-refund", "Refunds an order; a manager must approve it")
            {
                ParametersSampleObject = """{"orderId":"the id of the order to refund","reason":"why the customer wants a refund"}""",
            },
        ],
    };

    // RavenDB agents are created with a sample object, the answer's shape; RavenAIAgent.RunAsync<Reply> reads it typed.
    await store.AI.CreateAgentAsync(configuration, new Reply { Answer = "Your answer to the customer" });
    return configuration;
}

static AiConnectionString CreateConnectionString()
{
    var connectionString = new AiConnectionString { Name = "llm", Identifier = "llm", ModelType = AiModelType.Chat };
    if (Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT") is { Length: > 0 } endpoint)
    {
        var deployment = Required("AZURE_OPENAI_DEPLOYMENT");
        connectionString.AzureOpenAiSettings = new AzureOpenAiSettings(Required("AZURE_OPENAI_API_KEY"), endpoint, deployment, deployment, dimensions: null, temperature: null);
    }
    else
    {
        var model = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4.1-mini";
        connectionString.OpenAiSettings = new OpenAiSettings(Required("OPENAI_API_KEY"), "https://api.openai.com/v1", model, null, null, null, null, null, null);
    }

    return connectionString;

    static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Set {name}.");
}

static async Task<IDocumentStore> OpenDocumentStoreAsync(string database)
{
    if (Environment.GetEnvironmentVariable("RAVEN_URL") is { Length: > 0 } url)
    {
        var store = new DocumentStore { Urls = [url], Database = database }.Initialize();
        try
        {
            await store.Maintenance.Server.SendAsync(new CreateDatabaseOperation(new DatabaseRecord(database)));
        }
        catch (ConcurrencyException)
        {
            // Already exists.
        }

        return store;
    }

    // No server configured: run a licensed RavenDB in-process. Data persists across runs in ./RavenDB.
    var options = new ServerOptions { DataDirectory = Path.Combine(AppContext.BaseDirectory, "RavenDB") };
    options.Licensing.License = Environment.GetEnvironmentVariable("RAVEN_LICENSE") is { Length: > 0 } license ? license : null;
    options.Licensing.LicensePath = options.Licensing.License is null ? Environment.GetEnvironmentVariable("RAVEN_License_Path") : null;
    EmbeddedServer.Instance.StartServer(options);
    return await EmbeddedServer.Instance.GetDocumentStoreAsync(database);
}

static async Task SeedOrdersAsync(IDocumentStore store)
{
    using var session = store.OpenAsyncSession();
    await session.StoreAsync(new Order(CompanyId, "Laptop", 1200), "orders/1");
    await session.StoreAsync(new Order(CompanyId, "Mouse", 300), "orders/2");
    await session.StoreAsync(new Order("companies/2", "Desk", 999), "orders/3"); // another customer's: never visible here
    await session.SaveChangesAsync();
}

internal sealed record Order(string Company, string Product, decimal Total);

internal sealed class Reply
{
    public string Answer { get; set; } = string.Empty;
}
