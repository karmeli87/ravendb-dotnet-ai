using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Raven.Client.Documents;
using Raven.TestDriver;

namespace RavenDB.Agents.AI.Tests;

/// <summary>Each test gets a fresh database on a shared embedded RavenDB server.</summary>
public abstract class RavenTestBase : RavenTestDriver
{
    protected IDocumentStore Store { get; }

    /// <summary>Gets a value indicating whether a RavenDB license is configured (needed for AI agents).</summary>
    protected static bool HasLicense { get; }

    // License-gated features (AI agents) need a license; it is read from the environment and never logged.
    static RavenTestBase()
    {
        var license = Environment.GetEnvironmentVariable("RAVEN_LICENSE");
        var licensePath = Environment.GetEnvironmentVariable("RAVEN_License_Path");
        HasLicense = !string.IsNullOrWhiteSpace(license) || !string.IsNullOrWhiteSpace(licensePath);
        if (!HasLicense)
        {
            return;
        }

        var options = new TestServerOptions();
        options.Licensing ??= new Raven.Embedded.ServerOptions.LicensingOptions();
        options.Licensing.License = string.IsNullOrWhiteSpace(license) ? null : license;
        options.Licensing.LicensePath = string.IsNullOrWhiteSpace(license) ? licensePath : null;
        ConfigureServer(options);
    }

    protected RavenTestBase()
    {
        Store = GetDocumentStore();
    }

    // Vector search scores come back only when the database reports them.
    protected override void PreConfigureDatabase(Raven.Client.ServerWide.DatabaseRecord databaseRecord)
    {
        databaseRecord.Settings["Indexing.Corax.IncludeDocumentScore"] = "true";
    }

    public override void Dispose()
    {
        Store.Dispose();
        base.Dispose();
    }

    protected static ChatClientAgent CreateAgent(IChatClient client, ChatHistoryProvider? history = null, params AIContextProvider[] contextProviders) =>
        new(client, new ChatClientAgentOptions
        {
            Name = "test-agent",
            ChatHistoryProvider = history,
            AIContextProviders = contextProviders,
        });
}

/// <summary>A scripted <see cref="IChatClient"/> that records every request the agent sends.</summary>
public sealed class RecordingChatClient : IChatClient
{
    private int _calls;

    public List<List<ChatMessage>> Requests { get; } = [];

    public List<ChatOptions?> Options { get; } = [];

    /// <summary>Everything the model saw on the last call: instructions plus message text.</summary>
    public string LastPromptText => string.Join("\n", new[] { Options[^1]?.Instructions }.Concat(LastRequest.Select(m => m.Text)));

    public IReadOnlyList<ChatMessage> LastRequest => Requests[^1];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Requests.Add(messages.ToList());
        Options.Add(options);
        var call = Interlocked.Increment(ref _calls);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"reply {call}")));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var update in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
