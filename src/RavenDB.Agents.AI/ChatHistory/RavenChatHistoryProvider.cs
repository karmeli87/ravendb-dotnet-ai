using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.JsonPatch;
using Microsoft.Extensions.AI;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Exceptions;
using RavenDB.Agents.AI.Internal;

namespace RavenDB.Agents.AI;

/// <summary>
/// Keeps an agent's chat history in RavenDB, one <see cref="ChatConversation"/> document per conversation. Use it with
/// agents that call the model themselves, such as <see cref="ChatClientAgent"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><b>Not needed for <see cref="RavenAIAgent"/></b>: RavenDB keeps that agent's conversation itself.</description></item>
/// <item><description>Messages are stored complete (text, tool calls and results, images, reasoning), so the history
/// reads back exactly as it was written.</description></item>
/// <item><description>The session remembers its conversation, so a saved and restored session continues the same history.</description></item>
/// <item><description>Runs that add to the same conversation at the same time don't lose messages.</description></item>
/// <item><description>To limit how much history is sent to the model, use the Agent Framework's compaction
/// (e.g. <c>CompactionProvider</c>).</description></item>
/// </list>
/// </remarks>
/// <example>
/// <code><![CDATA[
/// var history = new RavenChatHistoryProvider(store, new RavenChatHistoryProviderOptions
/// {
///     StateInitializer = _ => new RavenChatHistoryProvider.State("tickets/42"), // continue ChatConversations/tickets/42
/// });
/// var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions { ChatHistoryProvider = history });
/// ]]></code>
/// </example>
public sealed class RavenChatHistoryProvider : ChatHistoryProvider
{
    private readonly IDocumentStore _store;
    private readonly RavenChatHistoryProviderOptions _options;
    private readonly ProviderSessionState<State> _sessionState;

    /// <summary>Creates a chat history provider.</summary>
    /// <param name="store">An initialized RavenDB document store.</param>
    /// <param name="options">Optional settings, such as which conversation a new session continues.</param>
    public RavenChatHistoryProvider(IDocumentStore store, RavenChatHistoryProviderOptions? options = null)
        : base(options?.ProvideOutputMessageFilter, options?.StoreInputRequestMessageFilter, options?.StoreInputResponseMessageFilter)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? new RavenChatHistoryProviderOptions();
        _sessionState = new ProviderSessionState<State>(
            _options.StateInitializer ?? (_ => new State(Guid.NewGuid().ToString("N"))),
            _options.StateKey ?? GetType().Name);
        StateKeys = [_sessionState.StateKey];
    }

    /// <inheritdoc />
    public override IReadOnlyList<string> StateKeys { get; }

    /// <inheritdoc />
    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        using var session = RavenHelpers.OpenSession(_store, _options.Database, noTracking: true);
        var conversation = await session.LoadAsync<ChatConversation>(GetDocumentId(context.Session), cancellationToken).ConfigureAwait(false);
        return conversation?.Messages ?? [];
    }

    /// <inheritdoc />
    protected override async ValueTask StoreChatHistoryAsync(InvokedContext context, CancellationToken cancellationToken = default)
    {
        var patch = new JsonPatchDocument();
        foreach (var message in context.RequestMessages.Concat(context.ResponseMessages ?? []))
        {
            if (message.GetAgentRequestMessageSourceType() == AgentRequestMessageSourceType.ChatHistory)
            {
                continue;
            }

            patch.Add($"/{nameof(ChatConversation.Messages)}/-", SystemTextJsonConverter.ToToken(message, typeof(ChatMessage)));
        }

        if (patch.Operations.Count == 0)
        {
            return;
        }

        var id = GetDocumentId(context.Session);
        using (var session = RavenHelpers.OpenSession(_store, _options.Database))
        {
            if (!await session.Advanced.ExistsAsync(id, cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    var conversation = new ChatConversation();
                    await session.StoreAsync(conversation, changeVector: string.Empty, id, cancellationToken).ConfigureAwait(false);
                    RavenHelpers.SetMetadata(session, conversation, ChatConversation.CollectionName, _options.TimeToLive);
                    await session.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (ConcurrencyException)
                {
                }
            }
        }

        var operations = _options.Database is null ? _store.Operations : _store.Operations.ForDatabase(_options.Database);
        await operations.SendAsync(new JsonPatchOperation(id, patch), token: cancellationToken).ConfigureAwait(false);
    }

    private string GetDocumentId(AgentSession? session) => $"{_options.DocumentIdPrefix}/{_sessionState.GetOrInitializeState(session).ConversationId}";

    /// <summary>Which conversation a session uses. The provider saves it with the session.</summary>
    public sealed class State
    {
        /// <summary>Points a session at a conversation.</summary>
        /// <param name="conversationId">The conversation id, without the document id prefix, e.g. <c>tickets/42</c>.</param>
        [JsonConstructor]
        public State(string conversationId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
            ConversationId = conversationId;
        }

        /// <summary>Gets the conversation id, without the document id prefix.</summary>
        [JsonPropertyName("conversationId")]
        public string ConversationId { get; }
    }
}

/// <summary>Settings for a <see cref="RavenChatHistoryProvider"/>.</summary>
/// <remarks>
/// Each conversation is the document <c>{DocumentIdPrefix}/{conversationId}</c>; the prefix defaults to <c>ChatConversations</c>.
/// </remarks>
public sealed class RavenChatHistoryProviderOptions() : RavenDocumentStoreOptions("ChatConversations")
{
    /// <summary>
    /// Gets or sets which conversation a new session uses, e.g. <c>_ =&gt; new RavenChatHistoryProvider.State("tickets/42")</c>.
    /// When not set, each new session starts a new conversation.
    /// </summary>
    public Func<AgentSession?, RavenChatHistoryProvider.State>? StateInitializer { get; set; }

    /// <summary>
    /// Gets or sets the name under which the provider saves its state in the session. Set it only when an agent uses two
    /// chat history providers.
    /// </summary>
    public string? StateKey { get; set; }

    /// <summary>Gets or sets a function that chooses which stored messages are sent to the model.</summary>
    public Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>>? ProvideOutputMessageFilter { get; set; }

    /// <summary>
    /// Gets or sets a function that chooses which input messages are saved, e.g. to skip memories and search results that
    /// context providers add on every run.
    /// </summary>
    public Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>>? StoreInputRequestMessageFilter { get; set; }

    /// <summary>Gets or sets a function that chooses which response messages are saved.</summary>
    public Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>>? StoreInputResponseMessageFilter { get; set; }
}

/// <summary>
/// A stored conversation of a <see cref="RavenChatHistoryProvider"/>. Load it to read a conversation's messages.
/// </summary>
public sealed class ChatConversation
{
    /// <summary>
    /// The collection of these documents, <c>@ChatConversations</c>. Pass it as <c>collectionName</c> when you query them.
    /// </summary>
    public const string CollectionName = "@ChatConversations";

    /// <summary>Gets or sets the messages, oldest first.</summary>
    [Newtonsoft.Json.JsonConverter(typeof(SystemTextJsonConverter))]
    public List<ChatMessage> Messages { get; set; } = [];
}

