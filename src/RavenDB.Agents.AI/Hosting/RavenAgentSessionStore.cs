using System.Text.Json;
using Microsoft.Agents.AI;
using Raven.Client.Documents;
using RavenDB.Agents.AI.Internal;

namespace RavenDB.Agents.AI;

/// <summary>
/// An <see cref="AgentSessionStore"/> that persists every agent's session in RavenDB, one <see cref="PersistedAgentSession"/>
/// document per key, so hosted agents resume conversations across restarts and machines.
/// </summary>
/// <remarks>
/// Every agent is handled the same way, as in MAF's own stores: the session is stored as the agent serializes it, and handed
/// back to the agent to deserialize on load. A new session gets the key's partitions in its state bag, the way MAF's hosting
/// layers add their data; a <see cref="RavenAIAgent"/> uses them as its conversation's parameters, other agents ignore them.
/// </remarks>
public sealed class RavenAgentSessionStore : AgentSessionStore
{
    internal const string PartitionsStateKey = "RavenAgentSessionStore.Partitions";

    private readonly IDocumentStore _store;
    private readonly RavenAgentSessionStoreOptions _options;

    /// <summary>Initializes a new instance of the <see cref="RavenAgentSessionStore"/> class.</summary>
    /// <param name="store">An initialized RavenDB document store.</param>
    /// <param name="options">Optional configuration.</param>
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
    /// <remarks>A new session gets the key's partitions in its state bag (see <see cref="RavenAIAgentSession.Parameters"/>).</remarks>
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

    /// <summary>Deletes the stored session for <paramref name="key"/>, if any.</summary>
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

/// <summary>Options for <see cref="RavenAgentSessionStore"/>.</summary>
/// <remarks>
/// Documents are <c>{DocumentIdPrefix}/{agent}/{sessionId}</c> followed by each partition's name and value, ordered by name; default prefix
/// <c>AgentSessions</c>.
/// </remarks>
public sealed class RavenAgentSessionStoreOptions() : RavenDocumentStoreOptions("AgentSessions")
{
    /// <summary>Gets or sets a stable agent namespace. Defaults to the agent's name, then its id.</summary>
    public string? AgentNamespace { get; set; }
}

/// <summary>
/// A persisted agent session (collection <c>@AgentSessions</c>): the session as its agent serialized it, plus the key it
/// belongs to, readable and queryable in Studio.
/// </summary>
public sealed class PersistedAgentSession
{
    /// <summary>The collection these documents are stored in, <c>@AgentSessions</c>; pass it as <c>collectionName</c> to query them.</summary>
    public const string CollectionName = "@AgentSessions";

    /// <summary>Gets or sets the agent namespace.</summary>
    public string Agent { get; set; } = string.Empty;

    /// <summary>Gets or sets the session id from the store key.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Gets or sets the partitions from the store key, e.g. the hosting isolation key.</summary>
    public Dictionary<string, string> Partitions { get; set; } = [];

    /// <summary>Gets or sets the session as serialized by <see cref="AIAgent.SerializeSessionAsync"/>, stored as-is.</summary>
    [Newtonsoft.Json.JsonConverter(typeof(SystemTextJsonConverter))]
    public JsonElement Session { get; set; }
}
