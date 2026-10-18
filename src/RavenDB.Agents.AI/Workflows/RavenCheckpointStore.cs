using System.IO.Compression;
using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using RavenDB.Agents.AI.Internal;

namespace RavenDB.Agents.AI;

/// <summary>
/// A <see cref="JsonCheckpointStore"/> that keeps workflow checkpoints in RavenDB, so long-running workflows
/// survive restarts and can be resumed from any checkpoint.
/// </summary>
/// <remarks>
/// Use with <c>CheckpointManager.CreateJson(new RavenCheckpointStore(store))</c>.
/// Checkpoints of one workflow session share an id prefix, so listing them is an id-prefix query that needs no index.
/// The checkpoint payload is stored Brotli-compressed as the document's <c>value.json.br</c> attachment: MAF checkpoints
/// repeat most of their content (a sub-workflow's checkpoints are copied into every parent checkpoint), so they compress
/// well; the document keeps only the index fields.
/// </remarks>
public sealed class RavenCheckpointStore : JsonCheckpointStore
{
    private const string ValueAttachment = "value.json.br";

    private readonly IDocumentStore _store;
    private readonly RavenCheckpointStoreOptions _options;

    /// <summary>Initializes a new instance of the <see cref="RavenCheckpointStore"/> class.</summary>
    /// <param name="store">An initialized RavenDB document store.</param>
    /// <param name="options">Optional configuration.</param>
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

/// <summary>Options for <see cref="RavenCheckpointStore"/>.</summary>
/// <remarks>Documents are <c>{DocumentIdPrefix}/{sessionId}/{checkpointId}</c>; default prefix <c>WorkflowCheckpoints</c>.</remarks>
public sealed class RavenCheckpointStoreOptions() : RavenDocumentStoreOptions("WorkflowCheckpoints");

/// <summary>
/// A workflow checkpoint document (collection <c>@WorkflowCheckpoints</c>). The payload is its Brotli-compressed
/// <c>value.json.br</c> attachment.
/// </summary>
public sealed class WorkflowCheckpoint
{
    /// <summary>The collection these documents are stored in, <c>@WorkflowCheckpoints</c>; pass it as <c>collectionName</c> to query them.</summary>
    public const string CollectionName = "@WorkflowCheckpoints";

    /// <summary>Gets or sets the document id, <c>{prefix}/{sessionId}/{checkpointId}</c>.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the workflow session id.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Gets or sets the checkpoint id (time-ordered).</summary>
    public string CheckpointId { get; set; } = string.Empty;

    /// <summary>Gets or sets the parent checkpoint id, if any.</summary>
    public string? ParentCheckpointId { get; set; }

    /// <summary>Gets or sets when the checkpoint was created.</summary>
    public DateTime CreatedAt { get; set; }
}
