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
/// Chat history for MAF agents that call the model themselves, such as <see cref="ChatClientAgent"/>: each conversation is
/// one <see cref="ChatConversation"/> document holding the messages exactly as MAF produced them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not for <see cref="RavenAIAgent"/>.</b> A RavenDB AI agent's conversation is kept by RavenDB itself (in
/// <c>@conversations</c>, readable with <c>GetConversationMessagesOperation</c>); this provider is for every other agent.
/// </para>
/// <para>
/// Everything round-trips: text, tool calls and results, images, reasoning and custom properties. The conversation id lives
/// in the <see cref="AgentSession.StateBag"/>, so a serialized session reconnects to the same document. To bound what is
/// sent to the model, use MAF's compaction (e.g. <c>CompactionProvider</c>), which keeps tool calls and results together.
/// </para>
/// <para>
/// Each append is one server-side JSON Patch that adds the messages to the end of the stored list, so concurrent runs on
/// one conversation never lose messages and the growing conversation is never rewritten. <see cref="RavenDocumentStoreOptions.TimeToLive"/> counts from when the conversation is created.
/// </para>
/// </remarks>
public sealed class RavenChatHistoryProvider : ChatHistoryProvider
{
    private readonly IDocumentStore _store;
    private readonly RavenChatHistoryProviderOptions _options;
    private readonly ProviderSessionState<State> _sessionState;

    /// <summary>Initializes a new instance of the <see cref="RavenChatHistoryProvider"/> class.</summary>
    /// <param name="store">An initialized RavenDB document store.</param>
    /// <param name="options">Optional configuration.</param>
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

    /// <summary>Per-session state stored in the <see cref="AgentSession.StateBag"/>.</summary>
    public sealed class State
    {
        /// <summary>Initializes a new instance of the <see cref="State"/> class.</summary>
        [JsonConstructor]
        public State(string conversationId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
            ConversationId = conversationId;
        }

        /// <summary>Gets the id of the conversation document (without prefix).</summary>
        [JsonPropertyName("conversationId")]
        public string ConversationId { get; }
    }
}

/// <summary>Options for <see cref="RavenChatHistoryProvider"/>, the chat history for agents other than <see cref="RavenAIAgent"/>.</summary>
/// <remarks>Documents are <c>{DocumentIdPrefix}/{conversationId}</c>; default prefix <c>ChatConversations</c>.</remarks>
public sealed class RavenChatHistoryProviderOptions() : RavenDocumentStoreOptions("ChatConversations")
{
    /// <summary>Gets or sets a delegate that binds a new session to a conversation (e.g. a known conversation id).</summary>
    public Func<AgentSession?, RavenChatHistoryProvider.State>? StateInitializer { get; set; }

    /// <summary>Gets or sets the key used in the session state bag.</summary>
    public string? StateKey { get; set; }

    /// <summary>Gets or sets a filter applied to history before it is sent to the model.</summary>
    public Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>>? ProvideOutputMessageFilter { get; set; }

    /// <summary>Gets or sets a filter applied to request messages before they are stored.</summary>
    public Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>>? StoreInputRequestMessageFilter { get; set; }

    /// <summary>Gets or sets a filter applied to response messages before they are stored.</summary>
    public Func<IEnumerable<ChatMessage>, IEnumerable<ChatMessage>>? StoreInputResponseMessageFilter { get; set; }
}

/// <summary>A conversation of an agent other than <see cref="RavenAIAgent"/> (collection <c>@ChatConversations</c>).</summary>
public sealed class ChatConversation
{
    /// <summary>The collection these documents are stored in, <c>@ChatConversations</c>; pass it as <c>collectionName</c> to query them.</summary>
    public const string CollectionName = "@ChatConversations";

    /// <summary>Gets or sets the messages, oldest first, exactly as MAF produced them.</summary>
    [Newtonsoft.Json.JsonConverter(typeof(SystemTextJsonConverter))]
    public List<ChatMessage> Messages { get; set; } = [];
}

