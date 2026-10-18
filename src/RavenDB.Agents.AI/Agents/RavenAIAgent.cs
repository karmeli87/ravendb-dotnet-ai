using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Raven.Client.Documents;
using Raven.Client.Documents.AI;
using Raven.Client.Documents.Operations.AI;

namespace RavenDB.Agents.AI;

/// <summary>
/// Runs a RavenDB AI agent as a Microsoft Agent Framework agent, so you can use it in workflows, hosting and
/// orchestrations like any other <see cref="AIAgent"/>.
/// </summary>
/// <remarks>
/// <para>
/// The RavenDB AI agent is defined in RavenDB (in Studio, or with <c>store.AI.CreateAgentAsync</c>): its prompt, model
/// connection, parameters, query tools and actions. RavenDB calls the model and keeps the conversation; the
/// <see cref="RavenAIAgentSession"/> only holds the conversation id and its parameter values.
/// </para>
/// <list type="bullet">
/// <item><description><b>Answers.</b> A run returns the agent's answer as text. To get it typed, call
/// <see cref="RunAsync{TAnswer}(string, AgentSession?, CancellationToken)"/> with a type that matches the answer shape in
/// the agent's configuration, or <see cref="RunStreamingAsync{TAnswer}(Expression{Func{TAnswer, string}}, string, AgentSession?, CancellationToken)"/>
/// to stream one of its text properties. Calling <c>AIAgent.RunAsync&lt;T&gt;</c> through an <see cref="AIAgent"/> variable
/// throws <see cref="NotSupportedException"/>.</description></item>
/// <item><description><b>Actions.</b> When the agent requests an action, the <see cref="AIFunction"/> with the same name
/// from the constructor's <c>actions</c> runs automatically. Any other action comes back as a
/// <see cref="FunctionCallContent"/>; answer it with a <see cref="FunctionResultContent"/> in the next run.</description></item>
/// <item><description><b>Files.</b> Images, PDFs and text files in a user message (<see cref="DataContent"/>) are sent to
/// the model under their <see cref="DataContent.Name"/> (a generated name when it's not set); sending the same name again
/// replaces the file. To send a file that's already a RavenDB attachment, use <see cref="RavenAttachment.Copy"/>.</description></item>
/// </list>
/// </remarks>
/// <example>
/// <code><![CDATA[
/// var agent = new RavenAIAgent(store, "support-agent", actions: [sendEmail]);
/// var session = new RavenAIAgentSession { Parameters = { ["company"] = "companies/1" } };
///
/// AgentResponse response = await agent.RunAsync("Where is my order?", session);
/// AgentResponse<Reply> typed = await agent.RunAsync<Reply>("Where is my order?", session);
/// ]]></code>
/// </example>
public sealed class RavenAIAgent : AIAgent
{
    private readonly AiOperations _ai;
    private readonly string _agentId;
    private readonly RavenAIAgentOptions _options;
    private readonly Dictionary<string, AIFunction> _actions;

    /// <summary>Creates an agent for the RavenDB AI agent with the given identifier.</summary>
    /// <param name="store">An initialized RavenDB document store.</param>
    /// <param name="agentId">The RavenDB AI agent's identifier (<see cref="Raven.Client.Documents.Operations.AI.Agents.AiAgentConfiguration.Identifier"/>).</param>
    /// <param name="actions">Functions that answer the agent's actions, matched by name. Actions without a function here are
    /// returned to you as function calls.</param>
    /// <param name="options">Optional settings, such as the agent name.</param>
    public RavenAIAgent(IDocumentStore store, string agentId, IList<AIFunction>? actions = null, RavenAIAgentOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        _agentId = agentId;
        _options = options ?? new RavenAIAgentOptions();
        _ai = _options.Database is null ? store.AI : store.AI.ForDatabase(_options.Database);
        _actions = (actions ?? []).ToDictionary(a => a.Name, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    protected override string? IdCore => _agentId;

    /// <inheritdoc />
    public override string? Name => _options.Name ?? _agentId;

    /// <inheritdoc />
    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) =>
        new(new RavenAIAgentSession());

    /// <inheritdoc />
    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) =>
        new(JsonSerializer.SerializeToElement((RavenAIAgentSession)session, jsonSerializerOptions ?? AIJsonUtilities.DefaultOptions));

