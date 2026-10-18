using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Raven.Client.Documents;
using Raven.Client.Documents.Queries;
using RavenDB.Agents.AI.Internal;

namespace RavenDB.Agents.AI;

/// <summary>
/// Retrieval-augmented generation (RAG) over an existing RavenDB collection, using vector search.
/// </summary>
/// <remarks>
/// MAF's <see cref="TextSearchProvider"/> is sealed and takes a search delegate, so this class supplies that delegate:
/// call <see cref="CreateTextSearchProvider"/> and add the result to the agent's <c>AIContextProviders</c>.
/// RavenDB embeds the text itself, with its built-in model unless <see cref="RavenDocumentSearchOptions.EmbeddingsTaskIdentifier"/> is set.
/// </remarks>
public sealed class RavenDocumentSearch
{
    private readonly IDocumentStore _store;
    private readonly RavenDocumentSearchOptions _options;
    private readonly QueryData _projection;

    /// <summary>Initializes a new instance of the <see cref="RavenDocumentSearch"/> class.</summary>
    /// <param name="store">An initialized RavenDB document store.</param>
    /// <param name="options">The collection and fields to search.</param>
    public RavenDocumentSearch(IDocumentStore store, RavenDocumentSearchOptions options)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Collection);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.TextField);
        _projection = options.TitleField is null
            ? new QueryData([options.TextField], [nameof(SearchHit.Text)])
            : new QueryData([options.TextField, options.TitleField], [nameof(SearchHit.Text), nameof(SearchHit.Title)]);
    }

    /// <summary>Searches the collection. Matches <see cref="TextSearchProvider"/>'s search delegate.</summary>
    /// <param name="query">The text to search for; RavenDB embeds it.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Up to <see cref="RavenDocumentSearchOptions.MaxResults"/> results, each with the title field (or the document id) as its source name.</returns>
    public async Task<IEnumerable<TextSearchProvider.TextSearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        using var session = RavenHelpers.OpenSession(_store, _options.Database, noTracking: true);

        var search = session.Advanced.AsyncDocumentQuery<SearchHit>(collectionName: _options.Collection);
        if (_options.WaitForNonStaleResults)
        {
            search = search.WaitForNonStaleResults();
        }

        var field = _options.TextField;
        var taskId = _options.EmbeddingsTaskIdentifier;
        search = search.VectorSearch(f => RavenHelpers.EmbeddedTextField(f, field, taskId), v => v.ByText(query), _options.MinimumSimilarity, _options.NumberOfCandidates);

        var hits = await search.SelectFields<SearchHit>(_projection).Take(_options.MaxResults).ToListAsync(cancellationToken).ConfigureAwait(false);
        return hits.Select(hit => new TextSearchProvider.TextSearchResult
        {
            SourceName = hit.Title ?? hit.Id,
            Text = hit.Text ?? string.Empty,
            RawRepresentation = hit,
        });
    }

    /// <summary>Creates a MAF <see cref="TextSearchProvider"/> backed by this search.</summary>
    public TextSearchProvider CreateTextSearchProvider(TextSearchProviderOptions? options = null, ILoggerFactory? loggerFactory = null) =>
        new(SearchAsync, options, loggerFactory);

    /// <summary>A search hit projected from the collection.</summary>
    public sealed class SearchHit
    {
        /// <summary>Gets or sets the document id.</summary>
        public string? Id { get; set; }

        /// <summary>Gets or sets the searched text.</summary>
        public string? Text { get; set; }

        /// <summary>Gets or sets the title, if a title field is configured.</summary>
        public string? Title { get; set; }
    }
}

/// <summary>Options for <see cref="RavenDocumentSearch"/>.</summary>
public sealed class RavenDocumentSearchOptions : RavenOptions
{
    /// <summary>Gets or sets the collection to search, e.g. <c>Articles</c>.</summary>
    public string Collection { get; set; } = string.Empty;

    /// <summary>Gets or sets the field holding the text to search and return; RavenDB embeds it itself.</summary>
    public string TextField { get; set; } = string.Empty;

    /// <summary>Gets or sets an optional field used as the source name (defaults to the document id).</summary>
    public string? TitleField { get; set; }

    /// <summary>Gets or sets an embeddings generation task id; <see langword="null"/> uses RavenDB's built-in model.</summary>
    public string? EmbeddingsTaskIdentifier { get; set; }

    /// <summary>Gets or sets the minimum similarity; <see langword="null"/> uses the database's
    /// <c>Indexing.Corax.VectorSearch.DefaultMinimumSimilarity</c>.</summary>
    public float? MinimumSimilarity { get; set; }

    /// <summary>Gets or sets the HNSW candidates; <see langword="null"/> uses the database's
    /// <c>Indexing.Corax.VectorSearch.DefaultNumberOfCandidatesForQuerying</c> (16 by default), which caps the results.</summary>
    public int? NumberOfCandidates { get; set; }

    /// <summary>Gets or sets the maximum number of results. Default 3.</summary>
    public int MaxResults { get; set; } = 3;

    /// <summary>Gets or sets a value indicating whether searches wait for the index to catch up (slower; useful in tests).</summary>
    public bool WaitForNonStaleResults { get; set; }
}
