using Microsoft.Extensions.AI;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.AI.Agents;

namespace RavenDB.Agents.AI;

/// <summary>Extensions that connect MAF tools to RavenDB AI Agent definitions.</summary>
public static class RavenAIAgentExtensions
{
    /// <summary>
    /// Creates a RavenDB AI Agent action from a MAF tool: its name, description and parameters' JSON schema. The action the
    /// RavenDB agent requests and the tool <see cref="RavenAIAgent"/> runs for it then share one definition.
    /// </summary>
    /// <param name="function">The tool, e.g. from <see cref="AIFunctionFactory"/>; pass the same one in the agent's <c>actions</c>.</param>
    /// <returns>The action, for <see cref="AiAgentConfiguration.Actions"/>.</returns>
    public static AiAgentToolAction ToAgentToolAction(this AIFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        return new AiAgentToolAction(function.Name, function.Description) { ParametersSchema = function.JsonSchema.GetRawText() };
    }

    /// <summary>
    /// Creates the <see cref="RavenAIAgent"/> for a RavenDB AI Agent configuration: the agent id is its
    /// <see cref="AiAgentConfiguration.Identifier"/>, and the name is its <see cref="AiAgentConfiguration.Name"/> unless
    /// <paramref name="options"/> sets one.
    /// </summary>
    /// <param name="configuration">The RavenDB AI Agent configuration, e.g. the one passed to <c>store.AI.CreateAgentAsync</c>.</param>
    /// <param name="store">An initialized RavenDB document store.</param>
    /// <param name="actions">Client-side implementations of the RavenDB agent's actions, matched by name and run in place.</param>
    /// <param name="options">Optional configuration; <paramref name="options"/> itself is not changed.</param>
    /// <returns>The agent.</returns>
    /// <exception cref="ArgumentException">A function in <paramref name="actions"/> has no action of the same name in
    /// <see cref="AiAgentConfiguration.Actions"/>, so the RavenDB agent would never request it.</exception>
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

/// <summary>Files stored in RavenDB, for <see cref="RavenAIAgent"/> user messages.</summary>
public static class RavenAttachment
{
    /// <summary>
    /// Creates the content for a document attachment that <see cref="RavenAIAgent"/> copies into the conversation on the
    /// server (RavenDB's <c>CopyAttachmentFrom</c>), so the bytes never pass through the client.
    /// </summary>
    /// <remarks>
    /// It is MAF's own <see cref="HostedFileContent"/> (<see cref="HostedFileContent.FileId"/> = the document id,
    /// <see cref="HostedFileContent.Name"/> = the attachment name, <see cref="HostedFileContent.Scope"/> marks it as a RavenDB
    /// attachment), so messages carrying it serialize anywhere MAF stores them.
    /// </remarks>
    /// <param name="documentId">The id of the document that holds the attachment.</param>
    /// <param name="name">The attachment name; the copy in the conversation keeps it.</param>
    /// <returns>The content to add to a user message.</returns>
    public static HostedFileContent Copy(string documentId, string name) => new(documentId) { Name = name, Scope = CopyScope };

    internal const string CopyScope = "ravendb-attachment";
}
