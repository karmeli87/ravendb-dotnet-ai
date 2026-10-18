// A support agent whose history, long-term memory, knowledge base and session state all live in RavenDB.
//
//   RAVEN_URL              RavenDB server; when unset, an embedded RavenDB server is started (data kept next to the app)
//   OPENAI_API_KEY         OpenAI key                         | or Azure OpenAI:  AZURE_OPENAI_ENDPOINT
//   OPENAI_MODEL           chat model (default gpt-4.1-mini)  |                   AZURE_OPENAI_API_KEY
//                                                             |                   AZURE_OPENAI_DEPLOYMENT

using System.ClientModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using Raven.Client.Documents;
using Raven.Client.Exceptions;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Raven.Embedded;
using RavenDB.Agents.AI;
using RavenDB.Extensions.VectorData;

using var store = await OpenDocumentStoreAsync("AgentsDemo");
await SeedKnowledgeBaseAsync(store);

IChatClient chatClient = CreateChatClient();

const string UserId = "users/1-A";

var knowledgeBase = new RavenDocumentSearch(store, new RavenDocumentSearchOptions
{
    Collection = "Articles",
    TextField = "Body",
    TitleField = "Title",
});

var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    Name = "support",
    ChatOptions = new ChatOptions
    {
        Instructions = "You are a helpful support agent. Use the knowledge base when relevant.",
    },
    // Conversation transcript -> one RavenDB document per conversation. Store only the real conversation: memories and
    // knowledge-base snippets are re-retrieved every turn, so persisting them would only bloat the history.
    ChatHistoryProvider = new RavenChatHistoryProvider(store, new RavenChatHistoryProviderOptions
    {
        StoreInputRequestMessageFilter = messages => messages.Where(m => m.GetAgentRequestMessageSourceType() == AgentRequestMessageSourceType.External),
    }),
    AIContextProviders =
    [
        // Long-term memory across conversations: MAF's provider on the RavenDB vector store, which embeds the text itself
        // (the 384 dimensions are MAF's required argument; RavenDB's built-in model decides the real size).
        new ChatHistoryMemoryProvider(new RavenVectorStore(store), "ChatMemories", 384,
            _ => new ChatHistoryMemoryProvider.State(new ChatHistoryMemoryProviderScope { UserId = UserId })),
        // RAG over the Articles collection.
        knowledgeBase.CreateTextSearchProvider(),
    ],
});

// Sessions are persisted, so restarting the app resumes the same conversation.
var sessions = new RavenAgentSessionStore(store);
var sessionKey = new AgentSessionStoreKey("support-chat", new Dictionary<string, string> { ["user"] = UserId });
var session = await sessions.GetOrCreateSessionAsync(agent, sessionKey);

Console.WriteLine("Ask the support agent (empty line to exit).");
while (Console.ReadLine() is { Length: > 0 } input)
{
    Console.WriteLine($"> {input}");
    await foreach (var update in agent.RunStreamingAsync(input, session))
    {
        Console.Write(update.Text);
    }

    Console.WriteLine();
    await sessions.SaveSessionAsync(agent, sessionKey, session);
}

await PrintStoredStateAsync(store);

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

    // No server configured: run RavenDB in-process. Data persists across runs in ./RavenDB.
    EmbeddedServer.Instance.StartServer(new ServerOptions { DataDirectory = Path.Combine(AppContext.BaseDirectory, "RavenDB") });
    return await EmbeddedServer.Instance.GetDocumentStoreAsync(database);
}

static IChatClient CreateChatClient()
{
    if (Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT") is { Length: > 0 } endpoint)
    {
        // Azure OpenAI's v1 API is OpenAI-compatible, so the plain OpenAI SDK works against it.
        var baseUri = new Uri(new Uri(endpoint).GetLeftPart(UriPartial.Authority) + "/openai/v1/");
        return new OpenAIClient(new ApiKeyCredential(Required("AZURE_OPENAI_API_KEY")), new OpenAIClientOptions { Endpoint = baseUri })
            .GetChatClient(Required("AZURE_OPENAI_DEPLOYMENT"))
            .AsIChatClient();
    }

    return new OpenAIClient(Required("OPENAI_API_KEY"))
        .GetChatClient(Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4.1-mini")
        .AsIChatClient();

    static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Set {name}.");
}

static async Task SeedKnowledgeBaseAsync(IDocumentStore store)
{
    using var session = store.OpenAsyncSession();
    await session.StoreAsync(new Article("Returns", "Customers may return any product within 30 days for a full refund."), "articles/returns");
    await session.StoreAsync(new Article("Shipping", "Orders ship within two business days using express couriers."), "articles/shipping");
    await session.SaveChangesAsync();
}

// Shows what the run left in RavenDB.
static async Task PrintStoredStateAsync(IDocumentStore store)
{
    using var session = store.OpenAsyncSession();
    // Each conversation document holds the agent's messages as-is.
    var conversations = (await session.Advanced.LoadStartingWithAsync<ChatConversation>("ChatConversations/")).ToList();
    var memories = await session.Query<object>(collectionName: "ChatMemories").CountAsync();
    Console.WriteLine($"[RavenDB] {conversations.Sum(c => c.Messages.Count)} messages in {conversations.Count} conversation(s), {memories} memories.");
}

internal sealed record Article(string Title, string Body);
