using System.ClientModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Operations.AI;
using Raven.Client.Documents.Operations.AI.Agents;
using Raven.Client.Documents.Operations.ConnectionStrings;
using Raven.Client.Documents.Operations.Revisions;
using Raven.Client.Documents.Subscriptions;
using Raven.Client.Exceptions;
using Raven.Client.Exceptions.Documents.Subscriptions;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Raven.Embedded;
using RavenDB.Agents.AI;
using RavenDB.Extensions.VectorData;

// Provisioning, done once per environment (like creating indexes): the database with Northwind, the index, revisions,
// the vector collections, the pricing policy, the RavenDB AI agents and their model connection, and the subscription.
internal static class Setup
{
    public const string Database = "DealDesk";
    public const string DealSubscription = "deal-desk";

    public static async Task<IDocumentStore> OpenDocumentStoreAsync()
    {
        // Vector search reports scores only when the database has this setting.
        var record = new DatabaseRecord(Database) { Settings = { ["Indexing.Corax.IncludeDocumentScore"] = "true" } };

        if (Environment.GetEnvironmentVariable("RAVEN_URL") is { Length: > 0 } url)
        {
            var store = new DocumentStore { Urls = [url], Database = Database }.Initialize();
            try
            {
                await store.Maintenance.Server.SendAsync(new CreateDatabaseOperation(record));
            }
            catch (ConcurrencyException)
            {
                // Already exists.
            }

            return store;
        }

        // No server configured: a licensed RavenDB in-process (AI agents are a licensed feature); data persists in ./RavenDB.
        var options = new ServerOptions { DataDirectory = Path.Combine(AppContext.BaseDirectory, "RavenDB") };
        options.Licensing.License = Environment.GetEnvironmentVariable("RAVEN_LICENSE") is { Length: > 0 } license ? license : null;
        options.Licensing.LicensePath = options.Licensing.License is null ? Environment.GetEnvironmentVariable("RAVEN_License_Path") : null;
        EmbeddedServer.Instance.StartServer(options);
        return await EmbeddedServer.Instance.GetDocumentStoreAsync(new DatabaseOptions(record));
    }

    public static async Task ProvisionAsync(IDocumentStore store)
    {
        // Studio's "Create sample data" (Northwind), which only works on an empty database.
        if ((await store.Maintenance.SendAsync(new GetStatisticsOperation())).CountOfDocuments == 0)
        {
            using var http = new HttpClient();
            (await http.PostAsync($"{store.Urls[0]}/databases/{store.Database}/studio/sample-data", null)).EnsureSuccessStatusCode();
            await SeedVectorsAsync(store);
        }

        await new Companies_Value().ExecuteAsync(store);

        // Every change to a deal is kept: the revisions are the negotiation's history.
        var databaseRecord = await store.Maintenance.Server.SendAsync(new GetDatabaseRecordOperation(store.Database));
        if (databaseRecord.Revisions?.Collections?.ContainsKey("DealRequests") != true)
        {
            var revisions = databaseRecord.Revisions ?? new RevisionsConfiguration();
            revisions.Collections ??= [];
            revisions.Collections["DealRequests"] = new RevisionsCollectionConfiguration();
            await store.Maintenance.SendAsync(new ConfigureRevisionsOperation(revisions));
        }

        // The pricing policy is a skill document: edited in Studio, used by the next ruling. Never overwritten here.
        using (var session = store.OpenAsyncSession())
        {
            if (!await session.Advanced.ExistsAsync("AgentSkills/pricing-policy"))
            {
                await new RavenAgentSkillsSource(store).SaveSkillAsync(PricingPolicy);
            }
        }

        await CreateRavenAgentsAsync(store);

        try
        {
            await store.Subscriptions.GetSubscriptionStateAsync(DealSubscription);
        }
        catch (SubscriptionDoesNotExistException)
        {
            // Deals that need work: new ones, and ones a customer or a manager just answered.
            await store.Subscriptions.CreateAsync(new SubscriptionCreationOptions
            {
                Name = DealSubscription,
                Query = $"from DealRequests where Status in ('{DealStatus.New}', '{DealStatus.CustomerReplied}', '{DealStatus.ManagerDecided}')",
            });
        }
    }