    /// <inheritdoc />
    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) =>
        new(serializedState.Deserialize<RavenAIAgentSession>(jsonSerializerOptions ?? AIJsonUtilities.DefaultOptions) ?? new RavenAIAgentSession());

    /// <inheritdoc cref="RunAsync{TAnswer}(IEnumerable{ChatMessage}, AgentSession?, CancellationToken)" path="/summary|/typeparam|/returns"/>
    /// <param name="message">The user's message.</param>
    /// <param name="session">The session to continue; <see langword="null"/> starts a new conversation.</param>
    /// <param name="cancellationToken">A token to cancel the run.</param>
    public Task<AgentResponse<TAnswer>> RunAsync<TAnswer>(string message, AgentSession? session = null, CancellationToken cancellationToken = default) =>
        RunAsync<TAnswer>([new ChatMessage(ChatRole.User, message)], session, cancellationToken);

    /// <summary>Runs the agent and returns its answer as a <typeparamref name="TAnswer"/>.</summary>
    /// <typeparam name="TAnswer">The answer type. It must match the answer shape in the RavenDB agent's configuration.</typeparam>
    /// <param name="messages">The messages to send, e.g. a user message or the results of the agent's earlier actions.</param>
    /// <param name="session">The session to continue; <see langword="null"/> starts a new conversation.</param>
    /// <param name="cancellationToken">A token to cancel the run.</param>
    /// <returns>The response; the typed answer is in <see cref="AgentResponse{T}.Result"/>.</returns>
    public Task<AgentResponse<TAnswer>> RunAsync<TAnswer>(IEnumerable<ChatMessage> messages, AgentSession? session = null, CancellationToken cancellationToken = default) =>
        RunAsync<TAnswer>(messages, session, AIJsonUtilities.DefaultOptions, new RavenAgentRunOptions<TAnswer>(), cancellationToken);

    /// <inheritdoc />
    protected override async Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (options is not RavenAgentRunOptions && options?.ResponseFormat is ChatResponseFormatJson)
        {
            throw new NotSupportedException($"A typed answer needs {nameof(RavenAIAgent)}.{nameof(RunAsync)}<TAnswer>; MAF's AIAgent.RunAsync<T> doesn't pass the answer type.");
        }

        var (conversation, pending) = CreateConversation(messages, session);
        var turn = await (options as RavenAgentRunOptions ?? RavenAgentRunOptions.Text).RunAsync(conversation, cancellationToken).ConfigureAwait(false);

        var reply = pending.Count > 0
            ? new ChatMessage(ChatRole.Assistant, pending)
            : new ChatMessage(ChatRole.Assistant, turn.Text);
        return new AgentResponse(reply)
        {
            AgentId = Id,
            ResponseId = conversation.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            Usage = ToUsageDetails(turn.Usage),
            RawRepresentation = turn.Answer,
        };
    }

    /// <inheritdoc cref="RunStreamingAsync{TAnswer}(Expression{Func{TAnswer, string}}, IEnumerable{ChatMessage}, AgentSession?, CancellationToken)" path="/summary|/typeparam|/returns"/>
    /// <param name="stream">The text property to stream, e.g. <c>a =&gt; a.Answer</c>.</param>
    /// <param name="message">The user's message.</param>
    /// <param name="session">The session to continue; <see langword="null"/> starts a new conversation.</param>
    /// <param name="cancellationToken">A token to cancel the run.</param>
    public IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync<TAnswer>(Expression<Func<TAnswer, string>> stream, string message, AgentSession? session = null, CancellationToken cancellationToken = default) =>
        RunStreamingAsync(stream, [new ChatMessage(ChatRole.User, message)], session, cancellationToken);

    /// <summary>
    /// Runs the agent for a <typeparamref name="TAnswer"/> answer and streams one of its text properties as it's generated.
    /// </summary>
    /// <remarks>The last update holds the complete typed answer in <see cref="AgentResponseUpdate.RawRepresentation"/>.</remarks>
    /// <typeparam name="TAnswer">The answer type. It must match the answer shape in the RavenDB agent's configuration.</typeparam>
    /// <param name="stream">The text property to stream, e.g. <c>a =&gt; a.Answer</c>.</param>
    /// <param name="messages">The messages to send, e.g. a user message or the results of the agent's earlier actions.</param>
    /// <param name="session">The session to continue; <see langword="null"/> starts a new conversation.</param>
    /// <param name="cancellationToken">A token to cancel the run.</param>
    /// <returns>The updates, in order.</returns>
    public IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync<TAnswer>(Expression<Func<TAnswer, string>> stream, IEnumerable<ChatMessage> messages, AgentSession? session = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return RunStreamingAsync(messages, session, new RavenAgentRunOptions<TAnswer>(stream), cancellationToken);
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (conversation, pending) = CreateConversation(messages, session);
        var messageId = Guid.NewGuid().ToString("N");

        var chunks = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var run = (options as RavenAgentRunOptions ?? RavenAgentRunOptions.Text).StreamAsync(conversation, chunks.Writer, cancellationToken);
        await foreach (var chunk in chunks.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return new AgentResponseUpdate(ChatRole.Assistant, chunk) { AgentId = Id, MessageId = messageId };
        }

        var turn = await run.ConfigureAwait(false);
        yield return new AgentResponseUpdate(ChatRole.Assistant, [.. pending, new UsageContent(ToUsageDetails(turn.Usage))])
        {
            AgentId = Id,
            MessageId = messageId,
            ResponseId = conversation.Id,
            RawRepresentation = turn.Answer,
        };
    }

    private sealed record Turn(object? Answer, string? Text, AiUsage? Usage);

    private class RavenAgentRunOptions : AgentRunOptions
    {
        public static readonly RavenAgentRunOptions Text = new();

        public RavenAgentRunOptions()
        {
        }

        protected RavenAgentRunOptions(RavenAgentRunOptions options)
            : base(options)
        {
        }

        public virtual async Task<Turn> RunAsync(IAiConversationOperations conversation, CancellationToken cancellationToken)
        {
            var answer = await conversation.RunAsync(cancellationToken).ConfigureAwait(false);
            return new Turn(answer.Answer, answer.Answer, answer.Usage);
        }

        public async Task<Turn> StreamAsync(IAiConversationOperations conversation, ChannelWriter<string> chunks, CancellationToken cancellationToken)
        {
            try
            {
                return await StreamCoreAsync(conversation, chunk => chunks.WriteAsync(chunk, cancellationToken).AsTask(), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                chunks.Complete();
            }
        }

        protected virtual async Task<Turn> StreamCoreAsync(IAiConversationOperations conversation, Func<string, Task> onChunk, CancellationToken cancellationToken)
        {
            var answer = await conversation.StreamAsync(onChunk, cancellationToken).ConfigureAwait(false);
            return new Turn(answer.Answer, answer.Answer, answer.Usage);
        }

        public override AgentRunOptions Clone() => new RavenAgentRunOptions(this);
    }

    private sealed class RavenAgentRunOptions<TAnswer> : RavenAgentRunOptions
    {
        private readonly Expression<Func<TAnswer, string>>? _stream;

        public RavenAgentRunOptions()
        {
        }

        public RavenAgentRunOptions(Expression<Func<TAnswer, string>> stream)
        {
            _stream = stream;
        }

        private RavenAgentRunOptions(RavenAgentRunOptions<TAnswer> options)
            : base(options)
        {
            _stream = options._stream;
        }

        public override async Task<Turn> RunAsync(IAiConversationOperations conversation, CancellationToken cancellationToken)
        {
            var answer = await conversation.RunAsync<TAnswer>(cancellationToken).ConfigureAwait(false);
            return new Turn(answer.Answer, JsonSerializer.Serialize(answer.Answer, AIJsonUtilities.DefaultOptions), answer.Usage);
        }

        protected override async Task<Turn> StreamCoreAsync(IAiConversationOperations conversation, Func<string, Task> onChunk, CancellationToken cancellationToken)
        {
            var stream = _stream ?? throw new InvalidOperationException("A typed answer streams the property passed to RunStreamingAsync<TAnswer>.");
            var answer = await conversation.StreamAsync(stream, onChunk, cancellationToken).ConfigureAwait(false);
            return new Turn(answer.Answer, JsonSerializer.Serialize(answer.Answer, AIJsonUtilities.DefaultOptions), answer.Usage);
        }

        public override AgentRunOptions Clone() => new RavenAgentRunOptions<TAnswer>(this);
    }

    private (IAiConversationOperations Conversation, List<AIContent> Pending) CreateConversation(IEnumerable<ChatMessage> messages, AgentSession? agentSession)
    {
        var session = (RavenAIAgentSession?)agentSession ?? new RavenAIAgentSession();

        session.ConversationId ??= $"{_options.ConversationIdPrefix}/{Guid.NewGuid():N}";
        var conversation = _ai.Conversation(_agentId, session.ConversationId, CreateConversationOptions(session), changeVector: null);

        var otherAgentsCalls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case FunctionCallContent call:
                        otherAgentsCalls.Add(call.CallId);
                        break;
                    case FunctionResultContent result when !otherAgentsCalls.Remove(result.CallId):
                        conversation.AddActionResponse(result.CallId, JsonSerializer.Serialize(result.Result, AIJsonUtilities.DefaultOptions));
                        break;
                }
            }

            if (message.Role != ChatRole.User)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(message.Text))
            {
                conversation.AddUserPrompt([message.Text]);
            }

            foreach (var file in message.Contents.OfType<DataContent>())
            {
                conversation.AddAttachment(file.Name ?? Guid.NewGuid().ToString("N"), new MemoryStream(file.Data.ToArray()), file.MediaType);
            }

            foreach (var file in message.Contents.OfType<HostedFileContent>().Where(f => f.Scope == RavenAttachment.CopyScope))
            {
                conversation.CopyAttachmentFrom(file.FileId, file.Name!);
            }
        }

        var pending = new List<AIContent>();
        conversation.OnUnhandledAction += async args =>
        {
            var call = CreateFunctionCall(args.Action.ToolId, args.Action.Name, args.Action.Arguments);
            if (!_actions.TryGetValue(call.Name, out var action))
            {
                pending.Add(call);
                return;
            }

            if (call.Exception is { } malformed)
            {
                throw malformed;
            }

            var result = await action.InvokeAsync(new AIFunctionArguments(call.Arguments), args.Token).ConfigureAwait(false);
            args.Sender.AddActionResponse(args.Action.ToolId, JsonSerializer.Serialize(result, AIJsonUtilities.DefaultOptions));
        };

        return (conversation, pending);
    }

    private static UsageDetails ToUsageDetails(AiUsage? usage) =>
        new() { InputTokenCount = usage?.PromptTokens, OutputTokenCount = usage?.CompletionTokens, TotalTokenCount = usage?.TotalTokens };

    internal static FunctionCallContent CreateFunctionCall(string toolId, string name, string? arguments) =>
        FunctionCallContent.CreateFromParsedArguments(arguments ?? "{}", toolId, name, json => JsonSerializer.Deserialize<Dictionary<string, object?>>(json));

    private AiConversationCreationOptions CreateConversationOptions(RavenAIAgentSession session)
    {
        var creationOptions = new AiConversationCreationOptions();
        if (session.StateBag.TryGetValue<Dictionary<string, string>>(RavenAgentSessionStore.PartitionsStateKey, out var partitions) && partitions is not null)
        {
            foreach (var partition in partitions.Where(p => !session.Parameters.ContainsKey(p.Key)))
            {
                creationOptions.AddParameter(partition.Key, partition.Value);
            }
        }

        foreach (var parameter in session.Parameters)
        {
            creationOptions.AddParameter(parameter.Key, parameter.Value);
        }

        return creationOptions;
    }
}

/// <summary>Settings for a <see cref="RavenAIAgent"/>.</summary>
public sealed class RavenAIAgentOptions : RavenOptions
{
    /// <summary>
    /// Gets or sets the agent's name, e.g. the name you register it under in hosting. Defaults to the RavenDB agent's identifier.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the id prefix of new conversations, which get the id <c>{prefix}/{guid}</c>. Defaults to <c>Chats</c>.
    /// </summary>
    public string ConversationIdPrefix { get; set; } = "Chats";
}
