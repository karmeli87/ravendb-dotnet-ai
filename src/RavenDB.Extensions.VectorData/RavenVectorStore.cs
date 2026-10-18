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
/// A vector store on a RavenDB database. Use it wherever a <see cref="VectorStore"/> is expected, e.g. Semantic Kernel or
/// the Agent Framework's <c>ChatHistoryMemoryProvider</c>.
/// </summary>
/// <remarks>
/// Make a vector property a <see cref="string"/> and RavenDB turns the text into vectors itself, so you don't need an
/// embedding service. See <see cref="RavenCollection{TKey, TRecord}"/> for how records, vectors and filters work.
/// </remarks>
/// <example>
/// <code><![CDATA[
/// var vectorStore = new RavenVectorStore(store);
/// var hotels = vectorStore.GetCollection<string, Hotel>("Hotels");
/// await hotels.UpsertAsync(new Hotel { HotelId = "h1", Name = "Seaside", Description = "A quiet place by the sea" });
///
/// await foreach (var hit in hotels.SearchAsync("calm hotel near the beach", top: 5))
///     Console.WriteLine($"{hit.Record.Name} {hit.Score}");
/// ]]></code>
/// </example>
public sealed class RavenVectorStore : VectorStore
{
    internal const string SystemName = "ravendb";

    private readonly IDocumentStore _store;
    private readonly RavenVectorStoreOptions _options;
    private readonly VectorStoreMetadata _metadata;

    /// <summary>Creates a vector store on the document store's database.</summary>
    /// <param name="store">An initialized RavenDB document store.</param>
    /// <param name="options">Optional settings that apply to all its collections.</param>
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
    /// <remarks>Lists the collections created through the vector store, by <c>EnsureCollectionExistsAsync</c> or a first
    /// upsert. Other RavenDB collections in the database aren't listed.</remarks>
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

/// <summary>Settings for a <see cref="RavenVectorStore"/>, applied to all its collections.</summary>
public sealed class RavenVectorStoreOptions
{
    /// <summary>Gets or sets the database to use. <see langword="null"/> uses the document store's default database.</summary>
    public string? Database { get; set; }

    /// <summary>
    /// Gets or sets the identifier of a RavenDB embeddings generation task that turns <see cref="string"/> vector properties
    /// into vectors. <see langword="null"/> uses RavenDB's built-in embedding model.
    /// </summary>
    public string? EmbeddingsTaskIdentifier { get; set; }

    /// <summary>
    /// Gets or sets whether filtered gets and searches wait until RavenDB has indexed your latest changes. Slower, but you
    /// always see your own writes. Getting records by key doesn't need it.
    /// </summary>
    public bool WaitForNonStaleResults { get; set; }

    /// <summary>
    /// Gets or sets whether searches compare the query with every record (exact, slower) instead of using the vector index
    /// (approximate, faster). Vector properties with <see cref="IndexKind.Flat"/> are always searched exactly.
    /// </summary>
    public bool ExactSearch { get; set; }
}