    // Products, to find substitutes by meaning; past deals (Northwind's discounted orders), to see what closed before.
    private static async Task SeedVectorsAsync(IDocumentStore store)
    {
        var vectors = new RavenVectorStore(store, new() { WaitForNonStaleResults = true });
        using var session = store.OpenAsyncSession();

        var products = await session.Query<Product>().Include(p => p.Category).Take(100).ToListAsync();
        List<ProductCard> cards = [];
        foreach (var product in products)
        {
            var category = await session.LoadAsync<Category>(product.Category);
            cards.Add(new ProductCard { ProductId = product.Id!, Category = product.Category, Text = $"{product.Name}: {category.Name}, {product.QuantityPerUnit}" });
        }

        await vectors.GetCollection<string, ProductCard>("ProductCards").UpsertAsync(cards);

        var orders = await session.Query<Order>().Where(o => o.Lines.Any(l => l.Discount > 0)).Include(o => o.Company).Take(200).ToListAsync();
        List<PastDeal> deals = [];
        foreach (var order in orders)
        {
            var company = await session.LoadAsync<Company>(order.Company);
            deals.Add(new PastDeal { DealKey = order.Id!, Summary = Summarize(company.Name, order.Lines), Discount = order.Lines.Max(l => l.Discount), Outcome = "won" });
        }

        await vectors.GetCollection<string, PastDeal>("PastDeals").UpsertAsync(deals);
    }

    public static string Summarize(string companyName, IEnumerable<OrderLine> lines) =>
        $"{companyName} ordered {string.Join(", ", lines.Select(l => $"{l.Quantity} x {l.ProductName}"))} " +
        $"for {lines.Sum(l => l.Quantity * l.PricePerUnit):C0} at {lines.Max(l => l.Discount):P0} off";

    private static readonly PersistedAgentSkill PricingPolicy = new()
    {
        Frontmatter = new AgentSkillFrontmatter("pricing-policy", "Northwind Traders' pricing policy: discount authority, margin floor, counter-offers and rounds."),
        Instructions = """
            Discount authority (the most a person may commit to on their own, by Northwind title):
            - Sales Representative: up to 5%
            - Sales Manager: up to 10%
            - Vice President, Sales: up to 20%
            The customer's sales rep is named in the deal file, with their title. Accepting a discount above the rep's
            authority needs the approval of the rep's manager (the deal desk escalates it).
            Never grant more discount than the customer asked for.

            Margin: Northwind's cost is 70% of the list price. Never go below a 20% margin
            (margin = (price after discount - cost) / price after discount), which is 12.5% discount at most.

            Counter-offers: when the asked discount breaks the margin floor, or a line is short of stock, counter instead of
            declining. Offer the best discount that keeps the margin floor. For each short line, offer two options: a split
            shipment (what is in stock on time, the rest when the stock on order arrives), or a named substitute that is in
            stock, all on time. A counter-offer is a proposal: it needs no approval.

            Rounds: at most 3 rounds of negotiation; in round 3, accept, escalate or decline.
            Decline when the customer insists on terms below the margin floor.
            """,
    };

    // The RavenDB AI agents. RavenDB calls the model through the connection string and runs their queries itself.
    private static async Task CreateRavenAgentsAsync(IDocumentStore store)
    {
        await store.Maintenance.SendAsync(new PutConnectionStringOperation<AiConnectionString>(CreateConnectionString()));

        // The sample object is the answer's schema, so every field gets a typed sample value.
        await store.AI.CreateAgentAsync(new AiAgentConfiguration(
            "Deal reader",
            "llm",
            "You read purchase requests that customers send to Northwind Traders: emails, attached purchase orders, and replies " +
            "to our offers. Turn them into the terms the customer asks for. Look every product up with find-products and use its " +
            "id. RequiredBy is a date (yyyy-MM-dd); Discount is a fraction (0.15 for 15%), 0 when none is asked. " +
            "For a reply to an offer, give the complete terms the customer now accepts or asks for, including substitutes.")
        {
            Identifier = DealDesk.ReaderAgent,
            Queries =
            [
                new AiAgentToolQuery("find-products", "Finds Northwind products by name",
                    "from Products where search(Name, $name) select id() as Product, Name, PricePerUnit, QuantityPerUnit, UnitsInStock")
                {
                    ParametersSampleObject = """{"name":"a product name, e.g. Chai"}""",
                },
            ],
        }, new DealAsk
        {
            Lines = [new AskedLine { Product = "products/1-A", ProductName = "Chai", Quantity = 10, PricePerUnit = 18 }],
            RequiredBy = "2026-10-20",
            Discount = 0.05m,
            Summary = "One sentence: who wants what, by when, at what discount",
        });

        await CreateDebaterAsync(store, DealDesk.SalesRepAgent, "Sales rep",
            "You are the customer's sales rep at Northwind Traders, in a deal review with finance and operations. You want to win " +
            "the deal and keep the customer: argue from the customer's value and history (look it up with customer-history).",
            new AiAgentToolQuery("customer-history", "The customer's order count, revenue and last order",
                "from index 'Companies/Value' where Company = $company")
            {
                ParametersSampleObject = """{"company":"the customer's company id, e.g. companies/20-A"}""",
            });

        await CreateDebaterAsync(store, DealDesk.FinanceAgent, "Finance",
            "You are finance at Northwind Traders, in a deal review with the sales rep and operations. You protect the margin: " +
            "argue from the margin check, and from the discounts this customer got before (look them up with recent-orders).",
            new AiAgentToolQuery("recent-orders", "The customer's five latest orders with their line discounts",
                "from Orders where Company = $company order by OrderedAt desc select OrderedAt, Lines limit 5")
            {
                ParametersSampleObject = """{"company":"the customer's company id, e.g. companies/20-A"}""",
            });

        await CreateDebaterAsync(store, DealDesk.OperationsAgent, "Operations",
            "You are operations at Northwind Traders, in a deal review with the sales rep and finance. You own stock and delivery: " +
            "say what we can ship by the date, and propose substitutes or a split shipment for short lines (look a category's " +
            "stock up with category-stock).",
            new AiAgentToolQuery("category-stock", "Products of a category that are in stock",
                "from Products where Category = $category and Discontinued = false order by UnitsInStock desc " +
                "select id() as Product, Name, UnitsInStock, UnitsOnOrder, PricePerUnit limit 8")
            {
                ParametersSampleObject = """{"category":"a category id, e.g. categories/1-A"}""",
            });

        // The writer's conversation is per deal (a hosted session); its company parameter comes from the session key.
        await store.AI.CreateAgentAsync(new AiAgentConfiguration(
            "Deal writer",
            "llm",
            "You write the emails Northwind Traders' deal desk sends to a customer during a negotiation: offers, confirmations " +
            "and declines. Address the customer's contact by name (look it up with customer-contact). Be warm and concise: at " +
            "most 120 words, no subject line, signed 'Northwind Traders deal desk'.")
        {
            Identifier = DealDesk.WriterAgent,
            Parameters = [new AiAgentParameter("company", "The id of the customer's company")],
            Queries =
            [
                new AiAgentToolQuery("customer-contact", "The customer's name and contact person",
                    "from Companies where id() = $company select Name, Contact")
                {
                    ParametersSampleObject = "{}",
                },
            ],
        }, new Statement { Text = "The email" });
    }

