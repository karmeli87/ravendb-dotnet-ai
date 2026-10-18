using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;
using RavenDB.Agents.AI;
using RavenDB.Extensions.VectorData;

// The deal desk workflow, one run per deal (the run's session id is the deal id), checkpointed in RavenDB:
//
//   read ─fan-out─► stock │ customer │ margin │ past-deals ─fan-in─► deal-file ─► debate ─► rule
//    ▲                                                                                       ├─ accept   ─► won
//    │                                                                                       ├─ decline  ─► lost
//    │                         ┌──────────────── approved ─► won ◄───────────────────────────┤
//    │   customer ◄─ offer ◄───┴── rejected ◄── manager ◄── escalate ◄──── above the rep's ──┤
//    └── (reply) ◄──────────────── offer ◄────────────────────────────────────── counter ────┘
//
// Every step's data lives on the deal document; the messages only carry the deal id (and the decision to route on).
internal sealed class DealDesk
{
    public const string ReaderAgent = "deal-reader";
    public const string SalesRepAgent = "deal-sales-rep";
    public const string FinanceAgent = "deal-finance";
    public const string OperationsAgent = "deal-operations";
    public const string WriterAgent = "deal-writer";
    public const string CustomerPort = "customer-reply";
    public const string ManagerPort = "manager-approval";
    public const int MaxRounds = 3;

    // The deal lead's notes, one file per customer: AgentFiles/deal-lead/companies-20-A.md.
    public const string NotesRoot = "deal-lead";

    private const string DebateScope = "debate";

    private readonly IDocumentStore _store;
    private readonly IServiceProvider _services;
    private readonly RavenAIAgent _reader;
    private readonly AIAgent[] _debaters;
    private readonly ChatClientAgent _lead;
    private readonly VectorStoreCollection<string, ProductCard> _products;
    private readonly VectorStoreCollection<string, PastDeal> _pastDeals;

    // The writer's email as it streams, for the page.
    public event Action<Draft>? Drafting;

    public static string NotesFile(string company) => company.Replace('/', '-') + ".md";

