using System.IO.Compression;
using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using RavenDB.Agents.AI.Internal;

namespace RavenDB.Agents.AI;

/// <summary>
/// Saves workflow checkpoints in RavenDB, so a long-running workflow can resume after a restart, on any machine, from
/// any of its checkpoints.
/// </summary>
/// <remarks>
/// Each checkpoint is a <see cref="WorkflowCheckpoint"/> document. Its content is compressed into the document's
/// <c>value.json.br</c> attachment, since checkpoints are large and repetitive.
/// </remarks>
/// <example>
/// <code><![CDATA[
/// var checkpointStore = new RavenCheckpointStore(store);
/// var checkpoints = CheckpointManager.CreateJson(checkpointStore);
/// await using var run = await InProcessExecution.RunAsync(workflow, input, checkpoints, "order-1234");
///
/// // After a restart: resume from the latest checkpoint.
/// var last = (await checkpointStore.RetrieveIndexAsync("order-1234")).Last();
/// await using var resumed = await InProcessExecution.ResumeAsync(workflow, last, checkpoints);
/// ]]></code>
/// </example>
public sealed class RavenCheckpointStore : JsonCheckpointStore
{
    private const string ValueAttachment = "value.json.br";

    private readonly IDocumentStore _store;
    private readonly RavenCheckpointStoreOptions _options;

    /// <summary>Creates a checkpoint store.</summary>
    /// <param name="store">An initialized RavenDB document store.</param>
    /// <param name="options">Optional settings, such as how long checkpoints are kept.</param>
    public RavenCheckpointStore(IDocumentStore store, RavenCheckpointStoreOptions? options = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? new RavenCheckpointStoreOptions();
    }

    /// <inheritdoc />
    public override async ValueTask<CheckpointInfo> CreateCheckpointAsync(string sessionId, JsonElement value, CheckpointInfo? parent = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var checkpointId = $"{DateTime.UtcNow.Ticks:D19}-{Guid.NewGuid():N}";
        var document = new WorkflowCheckpoint
        {
            SessionId = sessionId,
            CheckpointId = checkpointId,
            ParentCheckpointId = parent?.CheckpointId,
            CreatedAt = DateTime.UtcNow,
        };

        using var compressed = new MemoryStream();
        using (var brotli = new BrotliStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        using (var writer = new Utf8JsonWriter(brotli))
        {
            value.WriteTo(writer);
        }

        compressed.Position = 0;
        using var session = RavenHelpers.OpenSession(_store, _options.Database);
        await session.StoreAsync(document, $"{_options.DocumentIdPrefix}/{sessionId}/{checkpointId}").ConfigureAwait(false);
        RavenHelpers.SetMetadata(session, document, WorkflowCheckpoint.CollectionName, _options.TimeToLive);
        session.Advanced.Attachments.Store(document, ValueAttachment, compressed, "application/x-brotli");
        await session.SaveChangesAsync().ConfigureAwait(false);

        return new CheckpointInfo(sessionId, checkpointId);
    }

    /// <inheritdoc />
    public override async ValueTask<JsonElement> RetrieveCheckpointAsync(string sessionId, CheckpointInfo key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(key);

        using var session = RavenHelpers.OpenSession(_store, _options.Database, noTracking: true);
        using var attachment = await session.Advanced.Attachments.GetAsync($"{_options.DocumentIdPrefix}/{sessionId}/{key.CheckpointId}", ValueAttachment).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Checkpoint '{key.CheckpointId}' for session '{sessionId}' was not found.");
        await using var brotli = new BrotliStream(attachment.Stream, CompressionMode.Decompress);
        using var json = await JsonDocument.ParseAsync(brotli).ConfigureAwait(false);
        return json.RootElement.Clone();
    }

    /// <inheritdoc />
    public override async ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(string sessionId, CheckpointInfo? withParent = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var prefix = $"{_options.DocumentIdPrefix}/{sessionId}/";
        using var session = RavenHelpers.OpenSession(_store, _options.Database, noTracking: true);
        var checkpoints = await session.Query<WorkflowCheckpoint>(collectionName: WorkflowCheckpoint.CollectionName)
            .Where(c => c.Id.StartsWith(prefix))
            .Select(c => new { c.SessionId, c.CheckpointId, c.ParentCheckpointId })
            .ToListAsync()
            .ConfigureAwait(false);

        return checkpoints
            .Where(c => c.SessionId == sessionId && (withParent is null || c.ParentCheckpointId == withParent.CheckpointId))
            .OrderBy(c => c.CheckpointId, StringComparer.Ordinal)
            .Select(c => new CheckpointInfo(c.SessionId, c.CheckpointId))
            .ToList();
    }
}

/// <summary>Settings for a <see cref="RavenCheckpointStore"/>.</summary>
/// <remarks>
/// Each checkpoint is the document <c>{DocumentIdPrefix}/{sessionId}/{checkpointId}</c>; the prefix defaults to
/// <c>WorkflowCheckpoints</c>.
/// </remarks>
public sealed class RavenCheckpointStoreOptions() : RavenDocumentStoreOptions("WorkflowCheckpoints");

/// <summary>
/// A checkpoint saved by <see cref="RavenCheckpointStore"/>. The document holds the checkpoint's details; its content is the
/// compressed <c>value.json.br</c> attachment.
/// </summary>
public sealed class WorkflowCheckpoint
{
    /// <summary>
    /// The collection of these documents, <c>@WorkflowCheckpoints</c>. Pass it as <c>collectionName</c> when you query them.
    /// </summary>
    public const string CollectionName = "@WorkflowCheckpoints";

    /// <summary>Gets or sets the document id, <c>{prefix}/{sessionId}/{checkpointId}</c>.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the id of the workflow run (the session id you passed when you started it).</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Gets or sets the checkpoint id. Ids sort in the order the checkpoints were created.</summary>
    public string CheckpointId { get; set; } = string.Empty;

    /// <summary>Gets or sets the id of the checkpoint this one follows, if any.</summary>
    public string? ParentCheckpointId { get; set; }

    /// <summary>Gets or sets when the checkpoint was created (UTC).</summary>
    public DateTime CreatedAt { get; set; }
}
