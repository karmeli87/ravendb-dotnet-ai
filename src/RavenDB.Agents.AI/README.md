# RavenDB.Agents.AI

RavenDB for [Microsoft Agent Framework](https://github.com/microsoft/agent-framework) agents (1.25, .NET 8+). Keep your
agents' conversations, sessions, workflow checkpoints, files and skills in RavenDB, give them your documents to answer
from, or run a RavenDB AI agent as an Agent Framework agent.

Each class plugs into one of the Agent Framework's own extension points, so it works wherever the framework expects it.

| You want to... | Use | In place of the framework's |
|---|---|---|
| Run a RavenDB AI agent in workflows and hosting | `RavenAIAgent` | `AIAgent` |
| Keep chat history | `RavenChatHistoryProvider` | `ChatHistoryProvider` |
| Save sessions, so conversations survive restarts | `RavenAgentSessionStore`, `WithRavenSessionStore()` | `AgentSessionStore` |
| Resume long-running workflows | `RavenCheckpointStore` | `JsonCheckpointStore` |
| Give agents durable files and notes | `RavenAgentFileStore` | `AgentFileStore` |
| Manage skills as data | `RavenAgentSkillsSource` | `AgentSkillsSource` |
| Let agents answer from your documents | `RavenDocumentSearch` | `TextSearchProvider` |

For a vector store and long-term memory, see [RavenDB.Extensions.VectorData](../RavenDB.Extensions.VectorData/README.md).

```bash
dotnet add package RavenDB.Agents.AI
```

## Quick start

```csharp
using var store = new DocumentStore { Urls = ["http://localhost:8080"], Database = "AgentsDemo" }.Initialize();

var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    ChatHistoryProvider = new RavenChatHistoryProvider(store),
    AIContextProviders =
    [
        new RavenDocumentSearch(store, new() { Collection = "Articles", TextField = "Body", TitleField = "Title" })
            .CreateTextSearchProvider(),
    ],
});

// The same key continues the same conversation, even after a restart.
var sessions = new RavenAgentSessionStore(store);
var key = new AgentSessionStoreKey("support-chat");
var session = await sessions.GetOrCreateSessionAsync(agent, key);
Console.WriteLine(await agent.RunAsync("How long do I have to return an item?", session));
await sessions.SaveSessionAsync(agent, key, session);
```

## RavenDB AI agents

A RavenDB AI agent is defined in RavenDB: its prompt, model connection, parameters, query tools and actions. RavenDB calls
the model, runs the queries and keeps the conversation. `RavenAIAgent` lets you use it like any other agent.

```csharp
// Define the agent once (or in Studio). An action can come straight from a function.
var sendEmail = AIFunctionFactory.Create(SendEmail, "send-email", "Sends an email to the customer");
var configuration = new AiAgentConfiguration("Store support", "llm", "You are a support agent...")
{
    Identifier = "support-agent",
    Parameters = [new AiAgentParameter("company", "The id of the customer's company")],
    Queries = [new AiAgentToolQuery("get-orders", "Lists the customer's orders", "from Orders where Company = $company") { ParametersSampleObject = "{}" }],
    Actions = [sendEmail.ToAgentToolAction(), new AiAgentToolAction("issue-refund", "Refunds an order") { ParametersSampleObject = """{"orderId":""}""" }],
};
await store.AI.CreateAgentAsync(configuration, new Reply { Answer = "Your answer to the customer" });

// Use it. send-email runs automatically; issue-refund comes back to you as a function call.
var agent = configuration.ToRavenAIAgent(store, [sendEmail]);
var session = new RavenAIAgentSession { Parameters = { ["company"] = "companies/1" } };
var response = await agent.RunAsync("Where is my order?", session);

foreach (var call in response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>())
{
    var result = await AskHumanAsync(call);   // e.g. approve the refund
    response = await agent.RunAsync(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, result)]), session);
}

// The answer typed, or one of its text properties streamed.
AgentResponse<Reply> reply = await agent.RunAsync<Reply>("Where is my order?", session);
await foreach (var update in agent.RunStreamingAsync<Reply>(r => r.Answer, "Where is my order?", session))
    Console.Write(update.Text);
```

Send files with the message: images, PDFs and text files. A file that's already a RavenDB attachment can be sent without
downloading it.

```csharp
var photo = await DataContent.LoadFromAsync("damage.jpg");
var stored = RavenAttachment.Copy("Claims/1", "claim-form.pdf");
await agent.RunAsync(new ChatMessage(ChatRole.User, [new TextContent("Does the photo match the claim?"), photo, stored]), session);
```

> [!NOTE]
> - The answer type must match the answer shape in the agent's configuration. Call `RunAsync<T>` on the `RavenAIAgent`
>   itself: through an `AIAgent` variable it throws `NotSupportedException`.
> - The model connection (API key) lives in RavenDB, not in your app.
> - A `RavenAIAgent` can be a handoff target but can't hand off itself: RavenDB doesn't support per-run tools yet.

## Chat history

For agents that call the model themselves, such as `ChatClientAgent`. (A `RavenAIAgent` keeps its own conversation.)

```csharp
var history = new RavenChatHistoryProvider(store, new RavenChatHistoryProviderOptions
{
    StateInitializer = _ => new RavenChatHistoryProvider.State("tickets/42"),   // continue a known conversation
    TimeToLive = TimeSpan.FromDays(30),                                         // needs expiration enabled in the database
});

// Read a conversation back.
ChatConversation conversation = await session.LoadAsync<ChatConversation>("ChatConversations/tickets/42");
```

## Sessions and hosting

```csharp
// A key can carry partitions; a RavenAIAgent uses them as values of its parameters.
var sessions = new RavenAgentSessionStore(store);
var key = new AgentSessionStoreKey("c-123", new Dictionary<string, string> { ["company"] = "companies/1" });
AgentSession session = await sessions.GetOrCreateSessionAsync(agent, key);
await sessions.SaveSessionAsync(agent, key, session);
```

For hosted agents, each user gets their own sessions:

```csharp
builder.Services.AddSingleton<IDocumentStore>(store);
builder.Services.AddSingleton<AgentIsolationKeyProvider, SignedInUserKeyProvider>();   // e.g. the signed-in user
builder.AddAIAgent("support", "You are a support agent.").WithRavenSessionStore();
```

## Workflow checkpoints

```csharp
var checkpointStore = new RavenCheckpointStore(store);
var checkpoints = CheckpointManager.CreateJson(checkpointStore);
await using var run = await InProcessExecution.RunAsync(workflow, input, checkpoints, "order-1234");

// After a restart: resume from the latest checkpoint.
var last = (await checkpointStore.RetrieveIndexAsync("order-1234")).Last();
await using var resumed = await InProcessExecution.ResumeAsync(workflow, last, checkpoints);
```

Checkpoints are compressed (often about 70× smaller), since they repeat a lot.

## Files and skills

```csharp
// Durable notes and working files, one set per user.
var files = new RavenAgentFileStore(store, root: "user-1");
var memory = new FileMemoryProvider(files);   // or new FileAccessProvider(files)

// Skills as documents: change them without redeploying.
var skills = new RavenAgentSkillsSource(store);
await skills.SaveSkillAsync(new PersistedAgentSkill
{
    Frontmatter = new AgentSkillFrontmatter("refund-policy", "How to handle refund requests."),
    Instructions = "Refunds are allowed within 30 days. Read the 'limits' resource for amounts.",
    Resources = [new PersistedAgentSkillResource { Name = "limits", Content = "Max refund: $500" }],
});
var skillsProvider = new AgentSkillsProviderBuilder().UseSource(skills).Build();
```

## Answering from your documents

```csharp
var search = new RavenDocumentSearch(store, new RavenDocumentSearchOptions
{
    Collection = "Articles",
    TextField = "Body",       // RavenDB turns this text into vectors itself
    TitleField = "Title",
});
AIContextProvider rag = search.CreateTextSearchProvider();   // add to the agent's context providers
```

## Where the data lives

| Class | Collection | Document id |
|---|---|---|
| `RavenChatHistoryProvider` | `@ChatConversations` | `ChatConversations/{conversationId}` |
| `RavenAgentSessionStore` | `@AgentSessions` | `AgentSessions/{agent}/{sessionId}[/{partition}/{value}...]` |
| `RavenAIAgent` | `@conversations` (RavenDB's own) | `Chats/{guid}`, or the id you set |
| `RavenCheckpointStore` | `@WorkflowCheckpoints` | `WorkflowCheckpoints/{sessionId}/{checkpointId}` |
| `RavenAgentFileStore` | `@AgentFiles` | `AgentFiles/{root}/{path}` |
| `RavenAgentSkillsSource` | `@AgentSkills` | `AgentSkills/{name}` |

Every settings class has `Database`; the ones above also have `DocumentIdPrefix` and `TimeToLive`. To query these
documents, pass the collection name: `session.Query<PersistedAgentSession>(collectionName: PersistedAgentSession.CollectionName)`.

> [!IMPORTANT]
> Whatever these collections hold (history, files, search results) reaches the model as-is. Treat them as trusted input.

## Samples

- [Sample](../../samples/RavenDB.Agents.AI.Sample): a support agent with history, memory, search and sessions in RavenDB.
- [AgentSample](../../samples/RavenDB.Agents.AI.AgentSample): a RavenDB AI agent with a query tool, a client-side action and a human-approved refund.
- [DealDesk](../../samples/RavenDB.Agents.AI.DealDesk/README.md): agents negotiate customer orders in a workflow.