    public DealDesk(IDocumentStore store, IServiceProvider services, IChatClient chatClient)
    {
        _store = store;
        _services = services;
        _reader = new RavenAIAgent(store, ReaderAgent);
        _debaters =
        [
            new RavenAIAgent(store, SalesRepAgent, options: new RavenAIAgentOptions { Name = "Sales rep" }),
            new RavenAIAgent(store, FinanceAgent, options: new RavenAIAgentOptions { Name = "Finance" }),
            new RavenAIAgent(store, OperationsAgent, options: new RavenAIAgentOptions { Name = "Operations" }),
        ];

        // The deal lead follows the pricing policy, a skill document: no caching, so a policy edit applies to the next ruling.
        _lead = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "deal-lead",
            ChatOptions = new ChatOptions
            {
                Instructions = "You are the deal lead at Northwind Traders. You rule on a customer's deal after the checks and the " +
                               "team's debate. Always load the pricing-policy skill first, and follow it. Read your notes on the " +
                               "customer before ruling, and after ruling add one line to them: the date, the round, what the " +
                               "customer asked, your decision and why.",
            },
            AIContextProviders =
            [
                new AgentSkillsProviderBuilder()
                    .UseSource(new RavenAgentSkillsSource(store))
                    .UseOptions(options => { options.DisableLoadSkillApproval = true; options.DisableReadSkillResourceApproval = true; })
                    .DisableCaching()
                    .Build(),

                // The lead's notes on each customer, kept as files in RavenDB, so it remembers past rounds and deals.
                new FileMemoryProvider(new RavenAgentFileStore(store, root: NotesRoot)),
            ],
        });

        var vectors = new RavenVectorStore(store, new() { WaitForNonStaleResults = true });
        _products = vectors.GetCollection<string, ProductCard>("ProductCards");
        _pastDeals = vectors.GetCollection<string, PastDeal>("PastDeals");
    }

    // The same graph is rebuilt on resume: MAF checks that a checkpoint belongs to an identical workflow.
    public Workflow Build()
    {
        Func<DealRef, IWorkflowContext, CancellationToken, ValueTask<DealRef>> read = ReadAsync;
        Func<DealRef, IWorkflowContext, CancellationToken, ValueTask<CheckDone>> stock = CheckStockAsync;
        Func<DealRef, IWorkflowContext, CancellationToken, ValueTask<CheckDone>> customer = CheckCustomerAsync;
        Func<DealRef, IWorkflowContext, CancellationToken, ValueTask<CheckDone>> margin = CheckMarginAsync;
        Func<DealRef, IWorkflowContext, CancellationToken, ValueTask<CheckDone>> pastDeals = CheckPastDealsAsync;
        Func<CheckDone, IWorkflowContext, CancellationToken, ValueTask> dealFile = OpenDealFileAsync;
        Func<List<ChatMessage>, IWorkflowContext, CancellationToken, ValueTask<DealStep>> rule = RuleAsync;
        Func<DealStep, IWorkflowContext, CancellationToken, ValueTask<DealStep>> escalate = EscalateAsync;
        Func<DealStep, IWorkflowContext, CancellationToken, ValueTask<DealRef>> offer = OfferAsync;
        Func<DealStep, IWorkflowContext, CancellationToken, ValueTask<string>> won = WinAsync;
        Func<DealStep, IWorkflowContext, CancellationToken, ValueTask<string>> lost = LoseAsync;

        ExecutorBinding readStep = read.BindAsExecutor("read");
        ExecutorBinding[] checks = [stock.BindAsExecutor("stock"), customer.BindAsExecutor("customer"), margin.BindAsExecutor("margin"), pastDeals.BindAsExecutor("past-deals")];
        ExecutorBinding dealFileStep = dealFile.BindAsExecutor("deal-file");
        ExecutorBinding ruleStep = rule.BindAsExecutor("rule");
        ExecutorBinding escalateStep = escalate.BindAsExecutor("escalate");
        ExecutorBinding offerStep = offer.BindAsExecutor("offer");
        ExecutorBinding wonStep = won.BindAsExecutor("won");
        ExecutorBinding lostStep = lost.BindAsExecutor("lost");

        // The debate is a group chat sub-workflow: the sales rep, finance and operations take turns.
        ExecutorBinding debate = AgentWorkflowBuilder
            .CreateGroupChatBuilderWith(agents => new DebateManager(agents) { MaximumIterationCount = 4 })
            .AddParticipants(_debaters)
            .Build()
            .BindAsExecutor("debate");

        // People answer through request ports: the run is checkpointed while it waits, for as long as it takes.
        ExecutorBinding customerPort = RequestPort.Create<DealRef, DealRef>(CustomerPort);
        ExecutorBinding managerPort = RequestPort.Create<DealStep, DealStep>(ManagerPort);

        return new WorkflowBuilder(readStep)
            .AddFanOutEdge(readStep, checks)
            .AddFanInBarrierEdge(checks, dealFileStep)
            .AddEdge(dealFileStep, debate)
            .AddEdge(debate, ruleStep)
            .AddEdge<DealStep>(ruleStep, wonStep, step => step?.Decision == Decisions.Accept)
            .AddEdge<DealStep>(ruleStep, offerStep, step => step?.Decision == Decisions.Counter)
            .AddEdge<DealStep>(ruleStep, escalateStep, step => step?.Decision == Decisions.Escalate)
            .AddEdge<DealStep>(ruleStep, lostStep, step => step?.Decision == Decisions.Decline)
            .AddEdge(escalateStep, managerPort)
            .AddEdge<DealStep>(managerPort, wonStep, step => step?.Decision == Decisions.Accept)
            .AddEdge<DealStep>(managerPort, offerStep, step => step?.Decision == Decisions.Counter)
            .AddEdge(offerStep, customerPort)
            .AddEdge(customerPort, readStep)
            .WithOutputFrom(wonStep, lostStep)
            .Build();
    }

    // Read: a RavenDB AI agent turns the email and its PO (round 1), or the customer's reply, into typed terms.
    private async ValueTask<DealRef> ReadAsync(DealRef input, IWorkflowContext context, CancellationToken cancellationToken)
    {
        using var session = _store.OpenAsyncSession();
        var deal = await session.LoadAsync<DealRequest>(input.DealId, cancellationToken);
        deal.Round++;
        deal.Status = DealStatus.Reading;
        Log(deal, $"round {deal.Round}: reading the {(deal.Round == 1 ? "request" : "reply")}");
        await session.SaveChangesAsync(cancellationToken);

        List<AIContent> contents;
        if (deal.Round == 1)
        {
            contents = [new TextContent($"Today is {DateTime.UtcNow:yyyy-MM-dd}. Request from {deal.CompanyName} ({deal.Company}):\n{deal.Email}")];

            // The customer's purchase order, an attachment of the deal, is copied into the conversation by the server:
            // the PDF never passes through the app.
            if (await session.Advanced.Attachments.ExistsAsync(deal.Id, "po.pdf", cancellationToken))
            {
                contents.Add(RavenAttachment.Copy(deal.Id!, "po.pdf"));
            }
        }
        else
        {
            contents = [new TextContent($"""
                Today is {DateTime.UtcNow:yyyy-MM-dd}. {deal.CompanyName} ({deal.Company}) asked for: {Describe(deal.Ask!)}
                We offered: {deal.Messages.Last(m => m.From == "desk").Text}
                The customer replied: {deal.Messages.Last(m => m.From == "customer").Text}
                """)];
        }

        var response = await _reader.RunAsync<DealAsk>([new ChatMessage(ChatRole.User, contents)], new RavenAIAgentSession(), cancellationToken);
        var ask = response.Result;

        // Prices and names come from the catalog, not from the model.
        var products = await session.LoadAsync<Product>(ask.Lines.Select(l => l.Product), cancellationToken);
        ask.Lines = [.. ask.Lines.Where(l => products.GetValueOrDefault(l.Product) is not null)];
        foreach (var line in ask.Lines)
        {
            line.ProductName = products[line.Product].Name;
            line.PricePerUnit = products[line.Product].PricePerUnit;
        }

        deal.Ask = ask;
        deal.Stock = null;
        deal.Customer = null;
        deal.Margin = null;
        deal.PastDeals = null;
        deal.Status = DealStatus.Checking;
        Log(deal, $"asks: {Describe(ask)}");
        await session.SaveChangesAsync(cancellationToken);
        return input;
    }

    // The four checks run in parallel; each one patches its own field of the deal, so the page fills in as they finish.
    private async ValueTask<CheckDone> CheckStockAsync(DealRef input, IWorkflowContext context, CancellationToken cancellationToken)
    {
        using var session = _store.OpenAsyncSession();
        var deal = await session.LoadAsync<DealRequest>(input.DealId, cancellationToken);
        var products = await session.LoadAsync<Product>(deal.Ask!.Lines.Select(l => l.Product), cancellationToken);

        List<StockLine> lines = [];
        foreach (var line in deal.Ask.Lines)
        {
            var product = products[line.Product];
            var isShort = line.Quantity > product.UnitsInStock;
            List<Substitute> substitutes = [];
            if (isShort)
            {
                // Substitutes: the same category, found by meaning, that we have enough of.
                var options = new VectorSearchOptions<ProductCard> { Filter = p => p.Category == product.Category };
                await foreach (var hit in _products.SearchAsync(product.Name, top: 6, options, cancellationToken))
                {
                    var candidate = await session.LoadAsync<Product>(hit.Record.ProductId, cancellationToken);
                    if (candidate.Id != product.Id && !candidate.Discontinued && candidate.UnitsInStock >= line.Quantity && substitutes.Count < 2)
                    {
                        substitutes.Add(new Substitute(candidate.Id!, candidate.Name, candidate.UnitsInStock, candidate.PricePerUnit));
                    }
                }
            }

            lines.Add(new StockLine(product.Name, line.Quantity, product.UnitsInStock, product.UnitsOnOrder, isShort, substitutes));
        }

        var shortLines = lines.Where(l => l.Short).ToList();
        await PatchAsync(input.DealId, d => d.Stock, new StockCheck(lines),
            shortLines.Count == 0 ? "stock: all lines in stock" : $"stock: short on {string.Join(", ", shortLines.Select(l => $"{l.ProductName} ({l.UnitsInStock} in stock)"))}",
            cancellationToken);
        return new CheckDone(input.DealId, "stock");
    }

    // Customer value comes from a map-reduce index over Northwind's orders; the rep and their manager from Employees.
    private async ValueTask<CheckDone> CheckCustomerAsync(DealRef input, IWorkflowContext context, CancellationToken cancellationToken)
    {
        using var session = _store.OpenAsyncSession();
        var deal = await session.LoadAsync<DealRequest>(input.DealId, cancellationToken);
        var value = await session.Query<Companies_Value.Result, Companies_Value>()
            .Customize(x => x.WaitForNonStaleResults())
            .FirstAsync(r => r.Company == deal.Company, cancellationToken);
        var rank = await session.Query<Companies_Value.Result, Companies_Value>().CountAsync(r => r.Revenue > value.Revenue, cancellationToken) + 1;

        var rep = await session.Include<Employee>(e => e.ReportsTo).LoadAsync<Employee>(value.LastEmployee, cancellationToken);
        var manager = await session.LoadAsync<Employee>(rep.ReportsTo ?? rep.Id, cancellationToken);

        var check = new CustomerCheck(value.Orders, value.Revenue, rank, value.Lines == 0 ? 0 : value.DiscountSum / value.Lines, value.LastOrderAt,
            rep.Id!, $"{rep.FirstName} {rep.LastName}", rep.Title, manager.Id!, $"{manager.FirstName} {manager.LastName}", manager.Title, value.LastShipVia);
        await PatchAsync(input.DealId, d => d.Customer, check,
            $"customer: #{rank} by revenue ({value.Revenue:C0} over {value.Orders} orders), rep {check.RepName} ({check.RepTitle})",
            cancellationToken);
        return new CheckDone(input.DealId, "customer");
    }

    // Margin is plain code: Northwind has no cost price, so cost is 70% of list (the pricing policy says the same).
    private async ValueTask<CheckDone> CheckMarginAsync(DealRef input, IWorkflowContext context, CancellationToken cancellationToken)
    {
        using var session = _store.OpenAsyncSession();
        var deal = await session.LoadAsync<DealRequest>(input.DealId, cancellationToken);
        var listTotal = deal.Ask!.Lines.Sum(l => l.Quantity * l.PricePerUnit);
        var cost = listTotal * 0.7m;

        List<MarginAt> levels = [];
        foreach (var discount in new[] { deal.Ask.Discount, 0.05m, 0.10m, 0.125m, 0.15m, 0.20m }.Distinct().Order())
        {
            var total = listTotal * (1 - discount);
            levels.Add(new MarginAt(discount, total, total == 0 ? 0 : (total - cost) / total));
        }

        var asked = levels.First(l => l.Discount == deal.Ask.Discount);
        await PatchAsync(input.DealId, d => d.Margin, new MarginCheck(listTotal, cost, levels),
            $"margin: {asked.Margin:P1} at the asked {asked.Discount:P1} ({asked.Total:C0} of {listTotal:C0} list)", cancellationToken);
        return new CheckDone(input.DealId, "margin");
    }

    // Past deals: vector search over Northwind's discounted orders and our closed deals.
    private async ValueTask<CheckDone> CheckPastDealsAsync(DealRef input, IWorkflowContext context, CancellationToken cancellationToken)
    {
        using var session = _store.OpenAsyncSession();
        var deal = await session.LoadAsync<DealRequest>(input.DealId, cancellationToken);

        List<PastDealHit> similar = [];
        await foreach (var hit in _pastDeals.SearchAsync(Setup.Summarize(deal.CompanyName, ToOrderLines(deal.Ask!, deal.Ask!.Discount)), top: 5, cancellationToken: cancellationToken))
        {
            similar.Add(new PastDealHit(hit.Record.Summary, hit.Record.Discount, hit.Record.Outcome, hit.Score ?? 0));
        }

        await PatchAsync(input.DealId, d => d.PastDeals, new PastDealsCheck(similar),
            $"past deals: {similar.Count} similar, closed at {string.Join(", ", similar.Select(s => s.Discount.ToString("P0")))}", cancellationToken);
        return new CheckDone(input.DealId, "past-deals");
    }

    // The fan-in barrier delivers the four results one by one in the same step: open the deal file on the fourth.
    [SendsMessage(typeof(List<ChatMessage>))]
    [SendsMessage(typeof(TurnToken))]
    private async ValueTask OpenDealFileAsync(CheckDone done, IWorkflowContext context, CancellationToken cancellationToken)
    {
        var checks = await context.ReadOrInitStateAsync("checks", () => new List<string>(), cancellationToken: cancellationToken);
        checks.Add(done.Check);
        await context.QueueStateUpdateAsync("checks", checks.Count < 4 ? checks : [], cancellationToken: cancellationToken);
        if (checks.Count < 4)
        {
            return;
        }

        using var session = _store.OpenAsyncSession();
        var deal = await session.LoadAsync<DealRequest>(done.DealId, cancellationToken);
        deal.Status = DealStatus.Debating;
        Log(deal, "debate: sales rep, finance and operations");
        await session.SaveChangesAsync(cancellationToken);

        // The group chat's messages don't carry the deal, so the ruling step finds it in the workflow's shared state.
        await context.QueueStateUpdateAsync("deal", done.DealId, DebateScope, cancellationToken);
        await context.SendMessageAsync(new List<ChatMessage> { new(ChatRole.User, DealFile(deal)) }, cancellationToken);
        await context.SendMessageAsync(new TurnToken(emitEvents: false), cancellationToken);
    }

    // Rule: the deal lead reads the deal file and the debate, follows the pricing-policy skill and answers typed.
    private async ValueTask<DealStep> RuleAsync(List<ChatMessage> debate, IWorkflowContext context, CancellationToken cancellationToken)
    {
        var dealId = await context.ReadStateAsync<string>("deal", DebateScope, cancellationToken) ?? throw new InvalidOperationException("No deal in the debate.");
        using var session = _store.OpenAsyncSession();
        var deal = await session.LoadAsync<DealRequest>(dealId, cancellationToken);

        // The round-robin manager gives the floor in order, after the deal file (the first message).
        deal.Debate = [.. debate.Skip(1).Select((message, i) => new DebateLine(_debaters[i % _debaters.Length].Name!, message.Text))];
        deal.Status = DealStatus.Deciding;
        Log(deal, "deal lead: ruling");
        await session.SaveChangesAsync(cancellationToken);

        var response = await _lead.RunAsync<Ruling>($"""
            {DealFile(deal)}

            The debate:
            {string.Join("\n", deal.Debate.Select(line => $"{line.Speaker}: {line.Text}"))}

            Today is {DateTime.UtcNow:yyyy-MM-dd}. This is round {deal.Round} of at most {MaxRounds}. Rule: accept, counter, escalate or decline.
            Your notes on this customer are in {NotesFile(deal.Company)}.
            """, cancellationToken: cancellationToken);
        var ruling = response.Result;
        ruling.Decision = ruling.Decision.Trim().ToLowerInvariant();
        ruling.Discount = Math.Min(ruling.Discount, deal.Ask!.Discount);   // never more than the customer asked for

        // The policy (data) sets the rep's authority; holding the deal to it is code.
        if (ruling.Decision == Decisions.Accept && ruling.Discount > ruling.RepAuthority)
        {
            ruling.Decision = Decisions.Escalate;
        }
        else if (ruling.Decision == Decisions.Counter && deal.Round >= MaxRounds)
        {
            ruling.Decision = Decisions.Decline;
        }

        deal.Ruling = ruling;
        Log(deal, $"ruling: {ruling.Decision} at {ruling.Discount:P1} (the rep may grant {ruling.RepAuthority:P1}): {ruling.Terms}");
        await session.SaveChangesAsync(cancellationToken);
        return new DealStep(dealId, ruling.Decision);
    }

    // Escalate: the rep's manager (Employees.ReportsTo) approves or rejects on the page; the run waits, checkpointed.
    private async ValueTask<DealStep> EscalateAsync(DealStep input, IWorkflowContext context, CancellationToken cancellationToken)
    {
        using var session = _store.OpenAsyncSession();
        var deal = await session.LoadAsync<DealRequest>(input.DealId, cancellationToken);
        var customer = deal.Customer!;
        deal.Escalation = new Escalation
        {
            Manager = customer.Manager,
            ManagerName = customer.ManagerName,
            ManagerTitle = customer.ManagerTitle,
            Discount = deal.Ruling!.Discount,
            Reason = $"{deal.Ruling.Discount:P1} is above the authority of {customer.RepName} ({customer.RepTitle}). {deal.Ruling.Reasoning}",
        };
        deal.Status = DealStatus.WaitingForManager;
        Log(deal, $"escalated to {customer.ManagerName} ({customer.ManagerTitle})");
        await session.SaveChangesAsync(cancellationToken);
        return input;
    }

    // Offer: the writer sends the counter-offer, and the run waits for the customer's reply.
    private async ValueTask<DealRef> OfferAsync(DealStep input, IWorkflowContext context, CancellationToken cancellationToken)
    {
        using var session = _store.OpenAsyncSession();
        var deal = await session.LoadAsync<DealRequest>(input.DealId, cancellationToken);
        var rejection = deal.Escalation is { Approved: false } escalation
            ? $" Our {escalation.ManagerTitle} could not approve {escalation.Discount:P1}: {escalation.Note}. Offer what the rep can grant instead."
            : string.Empty;
        deal.Escalation = null;

        var text = await WriteAsync(deal, $"Write the counter-offer for round {deal.Round}: {deal.Ruling!.Terms} ({deal.Ruling.Discount:P1} discount).{rejection} Ask which option they prefer.", cancellationToken);
        deal.Messages.Add(new DealMessage("desk", deal.Round, text));
        deal.Status = DealStatus.WaitingForCustomer;
        Log(deal, $"counter-offer sent (round {deal.Round})");
        await session.SaveChangesAsync(cancellationToken);
        return new DealRef(input.DealId);
    }

    // Won: a real Northwind order, the confirmation, and the deal joins the past deals.
    private async ValueTask<string> WinAsync(DealStep input, IWorkflowContext context, CancellationToken cancellationToken)
    {
        using var session = _store.OpenAsyncSession();
        var deal = await session.Include<DealRequest>(d => d.Company).LoadAsync<DealRequest>(input.DealId, cancellationToken);
        var company = await session.LoadAsync<Company>(deal.Company, cancellationToken);
        var discount = deal.Escalation?.Discount ?? deal.Ruling!.Discount;

        var order = new Order
        {
            Company = deal.Company,
            Employee = deal.Customer!.Rep,
            Lines = ToOrderLines(deal.Ask!, discount),
            OrderedAt = DateTime.UtcNow,
            RequireAt = DateTime.TryParse(deal.Ask!.RequiredBy, out var requiredBy) ? requiredBy : DateTime.UtcNow.AddDays(14),
            ShipTo = company.Address,
            ShipVia = deal.Customer.ShipVia,
        };
        order.Freight = Math.Round(order.Lines.Sum(l => l.Quantity * l.PricePerUnit) * 0.02m, 2);
        await session.StoreAsync(order, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);

        var text = await WriteAsync(deal, $"Confirm the order {order.Id}: {deal.Ruling!.Terms} ({discount:P1} discount), required by {order.RequireAt:d MMMM}.", cancellationToken);
        deal.Messages.Add(new DealMessage("desk", deal.Round, text));
        deal.OrderId = order.Id;
        deal.Status = DealStatus.Won;
        Log(deal, $"won: {order.Id}");
        await session.SaveChangesAsync(cancellationToken);

        await RememberAsync(deal, discount, "won", cancellationToken);
        return $"{deal.Id} won: {order.Id}";
    }

    private async ValueTask<string> LoseAsync(DealStep input, IWorkflowContext context, CancellationToken cancellationToken)
    {
        using var session = _store.OpenAsyncSession();
        var deal = await session.LoadAsync<DealRequest>(input.DealId, cancellationToken);
        var text = await WriteAsync(deal, $"Politely decline the deal, and say why: {deal.Ruling!.Reasoning}", cancellationToken);
        deal.Messages.Add(new DealMessage("desk", deal.Round, text));
        deal.Status = DealStatus.Lost;
        Log(deal, "lost");
        await session.SaveChangesAsync(cancellationToken);

        await RememberAsync(deal, deal.Ask!.Discount, "lost", cancellationToken);
        return $"{deal.Id} lost";
    }

    // The writer is hosted with its sessions in RavenDB, one per deal, so it remembers the whole negotiation; the
    // session key's company partition becomes the RavenDB agent's company parameter. The email's text streams to the
    // page as it is written.
    private async Task<string> WriteAsync(DealRequest deal, string instruction, CancellationToken cancellationToken)
    {
        var agent = (RavenAIAgent)_services.GetRequiredKeyedService<AIAgent>(WriterAgent);   // typed streaming needs the RavenAIAgent type
        var sessions = _services.GetRequiredKeyedService<AgentSessionStore>(WriterAgent);
        var key = new AgentSessionStoreKey(deal.Id!, new Dictionary<string, string> { ["company"] = deal.Company });
        var session = await sessions.GetOrCreateSessionAsync(agent, key, cancellationToken);

        var text = string.Empty;
        await foreach (var update in agent.RunStreamingAsync<Statement>(s => s.Text, instruction, session, cancellationToken))
        {
            text += update.Text;
            Drafting?.Invoke(new Draft(deal.Id!, text));
        }

        await sessions.SaveSessionAsync(agent, key, session, cancellationToken);
        return text;
    }

    private Task RememberAsync(DealRequest deal, decimal discount, string outcome, CancellationToken cancellationToken) =>
        _pastDeals.UpsertAsync(new PastDeal { DealKey = deal.Id!, Summary = Setup.Summarize(deal.CompanyName, ToOrderLines(deal.Ask!, discount)), Discount = discount, Outcome = outcome }, cancellationToken);

    private async Task PatchAsync<T>(string dealId, System.Linq.Expressions.Expression<Func<DealRequest, T>> field, T value, string entry, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[{dealId}] {entry}");
        using var session = _store.OpenAsyncSession();
        session.Advanced.Patch(dealId, field, value);
        session.Advanced.Patch<DealRequest, string>(dealId, d => d.Log, log => log.Add($"{DateTime.UtcNow:HH:mm:ss} {entry}"));
        await session.SaveChangesAsync(cancellationToken);
    }

    private static void Log(DealRequest deal, string entry)
    {
        Console.WriteLine($"[{deal.Id}] {entry}");
        deal.Log.Add($"{DateTime.UtcNow:HH:mm:ss} {entry}");
    }

    private static List<OrderLine> ToOrderLines(DealAsk ask, decimal discount) =>
        [.. ask.Lines.Select(l => new OrderLine { Product = l.Product, ProductName = l.ProductName, Quantity = l.Quantity, PricePerUnit = l.PricePerUnit, Discount = discount })];

    private static string Describe(DealAsk ask) =>
        $"{string.Join(", ", ask.Lines.Select(l => $"{l.Quantity} x {l.ProductName} ({l.Product}, {l.PricePerUnit:C2})"))}, " +
        $"by {ask.RequiredBy}, at {ask.Discount:P1} off";

    // What the debaters and the deal lead see.
    private static string DealFile(DealRequest deal)
    {
        var customer = deal.Customer!;
        return $"""
            Deal {deal.Id}, round {deal.Round}: {deal.CompanyName} ({deal.Company}) asks for {Describe(deal.Ask!)}.
            Stock: {string.Join("; ", deal.Stock!.Lines.Select(l => $"{l.ProductName}: {l.Quantity} asked, {l.UnitsInStock} in stock, {l.UnitsOnOrder} on order" +
                (l.Substitutes.Count == 0 ? string.Empty : $", substitutes in stock: {string.Join(", ", l.Substitutes.Select(s => $"{s.Name} ({s.Product}, {s.UnitsInStock} in stock, {s.PricePerUnit:C2})"))}")))}
            Customer: #{customer.Rank} by revenue, {customer.Revenue:C0} over {customer.Orders} orders, last order {customer.LastOrderAt:d MMM yyyy}, usual discount {customer.UsualDiscount:P1}.
            Sales rep: {customer.RepName} ({customer.RepTitle}), who reports to {customer.ManagerName} ({customer.ManagerTitle}).
            Margin (cost = 70% of list {deal.Margin!.ListTotal:C0}): {string.Join("; ", deal.Margin.Levels.Select(l => $"{l.Margin:P1} at {l.Discount:P1} off"))}.
            Similar past deals: {string.Join("; ", deal.PastDeals!.Similar.Select(s => $"{s.Summary} ({s.Outcome})"))}.
            {(deal.Messages.Count == 0 ? string.Empty : "Negotiation so far:\n" + string.Join("\n", deal.Messages.Select(m => $"{m.From} (round {m.Round}): {m.Text}")))}
            """;
    }
}

// Round-robin, with each turn labeled by its speaker: RavenDB AI agents get the others' turns as plain user prompts,
// so the speaker's name goes into the text the others see.
internal sealed class DebateManager(IReadOnlyList<AIAgent> agents) : RoundRobinGroupChatManager(agents)
{
    private readonly IReadOnlyList<AIAgent> _agents = agents;

    protected override ValueTask<IEnumerable<ChatMessage>> UpdateHistoryAsync(IReadOnlyList<ChatMessage> history, CancellationToken cancellationToken = default)
    {
        if (IterationCount == 0)
        {
            return new(history);   // the deal file
        }

        var speaker = _agents[(IterationCount - 1) % _agents.Count].Name!;
        return new(history.Select(m => new ChatMessage(ChatRole.User, $"{speaker}: {m.Text}")).ToList());
    }
}
