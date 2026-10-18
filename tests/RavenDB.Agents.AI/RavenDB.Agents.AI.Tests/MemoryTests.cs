using Microsoft.Agents.AI;
using RavenDB.Extensions.VectorData;
using Xunit;

namespace RavenDB.Agents.AI.Tests;

/// <summary>MAF's own memory provider on the RavenDB vector store.</summary>
public class MemoryTests : RavenTestBase
{
    [Fact]
    public async Task Maf_chat_history_memory_provider_works_end_to_end()
    {
        var vectorStore = new RavenVectorStore(Store, new RavenVectorStoreOptions { WaitForNonStaleResults = true });

        ChatHistoryMemoryProvider CreateProvider(string userId) => new(
            vectorStore,
            "ChatMemories",
            384,
            _ => new ChatHistoryMemoryProvider.State(new ChatHistoryMemoryProviderScope { UserId = userId }),
            new ChatHistoryMemoryProviderOptions { MaxResults = 1 });

        var alice = CreateAgent(new RecordingChatClient(), null, CreateProvider("alice"));
        await alice.RunAsync("My dog Rex sleeps on the sofa.", await alice.CreateSessionAsync());

        // A new session for the same user recalls the memory...
        var client = new RecordingChatClient();
        var agent = CreateAgent(client, null, CreateProvider("alice"));
        await agent.RunAsync("Where does my dog Rex sleep?", await agent.CreateSessionAsync());
        Assert.Contains("sofa", client.LastPromptText, StringComparison.Ordinal);

        // ...and another user's scope filter keeps it private.
        var bobClient = new RecordingChatClient();
        var bob = CreateAgent(bobClient, null, CreateProvider("bob"));
        await bob.RunAsync("Where does my dog Rex sleep?", await bob.CreateSessionAsync());
        Assert.DoesNotContain("sofa", bobClient.LastPromptText, StringComparison.Ordinal);
    }
}
