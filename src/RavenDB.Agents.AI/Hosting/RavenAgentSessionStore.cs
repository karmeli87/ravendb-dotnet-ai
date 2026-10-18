using System.Text.Json;
using Microsoft.Agents.AI;
using Raven.Client.Documents;
using RavenDB.Agents.AI.Internal;

namespace RavenDB.Agents.AI;

/// <summary>
/// Saves agent sessions in RavenDB, one <see cref="PersistedAgentSession"/> document per key, so conversations continue
/// after a restart or on another machine.
/// </summary>
/// <remarks>
/// <para>
/// Works with any agent. A key is a session id plus optional partitions, e.g. <c>company = companies/1</c>. A new
/// <see cref="RavenAIAgent"/> session uses the partitions as values of the agent's parameters (see
/// <see cref="RavenAIAgentSession.Parameters"/>); other agents ignore them.
/// </para>
/// <para>For hosted agents, register it with <see cref="RavenHostingExtensions.WithRavenSessionStore"/>.</para>
/// </remarks>
/// <example>
/// <code><![CDATA[
/// var sessions = new RavenAgentSessionStore(store);
/// var key = new AgentSessionStoreKey("c-123", new Dictionary<string, string> { ["company"] = "companies/1" });
///
/// AgentSession session = await sessions.GetOrCreateSessionAsync(agent, key);
/// await agent.RunAsync("Where is my order?", session);
/// await sessions.SaveSessionAsync(agent, key, session);
/// ]]></code>
/// </example>
public sealed class RavenAgentSessionStore : AgentSessionStore
{
    internal const string PartitionsStateKey = "RavenAgentSessionStore.Partitions";

    private readonly IDocumentStore _store;
    private readonly RavenAgentSessionStoreOptions _options;

    /// <summary>Creates a session store.</summary>
    /// <param name="store">An initialized RavenDB document store.</param>
    /// <param name="options">Optional settings, such as the agent namespace.</param>
    public RavenAgentSessionStore(IDocumentStore store, RavenAgentSessionStoreOptions? options = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? new RavenAgentSessionStoreOptions();
    }

    /// <inheritdoc />
    public override async ValueTask SaveSessionAsync(AIAgent agent, AgentSessionStoreKey key, AgentSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(session);

        var agentName = GetAgentName(agent);
        var document = new PersistedAgentSession
        {
            Agent = agentName,
            SessionId = key.SessionId,
            Partitions = key.Partitions?.ToDictionary(p => p.Key, p => p.Value) ?? [],
            Session = await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken).ConfigureAwait(false),
        };

        using var dbSession = RavenHelpers.OpenSession(_store, _options.Database);
        await dbSession.StoreAsync(document, GetDocumentId(agentName, key), cancellationToken).ConfigureAwait(false);
        RavenHelpers.SetMetadata(dbSession, document, PersistedAgentSession.CollectionName, _options.TimeToLive);
        await dbSession.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async ValueTask<AgentSession?> GetSessionAsync(AIAgent agent, AgentSessionStoreKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(key);

        using var dbSession = RavenHelpers.OpenSession(_store, _options.Database, noTracking: true);
        var document = await dbSession.LoadAsync<PersistedAgentSession>(GetDocumentId(GetAgentName(agent), key), cancellationToken).ConfigureAwait(false);
        return document is null ? null : await agent.DeserializeSessionAsync(document.Session, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>A new <see cref="RavenAIAgent"/> session uses the key's partitions as parameter values (see
    /// <see cref="RavenAIAgentSession.Parameters"/>).</remarks>
    public override async ValueTask<AgentSession> GetOrCreateSessionAsync(AIAgent agent, AgentSessionStoreKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(key);

        if (await GetSessionAsync(agent, key, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            return existing;
        }

        var session = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        session.StateBag.SetValue(PartitionsStateKey, key.Partitions?.ToDictionary(p => p.Key, p => p.Value) ?? []);
        return session;
    }

    /// <summary>Deletes the saved session for this agent and key. Does nothing if there's none.</summary>
    /// <param name="agent">The agent the session belongs to.</param>
    /// <param name="key">The session's key.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that completes when the session is deleted.</returns>
    public async Task DeleteSessionAsync(AIAgent agent, AgentSessionStoreKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(key);

        using var dbSession = RavenHelpers.OpenSession(_store, _options.Database);
        dbSession.Delete(GetDocumentId(GetAgentName(agent), key));
        await dbSession.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private string GetAgentName(AIAgent agent) => _options.AgentNamespace ?? agent.Name ?? agent.Id;

    private string GetDocumentId(string agentName, AgentSessionStoreKey key) => string.Join(
        '/',
        [_options.DocumentIdPrefix, agentName, key.SessionId, .. (key.Partitions ?? new Dictionary<string, string>()).OrderBy(p => p.Key, StringComparer.Ordinal).SelectMany(p => new[] { p.Key, p.Value })]);
}

/// <summary>Settings for a <see cref="RavenAgentSessionStore"/>.</summary>
/// <remarks>
/// A session's document id is <c>{DocumentIdPrefix}/{agent}/{sessionId}</c>, followed by each partition's name and value in
/// name order, e.g. <c>AgentSessions/support/c-123/company/companies/1</c>. The prefix defaults to <c>AgentSessions</c>.
/// </remarks>
public sealed class RavenAgentSessionStoreOptions() : RavenDocumentStoreOptions("AgentSessions")
{
    /// <summary>
    /// Gets or sets the agent part of the document ids. Set it so ids stay the same if the agent is renamed. Defaults to the
    /// agent's name, or its id when it has no name.
    /// </summary>
    public string? AgentNamespace { get; set; }
}

/// <summary>
/// A session saved by <see cref="RavenAgentSessionStore"/>, with the key it was saved under. You can read and query these
/// documents, e.g. in RavenDB Studio.
/// </summary>
public sealed class PersistedAgentSession
{
    /// <summary>
    /// The collection of these documents, <c>@AgentSessions</c>. Pass it as <c>collectionName</c> when you query them.
    /// </summary>
    public const string CollectionName = "@AgentSessions";

    /// <summary>Gets or sets the agent the session belongs to (see <see cref="RavenAgentSessionStoreOptions.AgentNamespace"/>).</summary>
    public string Agent { get; set; } = string.Empty;

    /// <summary>Gets or sets the session id of the key.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Gets or sets the partitions of the key, e.g. the signed-in user when hosted.</summary>
    public Dictionary<string, string> Partitions { get; set; } = [];

    /// <summary>Gets or sets the session, in the form the agent saved it (<see cref="AIAgent.SerializeSessionAsync"/>).</summary>
    [Newtonsoft.Json.JsonConverter(typeof(SystemTextJsonConverter))]
    public JsonElement Session { get; set; }
}
