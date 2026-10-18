# RavenDB.Agents.AI

RavenDB integration for [Microsoft Agent Framework](https://github.com/microsoft/agent-framework) (MAF) 1.25 for .NET.
Each class implements one of MAF's own extension points, so it works anywhere MAF expects that abstraction.

| MAF extension point | RavenDB implementation |
|---|---|
| `ChatHistoryProvider` | `RavenChatHistoryProvider` |
| `AgentSessionStore` (+ hosting) | `RavenAgentSessionStore`, `WithRavenSessionStore()` |
| `AIAgent` | `RavenAIAgent` (a RavenDB server-side AI Agent) |
| `VectorStore` (Microsoft.Extensions.VectorData) | `RavenVectorStore`, `RavenCollection<TKey,TRecord>`, `RavenDynamicCollection` (separate package, `RavenDB.Extensions.VectorData`) |
| `TextSearchProvider` search function | `RavenDocumentSearch` |
| `JsonCheckpointStore` | `RavenCheckpointStore` |
| `AgentFileStore` | `RavenAgentFileStore` |
| `AgentSkillsSource` | `RavenAgentSkillsSource` |

## Quick start

```csharp
using var store = new DocumentStore { Urls = ["http://localhost:8080"], Database = "AgentsDemo" }.Initialize();

var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    Name = "support",
    ChatHistoryProvider = new RavenChatHistoryProvider(store),             // one document per conversation
    AIContextProviders =
    [
        // Long-term memory: MAF's own provider on the RavenDB vector store; RavenDB embeds the text itself.
        new ChatHistoryMemoryProvider(new RavenVectorStore(store), "ChatMemories", vectorDimensions: 384,
            _ => new ChatHistoryMemoryProvider.State(new ChatHistoryMemoryProviderScope { UserId = "users/1-A" })),

        // RAG over an existing collection.
        new RavenDocumentSearch(store, new() { Collection = "Articles", TextField = "Body", TitleField = "Title" })
            .CreateTextSearchProvider(),
    ],
});

// Sessions survive restarts: the same key resumes the same conversation.
var sessions = new RavenAgentSessionStore(store);
var key = new AgentSessionStoreKey("support-chat");
var session = await sessions.GetOrCreateSessionAsync(agent, key);
Console.WriteLine(await agent.RunAsync("How long do I have to return an item?", session));
await sessions.SaveSessionAsync(agent, key, session);
```

```bash
# Runnable version: embedded RavenDB unless RAVEN_URL is set; OPENAI_API_KEY (+ OPENAI_MODEL)
# or AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT. Run it twice to see the conversation resume.
dotnet run --project samples/RavenDB.Agents.AI.Sample
```

## Full example: the Northwind deal desk

[samples/RavenDB.Agents.AI.DealDesk](samples/RavenDB.Agents.AI.DealDesk) is a web app where agents negotiate customer
orders on RavenDB's Northwind sample data. The flow isn't a straight line: four checks run in parallel, the team argues,
offers loop back to the customer, and a person is asked only when a discount is above the sales rep's authority.

```text
DealRequests/n (email + PO.pdf attachment) -> data subscription -> MAF workflow, one run per deal, RavenCheckpointStore
  read        RavenAIAgent.RunAsync<DealAsk>, find-products query tool; the PO (an attachment of the deal) is copied
              into the conversation on the server with RavenAttachment.Copy
  checks      in parallel (AddFanOutEdge / AddFanInBarrierEdge): stock + substitutes (vector search on ProductCards),
              customer value (map-reduce index Companies/Value), margin (plain code), past deals (vector search)
  debate      MAF group chat sub-workflow: sales rep, finance, operations, each a RavenAIAgent with its own query tool
  rule        ChatClientAgent with the pricing-policy skill (RavenAgentSkillsSource) and its notes per customer
              (FileMemoryProvider on RavenAgentFileStore) -> typed Ruling
  counter     the writer (RavenAIAgent hosted with WithRavenSessionStore, session per deal) streams the offer to the page
              (RunStreamingAsync<Statement>(s => s.Text)); RequestPort waits for the customer; the reply resumes the run
              from its checkpoint -> read again (up to 3 rounds)
  escalate    above the rep's authority: RequestPort for the rep's manager (Employees.ReportsTo)
  won         a real Orders/... document; the deal joins the past deals
The page follows every deal live: RavenDB Changes API -> Server-Sent Events. Revisions on DealRequests = the negotiation.
```

```bash
# Licensed RavenDB (RAVEN_URL, or RAVEN_LICENSE for the embedded server) and the OpenAI variables above.
dotnet run --project samples/RavenDB.Agents.AI.DealDesk    # open the URL it prints; delete its bin/.../RavenDB folder to start over
```

Demo script (about 6 minutes):

1. **Easy deal.** Click *Easy deal*, then *Send*: Alfreds Futterkiste asks 3% off coffee and chocolate. The card moves
   through the lanes; the four checks fill in at the same time; the team debates; the deal lead accepts (3% is within the
   rep's 5%) and an `Orders/...` document appears in the *Won* lane.
2. **The negotiation.** Click *Big negotiation* (Ernst Handel, with the PDF purchase order attached), then *Send*. The
   reader takes the lines from the PDF: 25 Tarte au sucre and 20 Schoggi Schokolade by the 20th at 15% off. The checks
   find Schoggi short (11 in stock), Ernst Handel the #2 customer, 15% below the 20% margin floor, similar deals closed
   at 10-20%. After the debate the lead counters at 12.5%: a split shipment, or an in-stock substitute. Reply as the
   customer (*Suggest: take the substitute*). Round 2: 12.5% is above the rep's 5%, so it waits for the rep's manager,
   Andrew Fuller (from `Employees.ReportsTo`). *Approve*: the deal is won and the order is created.
3. **Policy is data.** In Studio, open `AgentSkills/pricing-policy` and change "Sales Representative: up to 5%" to 2%.
   Send the easy deal again: now it escalates. No redeploy, no restart.
4. **Studio.** The deal's revisions (the negotiation step by step), the new order, the agents' conversations
   (`@conversations`, the reader's with `po.pdf` attached), the writer's session, the deal lead's notes
   (`AgentFiles/deal-lead/companies-20-A.md`), the workflow checkpoints and the `deal-desk` subscription.

## Chat history

For agents that call the model themselves (`ChatClientAgent`, ...). A `RavenAIAgent` keeps its conversation in RavenDB and
uses no history provider.

```csharp
var history = new RavenChatHistoryProvider(store, new RavenChatHistoryProviderOptions
{
    // Bind a new session to a known conversation; by default each session gets a new id.
    StateInitializer = _ => new RavenChatHistoryProvider.State("tickets/42"),        // -> ChatConversations/tickets/42

    // Store only the real conversation, not the memories and RAG snippets that context providers re-inject every turn.
    StoreInputRequestMessageFilter = messages => messages.Where(m => m.GetAgentRequestMessageSourceType() == AgentRequestMessageSourceType.External),

    TimeToLive = TimeSpan.FromDays(30),       // counted from when the conversation is created; needs Expiration enabled
    DocumentIdPrefix = "ChatConversations",   // default
});

// Every message round-trips as MAF produced it: text, tool calls and results, images, reasoning.
// Appends are server-side JSON Patches, so concurrent runs never lose messages.
using var s = store.OpenAsyncSession();
ChatConversation conversation = await s.LoadAsync<ChatConversation>("ChatConversations/tickets/42");
List<ChatMessage> messages = conversation.Messages;
```

## Sessions

```csharp
var sessions = new RavenAgentSessionStore(store, new RavenAgentSessionStoreOptions
{
    AgentNamespace = "support",               // default: the agent's name, then its id
});

// Any number of partitions; the document id is AgentSessions/{agent}/{sessionId}/{name}/{value}..., names ordered.
var key = new AgentSessionStoreKey("c-123", new Dictionary<string, string> { ["company"] = "companies/1" });

AgentSession session = await sessions.GetOrCreateSessionAsync(agent, key);   // AgentSessions/support/c-123/company/companies/1
await sessions.SaveSessionAsync(agent, key, session);                        // stored as the agent serializes it
AgentSession? existing = await sessions.GetSessionAsync(agent, key);         // null when missing
await sessions.DeleteSessionAsync(agent, key);
```

### Hosting

```csharp
builder.Services.AddSingleton<IDocumentStore>(store);
builder.Services.AddSingleton<AgentIsolationKeyProvider, SignedInUserKeyProvider>();   // your provider: user, tenant, ...
builder.AddAIAgent("support", "You are a support agent.").WithRavenSessionStore();

// Isolation is on by default and is MAF's: the provider's key is added to every session key (as "isolation"),
// so a caller who knows another user's session id can't load it. Without a registered provider every load and save throws.
// Single-user or development host:
builder.AddAIAgent("dev", "...").WithRavenSessionStore(withIsolation: false);
```

## RavenDB AI Agents

`RavenAIAgent` runs a RavenDB AI Agent as a MAF agent, so it joins MAF workflows, hosting and orchestration. The agent
itself is RavenDB configuration, created once (in Studio, or from a deploy step like an index); RavenDB calls the model.

```csharp
// 1. The model connection lives in RavenDB; the app never passes the model key to RavenAIAgent.
await store.Maintenance.SendAsync(new PutConnectionStringOperation<AiConnectionString>(new AiConnectionString
{
    Name = "llm",
    Identifier = "llm",
    ModelType = AiModelType.Chat,
    OpenAiSettings = new OpenAiSettings(apiKey, "https://api.openai.com/v1", "gpt-4.1-mini", null, null, null, null, null, null),
    // or AzureOpenAiSettings = new AzureOpenAiSettings(apiKey, endpoint, deployment, deployment, dimensions: null, temperature: null),
}));

// 2. The agent: prompt, parameters, server-side queries (fixed RQL) and actions the client answers.
//    A client-side action is defined once, as a MAF tool: ToAgentToolAction() gives RavenDB its name, description and
//    parameters' JSON schema, and the same tool runs in place when the agent requests it.
var sendEmail = AIFunctionFactory.Create(SendEmail, "send-email", "Sends an email to the customer");

var configuration = new AiAgentConfiguration("Store support", "llm", "You are a support agent...")
{
    Identifier = "support-agent",
    Parameters = [new AiAgentParameter("company", "The id of the customer's company")],
    Queries = [new AiAgentToolQuery("get-orders", "Lists the customer's orders", "from Orders where Company = $company") { ParametersSampleObject = "{}" }],
    Actions =
    [
        sendEmail.ToAgentToolAction(),
        new AiAgentToolAction("issue-refund", "Refunds an order") { ParametersSampleObject = """{"orderId":"","reason":""}""" },   // answered by the caller
    ],
};
await store.AI.CreateAgentAsync(configuration, new Reply { Answer = "Your answer to the customer" });   // the answer shape (RunAsync<Reply> reads it typed)
```

```csharp
// 3. The MAF side. From the configuration: id = Identifier, name = Name. Actions passed here run in place; every one
//    must be one of the agent's actions (ArgumentException otherwise: the RavenDB agent would never request it).
//    Other actions come back as function calls.
var agent = configuration.ToRavenAIAgent(store, [sendEmail], new RavenAIAgentOptions
{
    Name = "support",                                              // optional: the MAF name (e.g. a hosting registration name)
    ConversationIdPrefix = "Chats",                                // new conversations are Chats/{guid}
});
// Or by id, when the configuration isn't at hand: new RavenAIAgent(store, "support-agent", [sendEmail], options).

// The agent's parameters belong to the conversation: RavenDB binds them when it creates it.
var session = new RavenAIAgentSession { Parameters = { ["company"] = "companies/1" } };
var response = await agent.RunAsync("Where is my order?", session);

// Actions without a matching tool come back as function calls; answer them in the next run.
foreach (var call in response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>())
{
    var result = await AskHumanAsync(call);                                     // e.g. approve a refund
    response = await agent.RunAsync(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, result)]), session);
}

// Plain-text streaming.
await foreach (var update in agent.RunStreamingAsync("Summarize my orders", session))
    Console.Write(update.Text);
```

Typed answers: `TAnswer` must match the answer shape in the RavenDB agent's configuration.

```csharp
AgentResponse<OrderSummary> summary = await agent.RunAsync<OrderSummary>("Summarize my orders", session);
OrderSummary result = summary.Result;

// Stream one string property of the typed answer; the last update's RawRepresentation is the full answer.
await foreach (var update in agent.RunStreamingAsync<Reply>(reply => reply.Answer, "Where is my order?", session))
    Console.Write(update.Text);

// MAF's own AIAgent.RunAsync<T> doesn't pass T to the agent, so through an AIAgent variable it throws NotSupportedException.
AIAgent asMaf = agent;
await asMaf.RunAsync<OrderSummary>("...", session);   // NotSupportedException: use RavenAIAgent.RunAsync<TAnswer>
```

Files: `DataContent` in a user message becomes an attachment of the conversation document, sent to the model with that turn.

```csharp
// Images (png, jpeg, gif, webp), PDFs and text/plain; other media types fail the run (AiException).
// The model sees the names (unnamed content gets a generated one); a name sent again replaces that attachment.
var photo = await DataContent.LoadFromAsync("damage.jpg");                     // Name = "damage.jpg", image/jpeg
var form = new DataContent(pdfBytes, "application/pdf") { Name = "claim-form.pdf" };
var reply = await agent.RunAsync(new ChatMessage(ChatRole.User, [new TextContent("Does the photo match the claim form?"), photo, form]), session);

// A file already stored in RavenDB (an attachment of Claims/1) is copied into the conversation on the server
// (CopyAttachmentFrom): the bytes never pass through the app, and the name stays the same.
var stored = RavenAttachment.Copy("Claims/1", "damage.jpg");                 // MAF's HostedFileContent: serializes anywhere MAF stores messages
reply = await agent.RunAsync(new ChatMessage(ChatRole.User, [new TextContent("Is the damage in the photo covered?"), stored]), session);
```

Sessions from a `RavenAgentSessionStore` (and so hosted sessions) get the key's partitions as default parameters:

```csharp
// Starts its conversation with company = companies/1 (and isolation = the signed-in user when hosted).
// Declare a parameter named "isolation" on the RavenDB agent to scope its queries to that user.
var session = await sessions.GetOrCreateSessionAsync(agent,
    new AgentSessionStoreKey("c-123", new Dictionary<string, string> { ["company"] = "companies/1" }));
((RavenAIAgentSession)session).ConversationId ??= "Chats/tickets/42";   // optional: bind a known conversation
```

To let a model query the database, give the RavenDB agent query tools: fixed RQL with parameters bound from the session
(`from Orders where Company = $company`), never written by the model. With other agents, write narrow `AIFunction`s.

Handoff workflows (`AgentWorkflowBuilder.CreateHandoffBuilderWith`): a `RavenAIAgent` can be a handoff target, but it
can't hand off. MAF offers each agent its `handoff_to_N` tools per run (in `ChatClientAgentRunOptions`), while a RavenDB
agent's tools come only from its configuration; RavenDB has no per-run actions yet.

```bash
# RavenDB AI agent with a server-side query, a client-side send-email tool and a human-approved issue-refund action.
# Needs a licensed RavenDB (RAVEN_URL, or RAVEN_LICENSE for the embedded server) and the OpenAI variables above.
dotnet run --project samples/RavenDB.Agents.AI.AgentSample
```

## Vector store (Microsoft.Extensions.VectorData)

Package `RavenDB.Extensions.VectorData` (namespace of the same name). It depends only on Microsoft.Extensions.VectorData
and the RavenDB client, so it also works with Semantic Kernel or any other MEVD consumer, without MAF.

```csharp
public sealed class Hotel
{
    [VectorStoreKey] public string HotelId { get; set; } = "";             // no property named Id (see below)
    [VectorStoreData(IsIndexed = true)] public string? Name { get; set; }
    [VectorStoreData(IsIndexed = true)] public int Rating { get; set; }
    [VectorStoreData] public string[] Tags { get; set; } = [];
    [VectorStoreData(IsFullTextIndexed = true)] public string? Summary { get; set; }   // keyword half of hybrid search
    [VectorStoreVector(1536)] public float[]? Embedding { get; set; }       // your own vectors (or ReadOnlyMemory<float>, Embedding<float>)
    [VectorStoreVector(384)] public string? Description { get; set; }       // or text that RavenDB embeds itself
}

var vectorStore = new RavenVectorStore(store, new RavenVectorStoreOptions
{
    EmbeddingsTaskIdentifier = "openai-embeddings",   // optional: an embeddings generation task instead of the built-in model
    WaitForNonStaleResults = true,                    // read your own writes in filtered gets and searches
});

var hotels = vectorStore.GetCollection<string, Hotel>("Hotels");
await hotels.EnsureCollectionExistsAsync();
await hotels.UpsertAsync(new Hotel { HotelId = "h1", Name = "Seaside", Rating = 5, Tags = ["pool"], Embedding = vector });  // Hotels/h1
Hotel? h1 = await hotels.GetAsync("h1");

await foreach (var hit in hotels.SearchAsync(queryVector, top: 5, new() { Filter = h => h.Rating >= 4 && h.Tags.Contains("pool") }))
    Console.WriteLine($"{hit.Record.Name} {hit.Score}");

await foreach (var hit in hotels.SearchAsync("quiet place by the sea", top: 5, new() { VectorProperty = h => h.Description }))
    Console.WriteLine(hit.Record.Name);

await foreach (var hotel in hotels.GetAsync(h => h.Rating >= 4, top: 10, new() { OrderBy = o => o.Descending(h => h.Rating) }))
    Console.WriteLine(hotel.Name);

// Hybrid: every vector match, with hotels whose Summary mentions the keywords ranked first (RavenDB adds the two scores).
var hybrid = (IKeywordHybridSearchable<Hotel>)hotels;
await foreach (var hit in hybrid.HybridSearchAsync(queryVector, ["pool", "spa"], top: 5, new() { Filter = h => h.Rating >= 4 }))
    Console.WriteLine($"{hit.Record.Name} {hit.Score}");
```

```csharp
// Scores: RavenDB reports them (cosine similarity) only when the database has this setting; otherwise Score is null.
await store.Maintenance.Server.SendAsync(new CreateDatabaseOperation(new DatabaseRecord("AgentsDemo")
{
    Settings = { ["Indexing.Corax.IncludeDocumentScore"] = "true" },
}));

// Collection options: unset values use the database's own vector search settings.
var tuned = new RavenCollection<string, Hotel>(store, "Hotels", new RavenCollectionOptions
{
    NumberOfCandidates = 100,    // default: the database's (16), which caps the results
    ExactSearch = true,          // brute force instead of HNSW; IndexKind.Flat properties are always exact
});

// Records defined at runtime (what MAF's ChatHistoryMemoryProvider uses).
var notes = vectorStore.GetDynamicCollection("Notes", new VectorStoreCollectionDefinition
{
    Properties =
    [
        new VectorStoreKeyProperty("Key", typeof(string)),
        new VectorStoreDataProperty("Owner", typeof(string)) { IsIndexed = true },
        new VectorStoreVectorProperty("Text", typeof(string), 384),
    ],
});
await notes.UpsertAsync(new Dictionary<string, object?> { ["Key"] = "n1", ["Owner"] = "alice", ["Text"] = "Buy milk" });

// Dependency injection: both use the IDocumentStore registered in the container. A collection is also registered as
// VectorStoreCollection<,>, IVectorSearchable<> and IKeywordHybridSearchable<>; an app-wide IEmbeddingGenerator is not used.
services.AddSingleton(store);
services.AddRavenVectorStore(new RavenVectorStoreOptions { WaitForNonStaleResults = true });
services.AddRavenCollection<string, Hotel>("Hotels");
services.AddKeyedRavenCollection<string, Hotel>("archive", "ArchivedHotels", lifetime: ServiceLifetime.Scoped);
```

RavenDB's native behavior, with no mapping layer:

```csharp
// Ids:      {collection}/{key} for any key type; keys are case-insensitive ("H1" and "h1" are one record).
// Id:       record types with an Id property are rejected (RavenDB maps it to the document id).
// Vectors:  float[], ReadOnlyMemory<float>, Embedding<float> (stored as JSON arrays) or string (embedded by RavenDB);
//           ReadOnlyMemory<float> and Embedding<float> need store.UseVectorData() before Initialize() (RavenDB's serializer
//           can't write them otherwise; collections throw a clear error); client-side embedding generators are rejected.
// Names:    field names are property names; rename with [JsonProperty("...")] (RavenDB honors it when storing and
//           querying). MEVD's StorageName is rejected.
// Filters:  RavenDB LINQ as-is, exact (case-sensitive): ==, !=, ranges, &&, ||, !, x.Tags.Contains(v), _ => true.
//           "One of these values": x.F.In(values); "array has any of these": x.Tags.ContainsAny(values)
//           (Raven.Client.Documents.Linq). values.Contains(x.F) and x.Tags.Any(t => values.Contains(t)) throw.
// Results:  stored vectors only with IncludeVectors = true (MEVD's default is false: RavenDB leaves them out, the query
//           selects every other field); text RavenDB embeds always comes back; dynamic records hold the definition's types.
// Search:   ScoreThreshold / NumberOfCandidates unset -> the database's settings (no minimum, 16 candidates by default).
// Writes:   one UpsertAsync / DeleteAsync = one transaction; gets by key are ACID, filtered gets and searches use auto-indexes.
```

## RAG over a collection

```csharp
var search = new RavenDocumentSearch(store, new RavenDocumentSearchOptions
{
    Collection = "Articles",
    TextField = "Body",                       // RavenDB embeds it; field names come from you, not from the model
    TitleField = "Title",                     // source name; default: the document id
    MaxResults = 3,                           // default
    MinimumSimilarity = 0.5f,                 // default: the database's setting
    NumberOfCandidates = 50,                  // default: the database's (16)
    EmbeddingsTaskIdentifier = "openai-embeddings",
});

AIContextProvider rag = search.CreateTextSearchProvider();       // add to AIContextProviders
var hits = await search.SearchAsync("Can I get my money back?"); // or call it yourself
```

## Workflow checkpoints

```csharp
var checkpointStore = new RavenCheckpointStore(store);   // WorkflowCheckpoints/{sessionId}/{time-ordered id}
// The payload is the Brotli-compressed value.json.br attachment (MAF checkpoints repeat a lot: ~70x smaller);
// the document keeps SessionId, CheckpointId, ParentCheckpointId and CreatedAt.
var checkpoints = CheckpointManager.CreateJson(checkpointStore);

await using var run = await InProcessExecution.RunAsync(workflow, input, checkpoints, "order-1234");

// Later, after a restart: resume from the latest checkpoint (the index is oldest first).
var last = (await checkpointStore.RetrieveIndexAsync("order-1234")).Last();
await using var resumed = await InProcessExecution.ResumeAsync(workflow, last, checkpoints);
```

## Files

```csharp
var files = new RavenAgentFileStore(store, root: "user-1");      // one document per file: AgentFiles/user-1/notes/todo.md (a root has no "/")
var fileMemory = new FileMemoryProvider(files);                  // or new FileAccessProvider(files)

await files.WriteAsync("notes/todo.md", "- call the bank");
string? todo = await files.ReadAsync("notes/todo.md");
```

## Skills

```csharp
var skills = new RavenAgentSkillsSource(store);   // AgentSkills/{name}

// Authored as documents, rolled out without redeploys. The frontmatter is MAF's own class (Agent Skills spec).
await skills.SaveSkillAsync(new PersistedAgentSkill
{
    Frontmatter = new AgentSkillFrontmatter("refund-policy", "How to handle refund requests.") { License = "MIT" },
    Instructions = "Refunds are allowed within 30 days. Read the 'limits' resource for amounts.",
    Resources = [new PersistedAgentSkillResource { Name = "limits", Content = "Max refund: $500" }],
    Disabled = false,                             // true hides it from agents
});

var provider = new AgentSkillsProviderBuilder().UseSource(skills).Build();   // add to AIContextProviders
// A stored skill whose frontmatter breaks the spec (e.g. edited in Studio) fails the load.
```

## Documents

| Class | Collection | Document id |
|---|---|---|
| `RavenChatHistoryProvider` | `@ChatConversations` | `ChatConversations/{conversationId}` |
| `RavenAgentSessionStore` | `@AgentSessions` | `AgentSessions/{agent}/{sessionId}[/{partition}/{value}...]` |
| `RavenAIAgent` | `@conversations` (RavenDB AI conversations) | `Chats/{guid}`, or the id you set |
| `RavenCollection<TKey,TRecord>` | `{collection}` | `{collection}/{key}` (+ `VectorStoreCollections/{collection}` marker in `@VectorStoreCollections`) |
| `RavenCheckpointStore` | `@WorkflowCheckpoints` | `WorkflowCheckpoints/{sessionId}/{time-ordered id}` (payload: `value.json.br` attachment) |
| `RavenAgentFileStore` | `@AgentFiles` | `AgentFiles/{root}/{path}` |
| `RavenAgentSkillsSource` | `@AgentSkills` | `AgentSkills/{name}` |

```csharp
// The collections are set explicitly, so query them by name (the class name alone points to another collection):
var sessions = await s.Query<PersistedAgentSession>(collectionName: PersistedAgentSession.CollectionName).ToListAsync();

// Every options class has Database (null = the store's default database).
// The classes that own their documents also have DocumentIdPrefix and TimeToLive (needs the Expiration feature).
new RavenCheckpointStore(store, new RavenCheckpointStoreOptions { Database = "Workflows", DocumentIdPrefix = "Checkpoints", TimeToLive = TimeSpan.FromDays(7) });

// Framework payloads (chat messages, sessions) are stored in their System.Text.Json shape,
// readable and queryable in Studio (checkpoint values are compressed attachments). Content loaded back (history,
// memories, search hits) reaches the model as-is,
// so treat those collections as trusted input.
```

## Build and test

```bash
dotnet test RavenDB.Agents.AI.slnx                                                # both libraries' tests
dotnet test --project tests/RavenDB.Agents.AI/RavenDB.Agents.AI.Tests
dotnet test --project tests/RavenDB.Extensions.VectorData/RavenDB.Extensions.VectorData.Tests

# Microsoft's MEVD conformance suite against RavenDB (also in the solution run; 12 tests currently fail on RavenDB limitations)
dotnet run --project tests/RavenDB.Extensions.VectorData/RavenDB.Extensions.VectorData.ConformanceTests
```

```bash
# Tests use an embedded RavenDB (RavenDB.TestDriver). Live RavenAIAgent tests also need, or they skip:
# RAVEN_LICENSE (or RAVEN_License_Path), plus RAVEN_AI_INTEGRATION_AZURE_OPENAI_API_KEY,
# RAVEN_AI_INTEGRATION_AZURE_OPENAI_DEPLOYMENT_ENDPOINT and RAVEN_AI_INTEGRATION_AZURE_OPENAI_CHAT_DEPLOYMENT_NAME,
# or RAVEN_AI_INTEGRATION_OPENAI_API_KEY.
# On Windows, keep the clone path short: under a deep path the embedded server drops the connection when it runs
# its built-in embedding model (String_vectors_are_embedded_by_ravendb_when_no_generator_is_configured fails).
```
