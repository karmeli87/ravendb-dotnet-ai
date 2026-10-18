# ravendb-dotnet-ai

RavenDB integrations for [Microsoft Agent Framework](https://github.com/microsoft/agent-framework) and
[Microsoft.Extensions.VectorData](https://learn.microsoft.com/dotnet/ai/vector-stores/overview), for .NET 8 and later.

| Package | What it does |
|---|---|
| [RavenDB.Agents.AI](src/RavenDB.Agents.AI/README.md) | Keeps Agent Framework agents' history, sessions, workflow checkpoints, files and skills in RavenDB, lets agents answer from your documents, and runs RavenDB AI agents as Agent Framework agents. |
| [RavenDB.Extensions.VectorData](src/RavenDB.Extensions.VectorData/README.md) | A vector store on RavenDB, with RavenDB turning text into vectors itself. Works with Semantic Kernel, the Agent Framework's memory or your own code. |

The two packages are independent: use either one, or both.

## A taste

```csharp
using var store = new DocumentStore { Urls = ["http://localhost:8080"], Database = "AgentsDemo" }.Initialize();

var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    ChatHistoryProvider = new RavenChatHistoryProvider(store),                   // RavenDB.Agents.AI
    AIContextProviders =
    [
        new ChatHistoryMemoryProvider(new RavenVectorStore(store), "ChatMemories", vectorDimensions: 384,   // RavenDB.Extensions.VectorData
            _ => new ChatHistoryMemoryProvider.State(new ChatHistoryMemoryProviderScope { UserId = "users/1-A" })),
    ],
});

Console.WriteLine(await agent.RunAsync("How long do I have to return an item?"));
```

## Samples

| Sample | Shows |
|---|---|
| [Sample](samples/RavenDB.Agents.AI.Sample) | A support agent whose history, memory, knowledge base and sessions live in RavenDB. Run it twice to see the conversation continue. |
| [AgentSample](samples/RavenDB.Agents.AI.AgentSample) | A RavenDB AI agent with a database query tool, a client-side action and a refund a person approves. |
| [DealDesk](samples/RavenDB.Agents.AI.DealDesk/README.md) | A web app where agents negotiate customer orders in a workflow, on the Northwind sample data. |

Each sample starts an embedded RavenDB unless `RAVEN_URL` is set, and needs `OPENAI_API_KEY` (or `AZURE_OPENAI_ENDPOINT`,
`AZURE_OPENAI_API_KEY` and `AZURE_OPENAI_DEPLOYMENT`). RavenDB AI agents need a licensed RavenDB: set `RAVEN_LICENSE`
or `RAVEN_License_Path` for the embedded server.

```bash
dotnet run --project samples/RavenDB.Agents.AI.Sample
```

## Build and test

```bash
dotnet test RavenDB.Agents.AI.slnx
```

Tests use an embedded RavenDB. The live RavenDB AI agent tests are skipped unless you set `RAVEN_LICENSE` (or
`RAVEN_License_Path`) and either `RAVEN_AI_INTEGRATION_OPENAI_API_KEY` or `RAVEN_AI_INTEGRATION_AZURE_OPENAI_API_KEY`,
`RAVEN_AI_INTEGRATION_AZURE_OPENAI_DEPLOYMENT_ENDPOINT` and `RAVEN_AI_INTEGRATION_AZURE_OPENAI_CHAT_DEPLOYMENT_NAME`.

The solution also runs Microsoft's VectorData conformance suite. Features RavenDB doesn't support are expected to fail
with RavenDB's error, and tests waiting on a RavenDB fix are skipped with the issue number (e.g. RavenDB-27709).

> [!TIP]
> On Windows, keep the clone path short. Under a deep path the embedded server fails when it runs its built-in embedding
> model.
