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
/// A vector store collection that keeps its records as documents of a RavenDB collection.
/// </summary>
/// <remarks>
/// <para><b>Records</b></para>
/// <list type="bullet">
/// <item><description>Each record is the document <c>{collection}/{key}</c>, saved as it is, so you can read and query it
/// like any other RavenDB document. Keys are case-insensitive.</description></item>
/// <item><description>A record type can't have a property named <c>Id</c>, since RavenDB uses it for the document id.</description></item>
/// <item><description>To store a property under another field name, use <c>[JsonProperty("name")]</c>.</description></item>
/// </list>
/// <para><b>Vectors</b></para>
/// <list type="bullet">
/// <item><description>Store your own vectors as <c>float[]</c>, <c>ReadOnlyMemory&lt;float&gt;</c> or <c>Embedding&lt;float&gt;</c>.
/// The last two need <see cref="RavenVectorDataDocumentStoreExtensions.UseVectorData{TStore}"/> when you set up the
/// document store.</description></item>
/// <item><description>Or make the vector property a <see cref="string"/>: RavenDB turns the text into a vector itself, with
/// its built-in model or an embeddings generation task (<see cref="RavenCollectionOptions.EmbeddingsTaskIdentifier"/>).
/// Embedding generators in your app aren't supported.</description></item>
/// <item><description>Stored vectors are returned only when you ask for them (<see cref="RecordRetrievalOptions.IncludeVectors"/>).</description></item>
/// </list>
/// <para><b>Filters</b></para>
/// <list type="bullet">
/// <item><description>Filters are RavenDB LINQ queries, and comparisons are case-sensitive.</description></item>
/// <item><description>For "is one of these values", write <c>r.Field.In(values)</c>; for "has any of these values", write
/// <c>r.Tags.ContainsAny(values)</c> (both from <c>Raven.Client.Documents.Linq</c>).</description></item>
/// <item><description>A property that isn't part of the record definition throws <see cref="InvalidOperationException"/>.</description></item>
/// </list>
/// <para><b>Consistency and scores</b></para>
/// <list type="bullet">
/// <item><description>Getting records by key always returns the latest data. Filtered gets and searches go through indexes
/// that update in the background; set <see cref="RavenCollectionOptions.WaitForNonStaleResults"/> to see your own changes
/// right away.</description></item>
/// <item><description><see cref="VectorSearchResult{TRecord}.Score"/> is the cosine similarity. RavenDB reports it only
/// when the database setting <c>Indexing.Corax.IncludeDocumentScore</c> is <c>true</c>; otherwise it's <see langword="null"/>.</description></item>
/// </list>
/// </remarks>
/// <typeparam name="TKey">The key type, e.g. <see cref="string"/>, <see cref="int"/> or <see cref="Guid"/>. Empty string keys
/// aren't allowed.</typeparam>
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

    /// <summary>Creates a collection. Usually you get one from <see cref="RavenVectorStore.GetCollection{TKey, TRecord}"/> instead.</summary>
    /// <param name="store">An initialized RavenDB document store.</param>
    /// <param name="name">The RavenDB collection name, e.g. <c>Hotels</c>.</param>
    /// <param name="options">Optional settings.</param>
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
            RavenFilterValidator.Validate(filter, _model);
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
    /// Searches by vector and by keywords together: you get the vector matches, with the records whose full-text property
    /// also contains the keywords ranked first.
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

        RavenFilterValidator.Validate(filter, _model);
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

/// <summary>
/// A collection whose records are dictionaries, with properties you describe at runtime instead of in a class. Get one from
/// <see cref="RavenVectorStore.GetDynamicCollection"/>.
/// </summary>
/// <remarks>
/// Values of the properties in the definition come back as the types the definition gives them. Other fields of the document
/// come back as plain JSON values (strings, numbers, booleans).
/// </remarks>
#pragma warning disable CA1711
public sealed class RavenDynamicCollection : RavenCollection<object, Dictionary<string, object?>>
#pragma warning restore CA1711
{
    /// <summary>Creates a dynamic collection.</summary>
    /// <param name="store">An initialized RavenDB document store.</param>
    /// <param name="name">The RavenDB collection name, e.g. <c>Notes</c>.</param>
    /// <param name="options">Settings; <see cref="VectorStoreCollectionOptions.Definition"/>, which describes the record's
    /// properties, is required.</param>
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

/// <summary>Settings for a <see cref="RavenCollection{TKey, TRecord}"/>.</summary>
public sealed class RavenCollectionOptions : VectorStoreCollectionOptions
{
    /// <summary>Gets or sets the database to use. <see langword="null"/> uses the document store's default database.</summary>
    public string? Database { get; set; }

    /// <summary>
    /// Gets or sets whether filtered gets and searches wait until RavenDB has indexed your latest changes. Slower, but you
    /// always see your own writes. Getting records by key doesn't need it.
    /// </summary>
    public bool WaitForNonStaleResults { get; set; }

    /// <summary>
    /// Gets or sets how many candidates each search considers. More candidates find better matches but take longer, and a
    /// search never returns more results than this. <see langword="null"/> uses the database's default (16).
    /// </summary>
    public int? NumberOfCandidates { get; set; }

    /// <summary>
    /// Gets or sets whether searches compare the query with every record (exact, slower) instead of using the vector index
    /// (approximate, faster). Vector properties with <see cref="IndexKind.Flat"/> are always searched exactly.
    /// </summary>
    public bool ExactSearch { get; set; }

    /// <summary>
    /// Gets or sets the identifier of a RavenDB embeddings generation task that turns <see cref="string"/> vector properties
    /// into vectors. <see langword="null"/> uses RavenDB's built-in embedding model.
    /// </summary>
    public string? EmbeddingsTaskIdentifier { get; set; }
}
