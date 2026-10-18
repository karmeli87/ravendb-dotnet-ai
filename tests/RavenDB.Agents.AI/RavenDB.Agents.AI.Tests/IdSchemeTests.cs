using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.VectorData;
using Raven.Client.Documents.Operations;
using RavenDB.Extensions.VectorData;
using Xunit;

namespace RavenDB.Agents.AI.Tests;

public class IdSchemeTests : RavenTestBase
{
    [Fact]
    public void Document_id_prefix_is_shared_and_normalized()
    {
        RavenDocumentStoreOptions[] options =
        [
            new RavenChatHistoryProviderOptions(), new RavenAgentSessionStoreOptions(), new RavenCheckpointStoreOptions(),
            new RavenAgentFileStoreOptions(), new RavenAgentSkillsSourceOptions(),
        ];
        Assert.Equal(["ChatConversations", "AgentSessions", "WorkflowCheckpoints", "AgentFiles", "AgentSkills"], options.Select(o => o.DocumentIdPrefix));

        var custom = new RavenChatHistoryProviderOptions { DocumentIdPrefix = "Chats/" };
        Assert.Equal("Chats", custom.DocumentIdPrefix);
        Assert.Throws<ArgumentException>(() => new RavenCheckpointStoreOptions { DocumentIdPrefix = " " });

        // Every option class carries Database through the shared base.
        RavenOptions[] all = [.. options, new RavenAIAgentOptions(), new RavenDocumentSearchOptions()];
        Assert.All(all, o => Assert.Null(o.Database));
    }

    [Fact]
    public async Task Stores_use_plain_ids()
    {
        // Chat history: {prefix}/{conversationId}, with a custom prefix; an id is an id, slashes included.
        var history = new RavenChatHistoryProvider(Store, new RavenChatHistoryProviderOptions
        {
            DocumentIdPrefix = "Chats",
            StateInitializer = _ => new RavenChatHistoryProvider.State("tickets/42"),
        });
        var agent = CreateAgent(new RecordingChatClient(), history);
        await agent.RunAsync("hi", await agent.CreateSessionAsync());

        // Session store: {prefix}/{agent}/{sessionId}/{partition}/{value}.
        var sessions = new RavenAgentSessionStore(Store);
        await sessions.SaveSessionAsync(agent, new AgentSessionStoreKey("conv-1", new Dictionary<string, string> { ["user"] = "alice" }), await agent.CreateSessionAsync());

        using var session = Store.OpenAsyncSession();
        Assert.True(await session.Advanced.ExistsAsync("Chats/tickets/42"));
        Assert.True(await session.Advanced.ExistsAsync("AgentSessions/test-agent/conv-1/user/alice"));
    }

    [Fact]
    public async Task File_roots_cannot_nest_and_names_keep_special_characters()
    {
        Assert.Throws<ArgumentException>(() => new RavenAgentFileStore(Store, root: "team/a"));

        var team = new RavenAgentFileStore(Store, root: "team");
        var other = new RavenAgentFileStore(Store, root: "team-a");
        await other.WriteAsync("notes/100% done.md", "content");

        Assert.Empty(await team.ListChildrenAsync(""));
        Assert.Equal("content", await other.ReadAsync("notes/100% done.md"));
        Assert.Equal(["100% done.md"], (await other.ListChildrenAsync("notes")).Select(e => e.Name));
    }

    [Fact]
    public async Task Time_to_live_is_applied_by_every_document_store()
    {
        var ttl = TimeSpan.FromDays(1);
        await new RavenAgentFileStore(Store, options: new RavenAgentFileStoreOptions { TimeToLive = ttl }).WriteAsync("a.md", "x");
        await new RavenAgentSkillsSource(Store, new RavenAgentSkillsSourceOptions { TimeToLive = ttl })
            .SaveSkillAsync(new PersistedAgentSkill { Frontmatter = new("temp-skill", "Temporary."), Instructions = "x" });

        using var session = Store.OpenAsyncSession();
        foreach (var id in new[] { "AgentFiles/default/a.md", "AgentSkills/temp-skill" })
        {
            var document = await session.LoadAsync<object>(id);
            Assert.True(session.Advanced.GetMetadataFor(document).ContainsKey(Raven.Client.Constants.Documents.Metadata.Expires), id);
        }
    }

    [Fact]
    public async Task Library_documents_go_to_at_prefixed_collections()
    {
        var agent = CreateAgent(new RecordingChatClient(), new RavenChatHistoryProvider(Store));
        var agentSession = await agent.CreateSessionAsync();
        await agent.RunAsync("hi", agentSession);
        await new RavenAgentSessionStore(Store).SaveSessionAsync(agent, new AgentSessionStoreKey("conv-1"), agentSession);
        await new RavenCheckpointStore(Store).CreateCheckpointAsync("run-a", JsonDocument.Parse("{}").RootElement);
        await new RavenAgentFileStore(Store).WriteAsync("a.md", "x");
        await new RavenAgentSkillsSource(Store).SaveSkillAsync(new PersistedAgentSkill { Frontmatter = new("a-skill", "A skill."), Instructions = "x" });
        await new RavenVectorStore(Store)
            .GetDynamicCollection("Notes", new VectorStoreCollectionDefinition { Properties = [new VectorStoreKeyProperty("Key", typeof(string))] })
            .EnsureCollectionExistsAsync();

        var statistics = await Store.Maintenance.SendAsync(new GetCollectionStatisticsOperation());

        Assert.Equal(
            ["@AgentFiles", "@AgentSessions", "@AgentSkills", "@ChatConversations", "@VectorStoreCollections", "@WorkflowCheckpoints"],
            statistics.Collections.Keys.Order(StringComparer.Ordinal));
    }
}
