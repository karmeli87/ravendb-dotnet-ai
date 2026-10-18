using Microsoft.Extensions.AI;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.AI.Agents;

namespace RavenDB.Agents.AI;

/// <summary>Helpers for defining RavenDB AI agents and creating <see cref="RavenAIAgent"/> instances for them.</summary>
public static class RavenAIAgentExtensions
{
    /// <summary>
    /// Turns a function into a RavenDB AI agent action with the same name, description and parameters, so you define the
    /// action once.
    /// </summary>
    /// <param name="function">The function, e.g. from <see cref="AIFunctionFactory"/>. Pass the same function to the
    /// <see cref="RavenAIAgent"/>'s <c>actions</c> so it runs when the agent requests the action.</param>
    /// <returns>An action to add to <see cref="AiAgentConfiguration.Actions"/>.</returns>
    /// <example>
    /// <code><![CDATA[
    /// var sendEmail = AIFunctionFactory.Create(SendEmail, "send-email", "Sends an email to the customer");
    /// var configuration = new AiAgentConfiguration("Support", "llm", "You are a support agent.")
    /// {
    ///     Actions = [sendEmail.ToAgentToolAction()],
    /// };
    /// var agent = configuration.ToRavenAIAgent(store, [sendEmail]);
    /// ]]></code>
    /// </example>
    public static AiAgentToolAction ToAgentToolAction(this AIFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        return new AiAgentToolAction(function.Name, function.Description) { ParametersSchema = function.JsonSchema.GetRawText() };
    }

    /// <summary>
    /// Creates a <see cref="RavenAIAgent"/> for this RavenDB AI agent configuration. The agent's name is the configuration's
    /// <see cref="AiAgentConfiguration.Name"/>, unless <paramref name="options"/> sets one.
    /// </summary>
    /// <param name="configuration">The configuration, e.g. the one you passed to <c>store.AI.CreateAgentAsync</c>.</param>
    /// <param name="store">An initialized RavenDB document store.</param>
    /// <param name="actions">Functions that answer the agent's actions, matched by name. Actions without a function here are
    /// returned to you as function calls.</param>
    /// <param name="options">Optional settings; the object you pass isn't modified.</param>
    /// <returns>The agent.</returns>
    /// <exception cref="ArgumentException">A function in <paramref name="actions"/> doesn't match any action in
    /// <see cref="AiAgentConfiguration.Actions"/>, so the agent would never call it.</exception>
    public static RavenAIAgent ToRavenAIAgent(this AiAgentConfiguration configuration, IDocumentStore store, IList<AIFunction>? actions = null, RavenAIAgentOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        options ??= new RavenAIAgentOptions();
        var declared = (configuration.Actions ?? []).Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
        var missing = (actions ?? []).Select(a => a.Name).Where(name => !declared.Contains(name)).ToList();
        if (missing.Count > 0)
        {
            throw new ArgumentException($"The RavenDB agent '{configuration.Identifier}' has no action for {string.Join(", ", missing.Select(n => $"'{n}'"))}. Add them to its actions, e.g. with ToAgentToolAction().", nameof(actions));
        }

        return new RavenAIAgent(store, configuration.Identifier, actions, new RavenAIAgentOptions
        {
            Name = options.Name ?? configuration.Name,
            ConversationIdPrefix = options.ConversationIdPrefix,
            Database = options.Database,
        });
    }
}

/// <summary>Sends files that are already stored in RavenDB to a <see cref="RavenAIAgent"/>.</summary>
public static class RavenAttachment
{
    /// <summary>
    /// Refers to a document's attachment, to send it to a <see cref="RavenAIAgent"/> in a user message. RavenDB copies the
    /// file into the conversation itself, so your app never downloads it.
    /// </summary>
    /// <remarks>
    /// The result is a regular <see cref="HostedFileContent"/>, so a message that carries it can be saved and restored like
    /// any other message (chat history, sessions, workflow checkpoints).
    /// </remarks>
    /// <param name="documentId">The id of the document that has the attachment, e.g. <c>Claims/1</c>.</param>
    /// <param name="name">The attachment's name. The model sees the file under this name.</param>
    /// <returns>Content to add to a user message.</returns>
    /// <example>
    /// <code><![CDATA[
    /// var photo = RavenAttachment.Copy("Claims/1", "damage.jpg");
    /// await agent.RunAsync(new ChatMessage(ChatRole.User, [new TextContent("Is this damage covered?"), photo]), session);
    /// ]]></code>
    /// </example>
    public static HostedFileContent Copy(string documentId, string name) => new(documentId) { Name = name, Scope = CopyScope };

    internal const string CopyScope = "ravendb-attachment";
}
