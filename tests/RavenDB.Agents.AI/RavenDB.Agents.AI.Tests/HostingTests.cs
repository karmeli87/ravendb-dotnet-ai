using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents;
using Xunit;

namespace RavenDB.Agents.AI.Tests;

public class HostingTests : RavenTestBase
{
    private const string AgentName = "support";

    // A hosted agent wired with WithRavenSessionStore, resolved from DI the way MAF hosting does.
    private (AIAgent Agent, AgentSessionStore Sessions) Host(string? isolationKey, Action<IHostedAgentBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDocumentStore>(Store);
        if (isolationKey is not null)
        {
            services.AddSingleton<AgentIsolationKeyProvider>(new FixedIsolationKey(isolationKey));
        }

        configure(services.AddAIAgent(AgentName, "Be brief.", new RecordingChatClient()));
        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredKeyedService<AIAgent>(AgentName), provider.GetRequiredKeyedService<AgentSessionStore>(AgentName));
    }

    private async Task<List<string>> StoredSessionIdsAsync()
    {
        using var session = Store.OpenAsyncSession();
        return [.. (await session.Query<PersistedAgentSession>(collectionName: PersistedAgentSession.CollectionName).Customize(x => x.WaitForNonStaleResults()).ToListAsync()).Select(s => session.Advanced.GetDocumentId(s))];
    }

    [Fact]
    public async Task Isolation_partitions_sessions_by_the_hosts_isolation_key()
    {
        var key = new AgentSessionStoreKey("conv-1");
        var (agent, alice) = Host("alice", builder => builder.WithRavenSessionStore());
        await alice.SaveSessionAsync(agent, key, await agent.CreateSessionAsync());

        // The isolation key becomes a partition of the document id; the agent name is the default namespace.
        Assert.Equal(["AgentSessions/support/conv-1/isolation/alice"], await StoredSessionIdsAsync());
        Assert.NotNull(await alice.GetSessionAsync(agent, key));

        // The same session id seen by another user is a different session.
        var (bobAgent, bob) = Host("bob", builder => builder.WithRavenSessionStore());
        Assert.Null(await bob.GetSessionAsync(bobAgent, key));
    }

    [Fact]
    public async Task Configured_options_apply_and_sessions_stay_isolated()
    {
        var (agent, sessions) = Host("alice", builder => builder.WithRavenSessionStore(options => options.DocumentIdPrefix = "HostedSessions"));
        await sessions.SaveSessionAsync(agent, new AgentSessionStoreKey("conv-1"), await agent.CreateSessionAsync());

        Assert.Equal(["HostedSessions/support/conv-1/isolation/alice"], await StoredSessionIdsAsync());
    }

    [Fact]
    public async Task Without_isolation_sessions_are_not_partitioned_and_need_no_key_provider()
    {
        var (agent, sessions) = Host(isolationKey: null, builder => builder.WithRavenSessionStore(withIsolation: false));
        await sessions.SaveSessionAsync(agent, new AgentSessionStoreKey("conv-1"), await agent.CreateSessionAsync());

        Assert.Equal(["AgentSessions/support/conv-1"], await StoredSessionIdsAsync());
    }

    [Fact]
    public async Task Default_isolation_without_a_key_provider_throws()
    {
        // MAF's isolation is strict by default: without an AgentIsolationKeyProvider every call fails.
        var (agent, sessions) = Host(isolationKey: null, builder => builder.WithRavenSessionStore());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await sessions.GetSessionAsync(agent, new AgentSessionStoreKey("conv-1")));
    }

    [Fact]
    public async Task A_hosted_RavenAIAgent_session_round_trips_through_the_store()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDocumentStore>(Store);
        services.AddSingleton<AgentIsolationKeyProvider>(new FixedIsolationKey("alice"));
        services.AddAIAgent(AgentName, (_, name) => new RavenAIAgent(Store, "support-agent", options: new RavenAIAgentOptions { Name = name })).WithRavenSessionStore();
        var provider = services.BuildServiceProvider();
        var agent = provider.GetRequiredKeyedService<AIAgent>(AgentName);
        var sessions = provider.GetRequiredKeyedService<AgentSessionStore>(AgentName);
        var key = new AgentSessionStoreKey("c-123");

        // A new session gets the key's partitions in its state bag; the isolation key arrives as "isolation".
        var session = (RavenAIAgentSession)await sessions.GetOrCreateSessionAsync(agent, key);
        Assert.Equal(new Dictionary<string, string> { ["isolation"] = "alice" }, session.StateBag.GetValue<Dictionary<string, string>>(RavenAgentSessionStore.PartitionsStateKey));
        session.ConversationId = "Chats/support/c-123";
        session.StateBag.SetValue("note", "kept");

        // Stored as the agent serializes it, under the key, and handed back to the agent on load.
        await sessions.SaveSessionAsync(agent, key, session);
        Assert.Equal(["AgentSessions/support/c-123/isolation/alice"], await StoredSessionIdsAsync());
        var loaded = (RavenAIAgentSession)(await sessions.GetSessionAsync(agent, key))!;
        Assert.Equal("Chats/support/c-123", loaded.ConversationId);
        Assert.Equal("alice", loaded.StateBag.GetValue<Dictionary<string, string>>(RavenAgentSessionStore.PartitionsStateKey)!["isolation"]);
        Assert.Equal("kept", loaded.StateBag.GetValue<string>("note"));
    }

    private sealed class FixedIsolationKey(string key) : AgentIsolationKeyProvider
    {
        public override ValueTask<string?> GetIsolationKeyAsync(CancellationToken cancellationToken = default) => new(key);
    }
}
