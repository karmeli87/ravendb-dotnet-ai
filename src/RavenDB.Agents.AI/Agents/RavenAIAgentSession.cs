using System.Text.Json.Serialization;
using Microsoft.Agents.AI;

namespace RavenDB.Agents.AI;

/// <summary>
/// A conversation with a <see cref="RavenAIAgent"/>: which RavenDB conversation it continues, and the values of the agent's
/// parameters.
/// </summary>
/// <remarks>
/// Pass the same session to every run to continue the conversation. Save and restore it like any agent session (for
/// example with <see cref="RavenAgentSessionStore"/>).
/// </remarks>
public sealed class RavenAIAgentSession : AgentSession
{
    /// <summary>Creates a session for a new conversation.</summary>
    public RavenAIAgentSession()
    {
    }

    /// <summary>Restores a session, e.g. when it's deserialized.</summary>
    /// <param name="conversationId">The id of the RavenDB conversation, or <see langword="null"/> if it hasn't started yet.</param>
    /// <param name="stateBag">The session's state bag.</param>
    [JsonConstructor]
    public RavenAIAgentSession(string? conversationId, AgentSessionStateBag stateBag)
        : base(stateBag)
    {
        ConversationId = conversationId;
    }

    /// <summary>
    /// Gets or sets the id of the RavenDB conversation. Set it to continue a conversation you know, e.g. <c>Chats/tickets/42</c>;
    /// otherwise a new id is created on the first run.
    /// </summary>
    public string? ConversationId { get; set; }

    /// <summary>
    /// Gets or sets the values of the agent's parameters for this conversation, e.g. <c>["company"] = "companies/1"</c>.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>Every parameter the agent declares needs a value when the conversation starts; the values then stay
    /// fixed for the whole conversation. Names the agent doesn't declare are ignored.</description></item>
    /// <item><description>A session from <see cref="RavenAgentSessionStore"/> also uses the values in its key (and, when
    /// hosted, the signed-in user's isolation key as <c>isolation</c>) for any parameter not set here.</description></item>
    /// </list>
    /// </remarks>
    public Dictionary<string, string> Parameters { get; set; } = [];
}
