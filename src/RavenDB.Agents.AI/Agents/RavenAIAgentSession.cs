using System.Text.Json.Serialization;
using Microsoft.Agents.AI;

namespace RavenDB.Agents.AI;

/// <summary>
/// A session of a <see cref="RavenAIAgent"/>: the RavenDB AI conversation it continues and that conversation's parameters.
/// </summary>
/// <remarks>
/// Like every MAF agent's session, its own data is typed properties; the <see cref="AgentSession.StateBag"/> is left to
/// providers and hosting layers (e.g. <see cref="RavenAgentSessionStore"/> puts the session key's partitions there).
/// </remarks>
public sealed class RavenAIAgentSession : AgentSession
{
    /// <summary>Initializes a new instance of the <see cref="RavenAIAgentSession"/> class.</summary>
    public RavenAIAgentSession()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="RavenAIAgentSession"/> class with existing state.</summary>
    /// <param name="conversationId">The RavenDB conversation document id, if the conversation has started.</param>
    /// <param name="stateBag">The session state bag.</param>
    [JsonConstructor]
    public RavenAIAgentSession(string? conversationId, AgentSessionStateBag stateBag)
        : base(stateBag)
    {
        ConversationId = conversationId;
    }

    /// <summary>
    /// Gets or sets the RavenDB AI conversation id. Set it to bind a known conversation; otherwise one is generated on the
    /// first run.
    /// </summary>
    public string? ConversationId { get; set; }

    /// <summary>
    /// Gets or sets the values of the RavenDB agent's parameters (e.g. <c>company</c>) for this session's conversation.
    /// RavenDB requires a value for each declared parameter when a conversation starts and keeps them for its lifetime;
    /// values the agent doesn't declare are ignored. When the session came from a <see cref="RavenAgentSessionStore"/>, the
    /// key's partitions (the hosting isolation key as <c>isolation</c>) are used for any parameter not set here.
    /// </summary>
    public Dictionary<string, string> Parameters { get; set; } = [];
}
