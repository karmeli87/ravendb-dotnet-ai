using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Microsoft.Extensions.VectorData.ProviderServices;
using Raven.Client.Documents;
using Raven.Client.Documents.Conventions;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Sparrow.Json;
using RavenDB.Extensions.VectorData.Internal;

namespace RavenDB.Extensions.VectorData;

/// <summary>
/// A <see cref="VectorStoreCollection{TKey, TRecord}"/> backed by a RavenDB collection.
/// </summary>
/// <remarks>
/// <para>
/// Records are stored as-is with the RavenDB client under the document id <c>{collection}/{key}</c>. Record types with
/// an <c>Id</c> property are rejected, since RavenDB maps it to the document id. Vectors are <c>float[]</c>,
/// <see cref="ReadOnlyMemory{T}"/> of <see cref="float"/> or <see cref="Embedding{T}"/> of <see cref="float"/> (all stored as JSON arrays; the
/// last two need <see cref="RavenVectorDataDocumentStoreExtensions.UseVectorData{TStore}"/> on the store), or
/// <see cref="string"/> properties that RavenDB embeds itself (its built-in model, or an embeddings generation task via
/// <see cref="RavenCollectionOptions.EmbeddingsTaskIdentifier"/>); client-side embedding generators and MEVD storage names
/// are rejected. To rename a field, use <c>[JsonProperty("...")]</c>, which RavenDB honors for storing and querying.
/// </para>
/// <para>
/// Filters go to RavenDB's LINQ provider as-is, with exact (case-sensitive) comparisons; <c>r =&gt; true</c> reads everything.
/// Shapes RavenDB doesn't support fail with RavenDB's error: for "field is one of these values" write
/// <c>r.Field.In(values)</c>, and for "array field has any of these values" <c>r.Tags.ContainsAny(values)</c>
/// (RavenDB's own extensions in <c>Raven.Client.Documents.Linq</c>) instead of <c>values.Contains(...)</c>. Stored vectors
/// (arrays) come back only when
/// <see cref="RecordRetrievalOptions.IncludeVectors"/> is <see langword="true"/>; otherwise RavenDB leaves them out (the query
/// selects every other field, and gets by key query the ids). Text that RavenDB embeds itself always comes back.
/// </para>
/// <para>
/// Gets by key are ACID. Filtered gets and searches use RavenDB auto-indexes, which are eventually consistent; set
/// <see cref="RavenCollectionOptions.WaitForNonStaleResults"/> to read your own writes.
/// </para>
/// <para>
/// <see cref="VectorSearchResult{TRecord}.Score"/> is RavenDB's <c>@index-score</c> (cosine similarity), reported only
/// when the database setting <c>Indexing.Corax.IncludeDocumentScore</c> is <see langword="true"/>; otherwise it is
/// <see langword="null"/>. <see cref="VectorSearchOptions{TRecord}.ScoreThreshold"/> is passed to RavenDB as the minimum
/// similarity; <see langword="null"/> uses the database's <c>Indexing.Corax.VectorSearch.DefaultMinimumSimilarity</c>.
/// </para>
/// </remarks>
/// <typeparam name="TKey">The key type. The document id is <c>{collection}/{key}</c>, with the key formatted in the invariant
/// culture; a null or empty string key is rejected. Keys are case-insensitive, as RavenDB document ids are.</typeparam>
/// <typeparam name="TRecord">The record type.</typeparam>
#pragma warning disable CA1711 // Identifiers should not have incorrect suffix: matches the MEVD naming convention.
public class RavenCollection<TKey, TRecord> : VectorStoreCollection<TKey, TRecord>, IKeywordHybridSearchable<TRecord>
#pragma warning restore CA1711
    where TKey : notnull
    where TRecord : class
{
    private readonly IDocumentStore _store;
    private readonly RavenCollectionOptions _options;
    private readonly CollectionModel _model;
    private readonly VectorStoreCollectionMetadata _metadata;
    private readonly Dictionary<PropertyModel, string> _fieldNames;
    private readonly string[]? _fieldsWithoutVectors;

    /// <summary>Initializes a new instance of the <see cref="RavenCollection{TKey, TRecord}"/> class.</summary>
    /// <param name="store">An initialized RavenDB document store.</param>
    /// <param name="name">The RavenDB collection name.</param>
    /// <param name="options">Optional configuration.</param>
    [RequiresDynamicCode("This constructor is incompatible with NativeAOT. For dynamic mapping use RavenDynamicCollection.")]
    [RequiresUnreferencedCode("This constructor is incompatible with trimming. For dynamic mapping use RavenDynamicCollection.")]
    public RavenCollection(IDocumentStore store, string name, RavenCollectionOptions? options = null)
        : this(store, name, BuildModel(options), options)
    {
    }

    internal RavenCollection(IDocumentStore store, string name, CollectionModel model, RavenCollectionOptions? options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? new RavenCollectionOptions();
        _model = model;
        _fieldNames = model.Properties.ToDictionary(p => p, p => FieldName(store.Conventions, p));
        var vectorFields = model.VectorProperties.Where(v => !RavenModelBuilder.UsesServerSideEmbeddings(v)).Select(v => _fieldNames[v]).ToList();
        if (vectorFields.Count > 0)
        {
            _fieldsWithoutVectors = [.. RecordFields(store, model).Except(vectorFields)];
        }
        if (model.VectorProperties.Where(RavenModelBuilder.NeedsArrayConversion).Select(v => $"'{v.ModelName}'").ToList() is { Count: > 0 } vectors
            && !RavenVectorDataDocumentStoreExtensions.UsesVectorData(store.Conventions))
        {
            throw new InvalidOperationException($"The vectors {string.Join(", ", vectors)} of '{name}' are ReadOnlyMemory<float> or Embedding<float>, which RavenDB can't serialize by itself: call store.UseVectorData() before store.Initialize(), or declare them as float[].");
        }

        if (store.Conventions.GetIdentityProperty(typeof(TRecord)) is { } idProperty)
        {
            throw new NotSupportedException($"'{typeof(TRecord).Name}.{idProperty.Name}' is RavenDB's document id property, so it can't be part of a vector record. Rename it, e.g. to Key.");
        }

        Name = name;
        _metadata = new VectorStoreCollectionMetadata
        {
            VectorStoreSystemName = RavenVectorStore.SystemName,
            VectorStoreName = _options.Database ?? store.Database,
            CollectionName = name,
        };
    }

    /// <inheritdoc />
    public override string Name { get; }

    /// <inheritdoc />
    public override Task<bool> CollectionExistsAsync(CancellationToken cancellationToken = default) =>
        RavenVectorStore.CollectionExistsAsync(_store, _options.Database, Name, cancellationToken);

    /// <inheritdoc />
    public override Task EnsureCollectionExistsAsync(CancellationToken cancellationToken = default) =>
        RavenVectorStore.EnsureCollectionAsync(_store, _options.Database, Name, cancellationToken);

    /// <inheritdoc />
    public override Task EnsureCollectionDeletedAsync(CancellationToken cancellationToken = default) =>
        RavenVectorStore.DeleteCollectionAsync(_store, _options.Database, Name, cancellationToken);

    /// <inheritdoc />
    public override async Task<TRecord?> GetAsync(TKey key, RecordRetrievalOptions? options = null, CancellationToken cancellationToken = default)
    {
        await foreach (var record in GetAsync([key], options, cancellationToken).ConfigureAwait(false))
        {
            return record;
        }

        return null;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<TRecord> GetAsync(IEnumerable<TKey> keys, RecordRetrievalOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var ids = keys.Select(key => DocumentId(key)).ToList();
        if (ids.Count == 0)
        {
            yield break;
        }

        using var session = RavenHelpers.OpenSession(_store, _options.Database, noTracking: true);
        List<TRecord> records;
        if (options?.IncludeVectors is not true && _fieldsWithoutVectors is { } fields)
        {
            records = await session.Advanced.AsyncDocumentQuery<TRecord>(collectionName: Name).WhereIn("id()", ids).SelectFields<TRecord>(fields).ToListAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            records = [.. (await session.LoadAsync<TRecord>(ids, cancellationToken).ConfigureAwait(false)).Values.OfType<TRecord>()];
        }
        foreach (var record in records)
        {
            yield return Loaded(record);
        }
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<TRecord> GetAsync(Expression<Func<TRecord, bool>> filter, int top, FilteredRecordRetrievalOptions<TRecord>? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfLessThan(top, 1);
        options ??= new FilteredRecordRetrievalOptions<TRecord>();

        if (filter.Body is ConstantExpression { Value: false })
        {
            yield break;
        }

        using var session = RavenHelpers.OpenSession(_store, _options.Database, noTracking: true);
        var collection = session.Query<TRecord>(collectionName: Name);
        if (_options.WaitForNonStaleResults)
        {
            collection = collection.Customize(x => x.WaitForNonStaleResults());
        }

        IQueryable<TRecord> filtered = collection;
        if (filter.Body is not ConstantExpression { Value: true })
        {
            filtered = collection.Where(filter, exact: true);
        }

        var query = Sort(filtered, options).Skip(options.Skip).Take(top);
        var records = await Project(query.ToAsyncDocumentQuery(), options.IncludeVectors).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var record in records)
        {
            yield return Loaded(record);
        }
    }

    /// <inheritdoc />
    public override Task UpsertAsync(TRecord record, CancellationToken cancellationToken = default) =>
        UpsertAsync([record], cancellationToken);

    /// <inheritdoc />
    public override async Task UpsertAsync(IEnumerable<TRecord> records, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        using var session = RavenHelpers.OpenSession(_store, _options.Database);
        await RavenVectorStore.StoreMarkerAsync(session, Name, cancellationToken).ConfigureAwait(false);

        var keyProperty = _model.KeyProperty;
        foreach (var record in records)
        {
            if (keyProperty.IsAutoGenerated && keyProperty.GetValueAsObject(record) is Guid guid && guid == Guid.Empty)
            {
                keyProperty.SetValueAsObject(record, Guid.NewGuid());
            }

            await session.StoreAsync(record, DocumentId(keyProperty.GetValueAsObject(record)), cancellationToken).ConfigureAwait(false);
            session.Advanced.GetMetadataFor(record)[Raven.Client.Constants.Documents.Metadata.Collection] = Name;
        }

        await session.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override Task DeleteAsync(TKey key, CancellationToken cancellationToken = default) =>
        DeleteAsync([key], cancellationToken);

    /// <inheritdoc />
    public override async Task DeleteAsync(IEnumerable<TKey> keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        using var session = RavenHelpers.OpenSession(_store, _options.Database);
        foreach (var key in keys)
        {
            session.Delete(DocumentId(key));
        }

        await session.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<VectorSearchResult<TRecord>> SearchAsync<TInput>(TInput searchValue, int top, VectorSearchOptions<TRecord>? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(searchValue);
        ArgumentOutOfRangeException.ThrowIfLessThan(top, 1);
        options ??= new VectorSearchOptions<TRecord>();

        var vectorProperty = _model.GetVectorPropertyOrSingle(options);
        if (options.Filter?.Body is ConstantExpression { Value: false })
        {
            yield break;
        }

        using var session = RavenHelpers.OpenSession(_store, _options.Database);
        var query = VectorSearch(Query(session, options.Filter), vectorProperty, searchValue, options.ScoreThreshold);
        await foreach (var result in ResultsAsync(query, options.Skip, top, options.IncludeVectors, cancellationToken).ConfigureAwait(false))
        {
            yield return result;
        }
    }

    /// <summary>
    /// Searches by vector and by keywords in a full-text indexed property: every vector match is returned, and records that
    /// also match the keywords rank higher (RavenDB adds the full-text relevance to the vector similarity).
    /// </summary>
    /// <inheritdoc cref="IKeywordHybridSearchable{TRecord}.HybridSearchAsync{TInput}(TInput, ICollection{string}, int, HybridSearchOptions{TRecord}?, CancellationToken)"/>
    public async IAsyncEnumerable<VectorSearchResult<TRecord>> HybridSearchAsync<TInput>(TInput searchValue, ICollection<string> keywords, int top, HybridSearchOptions<TRecord>? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        where TInput : notnull
    {
        ArgumentNullException.ThrowIfNull(searchValue);
        ArgumentNullException.ThrowIfNull(keywords);
        ArgumentOutOfRangeException.ThrowIfLessThan(top, 1);
        options ??= new HybridSearchOptions<TRecord>();

        var vectorProperty = _model.GetVectorPropertyOrSingle(new VectorSearchOptions<TRecord> { VectorProperty = options.VectorProperty });
        var textProperty = _model.GetFullTextDataPropertyOrSingle(options.AdditionalProperty);
        if (options.Filter?.Body is ConstantExpression { Value: false })
        {
            yield break;
        }

        using var session = RavenHelpers.OpenSession(_store, _options.Database);
        var query = VectorSearch(Query(session, options.Filter).OpenSubclause(), vectorProperty, searchValue, options.ScoreThreshold)
            .OrElse()
            .Search(_fieldNames[textProperty], string.Join(' ', keywords))
            .CloseSubclause();
        await foreach (var result in ResultsAsync(query, options.Skip, top, options.IncludeVectors, cancellationToken).ConfigureAwait(false))
        {
            yield return result;
        }
    }

    private IAsyncDocumentQuery<TRecord> Query(IAsyncDocumentSession session, Expression<Func<TRecord, bool>>? filter)
    {
        var query = session.Query<TRecord>(collectionName: Name);
        if (_options.WaitForNonStaleResults)
        {
            query = query.Customize(x => x.WaitForNonStaleResults());
        }

        if (filter is null || filter.Body is ConstantExpression { Value: true })
        {
            return query.ToAsyncDocumentQuery();
        }

        return query.Where(filter, exact: true).ToAsyncDocumentQuery().AndAlso();
    }

    private IAsyncDocumentQuery<TRecord> VectorSearch(IAsyncDocumentQuery<TRecord> query, VectorPropertyModel vectorProperty, object searchValue, double? scoreThreshold)
    {
        var field = _fieldNames[vectorProperty];
        var minimumSimilarity = (float?)scoreThreshold;

        var candidates = _options.NumberOfCandidates;
        var exact = _options.ExactSearch || vectorProperty.IndexKind == IndexKind.Flat;

        if (RavenModelBuilder.UsesServerSideEmbeddings(vectorProperty))
        {
            var text = searchValue as string ?? throw new NotSupportedException($"RavenDB embeds '{field}' itself, so search it with a string.");
            var taskId = _options.EmbeddingsTaskIdentifier;
            return query.VectorSearch(f => RavenHelpers.EmbeddedTextField(f, field, taskId), v => v.ByText(text), minimumSimilarity, candidates, exact);
        }

        var vector = searchValue switch
        {
            float[] array => array,
            ReadOnlyMemory<float> memory => memory.ToArray(),
            Embedding<float> embedding => embedding.Vector.ToArray(),
            _ => throw new NotSupportedException($"Search '{field}' with a float[] vector."),
        };
        return query.VectorSearch(f => f.WithEmbedding(field), v => v.ByEmbedding(vector), minimumSimilarity, candidates, exact);
    }

    private async IAsyncEnumerable<VectorSearchResult<TRecord>> ResultsAsync(IAsyncDocumentQuery<TRecord> query, int skip, int top, bool includeVectors, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var scores = new List<double?>();
        var records = await Project(query, includeVectors)
            .Skip(skip)
            .Take(top)
            .AfterQueryExecuted(result => scores.AddRange(result.Results.Cast<BlittableJsonReaderObject>().Select(Score)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        for (var i = 0; i < records.Count; i++)
        {
            yield return new VectorSearchResult<TRecord>(Loaded(records[i]), scores[i]);
        }
    }

    private static double? Score(BlittableJsonReaderObject result)
    {
        if (result.TryGet(Raven.Client.Constants.Documents.Metadata.Key, out BlittableJsonReaderObject? metadata) && metadata!.TryGet(Raven.Client.Constants.Documents.Metadata.IndexScore, out double score))
        {
            return score;
        }

        return null;
    }

    private IAsyncDocumentQuery<TRecord> Project(IAsyncDocumentQuery<TRecord> query, bool includeVectors)
    {
        if (includeVectors || _fieldsWithoutVectors is null)
        {
            return query;
        }

        return query.SelectFields<TRecord>(_fieldsWithoutVectors);
    }

    /// <inheritdoc />
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(VectorStoreCollectionMetadata))
        {
            return _metadata;
        }

        if (serviceType == typeof(IDocumentStore))
        {
            return _store;
        }

        if (serviceType.IsInstanceOfType(this))
        {
            return this;
        }

        return null;
    }

    private protected virtual TRecord Loaded(TRecord record) => record;

    [RequiresDynamicCode("Building a model from a record type is incompatible with NativeAOT.")]
    [RequiresUnreferencedCode("Building a model from a record type is incompatible with trimming.")]
    private static CollectionModel BuildModel(RavenCollectionOptions? options)
    {
        if (typeof(TRecord) == typeof(Dictionary<string, object?>))
        {
            throw new NotSupportedException(VectorDataStrings.NonDynamicCollectionWithDictionaryNotSupported(typeof(RavenDynamicCollection)));
        }

        return new RavenModelBuilder().Build(typeof(TRecord), typeof(TKey), options?.Definition, options?.EmbeddingGenerator);
    }

    private static IEnumerable<string> RecordFields(IDocumentStore store, CollectionModel model)
    {
        if (((JsonSerializer)store.Conventions.Serialization.CreateSerializer()).ContractResolver.ResolveContract(typeof(TRecord)) is JsonObjectContract contract)
        {
            return contract.Properties.Where(p => !p.Ignored).Select(p => p.PropertyName!);
        }

        return model.Properties.Select(p => p.ModelName);
    }

    private static string FieldName(DocumentConventions conventions, PropertyModel property)
    {
        if (property.PropertyInfo is not { } member)
        {
            return property.ModelName;
        }

        var name = member.Name;
        if (conventions.PropertyNameConverter is { } convert && conventions.ShouldApplyPropertyNameConverter(member))
        {
            name = convert(member);
        }

        return LinqPathProvider.HandlePropertyRenames(member, name);
    }

    private static IQueryable<TRecord> Sort(IQueryable<TRecord> query, FilteredRecordRetrievalOptions<TRecord> options)
    {
        if (options.OrderBy?.Invoke(new())?.Values is not { } sorts)
        {
            return query;
        }

        foreach (var sort in sorts)
        {
            if (sort.Ascending)
            {
                query = query.OrderBy(sort.PropertySelector);
            }
            else
            {
                query = query.OrderByDescending(sort.PropertySelector);
            }
        }

        return query;
    }

    private string DocumentId(object? key) => key switch
    {
        null => throw new ArgumentNullException(nameof(key)),
        string { Length: 0 } => throw new ArgumentException("A key can't be empty.", nameof(key)),
        _ => string.Create(CultureInfo.InvariantCulture, $"{Name}/{key}"),
    };
}

/// <summary>A <see cref="RavenCollection{TKey, TRecord}"/> over <see cref="Dictionary{TKey, TValue}"/> records, defined at runtime.</summary>
/// <remarks>Values of the definition's properties come back typed as the definition says; other fields come back JSON-typed.</remarks>
#pragma warning disable CA1711
public sealed class RavenDynamicCollection : RavenCollection<object, Dictionary<string, object?>>
#pragma warning restore CA1711
{
    /// <summary>Initializes a new instance of the <see cref="RavenDynamicCollection"/> class.</summary>
    /// <param name="store">An initialized RavenDB document store.</param>
    /// <param name="name">The RavenDB collection name.</param>
    /// <param name="options">Configuration; <see cref="VectorStoreCollectionOptions.Definition"/> is required.</param>
    public RavenDynamicCollection(IDocumentStore store, string name, RavenCollectionOptions options)
        : this(
            store,
            name,
            new RavenModelBuilder().BuildDynamic(
                options?.Definition ?? throw new ArgumentException("A definition is required for dynamic collections.", nameof(options)),
                options.EmbeddingGenerator),
            options)
    {
    }

    private RavenDynamicCollection(IDocumentStore store, string name, CollectionModel model, RavenCollectionOptions options)
        : base(store, name, model, options)
    {
        _model = model;
    }

    private static readonly JsonSerializer s_values = JsonSerializer.Create(new JsonSerializerSettings { Converters = { VectorJsonConverter.Instance } });

    private readonly CollectionModel _model;

    private protected override Dictionary<string, object?> Loaded(Dictionary<string, object?> record)
    {
        foreach (var property in _model.Properties)
        {
            if (record.TryGetValue(property.ModelName, out var value) && value is not null && !property.Type.IsInstanceOfType(value))
            {
                record[property.ModelName] = JToken.FromObject(value, s_values).ToObject(property.Type, s_values);
            }
        }

        return record;
    }
}

/// <summary>Options for <see cref="RavenCollection{TKey, TRecord}"/>.</summary>
public sealed class RavenCollectionOptions : VectorStoreCollectionOptions
{
    /// <summary>Gets or sets the database name; <see langword="null"/> uses the store's default database.</summary>
    public string? Database { get; set; }

    /// <summary>Gets or sets a value indicating whether queries wait for indexes to catch up with recent writes.</summary>
    public bool WaitForNonStaleResults { get; set; }

    /// <summary>Gets or sets the HNSW candidates per search; <see langword="null"/> uses the database's
    /// <c>Indexing.Corax.VectorSearch.DefaultNumberOfCandidatesForQuerying</c> (16 by default), which caps the results.</summary>
    public int? NumberOfCandidates { get; set; }

    /// <summary>Gets or sets a value indicating whether searches are exact (brute force) instead of approximate (HNSW).
    /// A vector property with <see cref="IndexKind.Flat"/> is always searched exactly.</summary>
    public bool ExactSearch { get; set; }

    /// <summary>Gets or sets an embeddings generation task id for string vector properties; <see langword="null"/> uses RavenDB's built-in model.</summary>
    public string? EmbeddingsTaskIdentifier { get; set; }
}
