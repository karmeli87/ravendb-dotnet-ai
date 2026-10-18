using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.VectorData;
using Microsoft.Extensions.VectorData.ProviderServices;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Session;
using RavenDB.Extensions.VectorData.Internal;

namespace RavenDB.Extensions.VectorData;

/// <summary>
/// A <c>Microsoft.Extensions.VectorData</c> <see cref="VectorStore"/> backed by a RavenDB database. Use it anywhere MAF or
/// Semantic Kernel takes a vector store, e.g. MAF's <c>ChatHistoryMemoryProvider</c>.
/// </summary>
/// <remarks>
/// <see cref="string"/> vector properties are embedded by RavenDB itself (its built-in model, or an embeddings generation
/// task), so semantic search needs no external embedding service. Records are stored as-is; see <see cref="RavenCollection{TKey, TRecord}"/>.
/// </remarks>
public sealed class RavenVectorStore : VectorStore
{
    internal const string SystemName = "ravendb";

    private readonly IDocumentStore _store;
    private readonly RavenVectorStoreOptions _options;
    private readonly VectorStoreMetadata _metadata;

    /// <summary>Initializes a new instance of the <see cref="RavenVectorStore"/> class.</summary>
    /// <param name="store">An initialized RavenDB document store.</param>
    /// <param name="options">Optional configuration.</param>
    public RavenVectorStore(IDocumentStore store, RavenVectorStoreOptions? options = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? new RavenVectorStoreOptions();
        _metadata = new VectorStoreMetadata { VectorStoreSystemName = SystemName, VectorStoreName = _options.Database ?? store.Database };
    }

    /// <inheritdoc />
    [RequiresDynamicCode("This method is incompatible with NativeAOT. For dynamic mapping use GetDynamicCollection().")]
    [RequiresUnreferencedCode("This method is incompatible with trimming. For dynamic mapping use GetDynamicCollection().")]
#pragma warning disable IDE0090 // explicit generic arguments are clearer here
    public override VectorStoreCollection<TKey, TRecord> GetCollection<TKey, TRecord>(string name, VectorStoreCollectionDefinition? definition = null) =>
        typeof(TRecord) == typeof(Dictionary<string, object?>)
            ? throw new NotSupportedException(VectorDataStrings.GetCollectionWithDictionaryNotSupported)
            : new RavenCollection<TKey, TRecord>(_store, name, CreateCollectionOptions(definition));
#pragma warning restore IDE0090

    /// <inheritdoc />
    public override VectorStoreCollection<object, Dictionary<string, object?>> GetDynamicCollection(string name, VectorStoreCollectionDefinition definition) =>
        new RavenDynamicCollection(_store, name, CreateCollectionOptions(definition));

    /// <inheritdoc />
    /// <remarks>Lists the collections that have a marker document <c>VectorStoreCollections/{name}</c> (collection
    /// <c>@VectorStoreCollections</c>), created by <c>EnsureCollectionExistsAsync</c> or the first upsert.</remarks>
    public override async IAsyncEnumerable<string> ListCollectionNamesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<CollectionMarker> markers;
        using (var session = RavenHelpers.OpenSession(_store, _options.Database, noTracking: true))
        {
            markers = await RavenHelpers.LoadAllStartingWithAsync<CollectionMarker>(session, $"{MarkerPrefix}/", cancellationToken).ConfigureAwait(false);
        }

        foreach (var marker in markers)
        {
            yield return marker.Name;
        }
    }

    /// <inheritdoc />
    public override Task<bool> CollectionExistsAsync(string name, CancellationToken cancellationToken = default) =>
        CollectionExistsAsync(_store, _options.Database, name, cancellationToken);

    /// <inheritdoc />
    public override Task EnsureCollectionDeletedAsync(string name, CancellationToken cancellationToken = default) =>
        DeleteCollectionAsync(_store, _options.Database, name, cancellationToken);

    /// <inheritdoc />
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is not null ? null
            : serviceType == typeof(VectorStoreMetadata) ? _metadata
            : serviceType == typeof(IDocumentStore) ? _store
            : serviceType.IsInstanceOfType(this) ? this
            : null;
    }

    internal const string MarkerPrefix = "VectorStoreCollections";

    private const string MarkerCollection = "@VectorStoreCollections";

    internal static async Task StoreMarkerAsync(IAsyncDocumentSession session, string name, CancellationToken cancellationToken)
    {
        var marker = new CollectionMarker { Name = name };
        await session.StoreAsync(marker, $"{MarkerPrefix}/{name}", cancellationToken).ConfigureAwait(false);
        session.Advanced.GetMetadataFor(marker)[Raven.Client.Constants.Documents.Metadata.Collection] = MarkerCollection;
    }

    internal static async Task EnsureCollectionAsync(IDocumentStore store, string? database, string name, CancellationToken cancellationToken)
    {
        using var session = RavenHelpers.OpenSession(store, database);
        await StoreMarkerAsync(session, name, cancellationToken).ConfigureAwait(false);
        await session.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<bool> CollectionExistsAsync(IDocumentStore store, string? database, string name, CancellationToken cancellationToken)
    {
        using var session = RavenHelpers.OpenSession(store, database, noTracking: true);
        return await session.Advanced.ExistsAsync($"{MarkerPrefix}/{name}", cancellationToken).ConfigureAwait(false);
    }

    internal static async Task DeleteCollectionAsync(IDocumentStore store, string? database, string name, CancellationToken cancellationToken)
    {
        using var session = RavenHelpers.OpenSession(store, database);
        var query = session.Advanced.AsyncDocumentQuery<object>(collectionName: name).GetIndexQuery();
        var deletion = await store.Operations.ForDatabase(database ?? store.Database).SendAsync(new DeleteByQueryOperation(query), token: cancellationToken).ConfigureAwait(false);
        await deletion.WaitForCompletionAsync(cancellationToken).ConfigureAwait(false);

        session.Delete($"{MarkerPrefix}/{name}");
        await session.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class CollectionMarker
    {
        public string Name { get; set; } = string.Empty;
    }

    private RavenCollectionOptions CreateCollectionOptions(VectorStoreCollectionDefinition? definition) => new()
    {
        Definition = definition,
        Database = _options.Database,
        WaitForNonStaleResults = _options.WaitForNonStaleResults,
        ExactSearch = _options.ExactSearch,
        EmbeddingsTaskIdentifier = _options.EmbeddingsTaskIdentifier,
    };
}

/// <summary>Options for <see cref="RavenVectorStore"/>.</summary>
public sealed class RavenVectorStoreOptions
{
    /// <summary>Gets or sets the database name; <see langword="null"/> uses the store's default database.</summary>
    public string? Database { get; set; }

    /// <summary>Gets or sets an embeddings generation task id for string vector properties; <see langword="null"/> uses RavenDB's built-in model.</summary>
    public string? EmbeddingsTaskIdentifier { get; set; }

    /// <summary>Gets or sets a value indicating whether queries wait for indexes to catch up with recent writes.</summary>
    public bool WaitForNonStaleResults { get; set; }

    /// <summary>Gets or sets a value indicating whether searches are exact (brute force) instead of approximate (HNSW).
    /// A vector property with <see cref="IndexKind.Flat"/> is always searched exactly.</summary>
    public bool ExactSearch { get; set; }
}