    private static Task CreateDebaterAsync(IDocumentStore store, string id, string name, string role, AiAgentToolQuery query) =>
        store.AI.CreateAgentAsync(new AiAgentConfiguration(name, "llm",
            role + " React to what the others said and propose concrete terms. Speak only for yourself, in one paragraph of " +
            "at most three sentences.")
        {
            Identifier = id,
            Queries = [query],
        }, new Statement { Text = "What you say" });

    public static IChatClient CreateChatClient()
    {
        if (Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT") is { Length: > 0 } endpoint)
        {
            var baseUri = new Uri(new Uri(endpoint).GetLeftPart(UriPartial.Authority) + "/openai/v1/");
            return new OpenAIClient(new ApiKeyCredential(Required("AZURE_OPENAI_API_KEY")), new OpenAIClientOptions { Endpoint = baseUri })
                .GetChatClient(Required("AZURE_OPENAI_DEPLOYMENT"))
                .AsIChatClient();
        }

        return new OpenAIClient(Required("OPENAI_API_KEY"))
            .GetChatClient(Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4.1-mini")
            .AsIChatClient();
    }

    private static AiConnectionString CreateConnectionString()
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
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Set {name}.");
}

// Customer value: a map-reduce index over Northwind's orders, per company.
internal sealed class Companies_Value : AbstractIndexCreationTask<Order, Companies_Value.Result>
{
    public sealed class Result
    {
        public string Company { get; set; } = string.Empty;
        public int Orders { get; set; }
        public decimal Revenue { get; set; }
        public decimal DiscountSum { get; set; }
        public int Lines { get; set; }
        public DateTime LastOrderAt { get; set; }
        public string LastEmployee { get; set; } = string.Empty;
        public string LastShipVia { get; set; } = string.Empty;
    }

    public Companies_Value()
    {
        Map = orders => from order in orders
                        select new Result
                        {
                            Company = order.Company,
                            Orders = 1,
                            Revenue = order.Lines.Sum(l => l.Quantity * l.PricePerUnit * (1 - l.Discount)),
                            DiscountSum = order.Lines.Sum(l => l.Discount),
                            Lines = order.Lines.Count,
                            LastOrderAt = order.OrderedAt,
                            LastEmployee = order.Employee,
                            LastShipVia = order.ShipVia,
                        };

        Reduce = results => from result in results
                            group result by result.Company into g
                            let last = g.OrderByDescending(r => r.LastOrderAt).First()
                            select new Result
                            {
                                Company = g.Key,
                                Orders = g.Sum(r => r.Orders),
                                Revenue = g.Sum(r => r.Revenue),
                                DiscountSum = g.Sum(r => r.DiscountSum),
                                Lines = g.Sum(r => r.Lines),
                                LastOrderAt = last.LastOrderAt,
                                LastEmployee = last.LastEmployee,
                                LastShipVia = last.LastShipVia,
                            };
    }
}
