using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Raven.Client.Documents;
using Raven.Client.Documents.Queries;
using RavenDB.Agents.AI.Internal;

namespace RavenDB.Agents.AI;

/// <summary>
/// Lets an agent answer from your own documents (retrieval-augmented generation): it finds the documents in a RavenDB
/// collection that are closest in meaning to the user's question and gives them to the model.
/// </summary>
/// <remarks>
/// Add <see cref="CreateTextSearchProvider"/>'s result to the agent's context providers, or call <see cref="SearchAsync"/>
/// yourself. RavenDB turns the text into vectors itself, with its built-in model or an embeddings generation task, so you
/// don't need an embedding service.
/// </remarks>
/// <example>
/// <code><![CDATA[
/// var search = new RavenDocumentSearch(store, new RavenDocumentSearchOptions { Collection = "Articles", TextField = "Body", TitleField = "Title" });
/// var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions { AIContextProviders = [search.CreateTextSearchProvider()] });
/// ]]></code>
/// </example>
public sealed class RavenDocumentSearch
{
    private readonly IDocumentStore _store;
    private readonly RavenDocumentSearchOptions _options;
    private readonly QueryData _projection;

    /// <summary>Creates a search over a collection.</summary>
    /// <param name="store">An initialized RavenDB document store.</param>
    /// <param name="options">The collection and fields to search. <see cref="RavenDocumentSearchOptions.Collection"/> and
    /// <see cref="RavenDocumentSearchOptions.TextField"/> are required.</param>
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

    /// <summary>Finds the documents closest in meaning to <paramref name="query"/>.</summary>
    /// <param name="query">The question or text to search for.</param>
    /// <param name="cancellationToken">A token to cancel the search.</param>
    /// <returns>Up to <see cref="RavenDocumentSearchOptions.MaxResults"/> results, best first. Each result's source name is
    /// the title field, or the document id when there's no title field.</returns>
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

    /// <summary>
    /// Creates a context provider that runs this search before each model call, so the agent sees the matching documents.
    /// </summary>
    /// <param name="options">Optional settings, e.g. whether to search on every run or only when the model asks.</param>
    /// <param name="loggerFactory">Optional logging.</param>
    /// <returns>A provider to add to the agent's context providers.</returns>
    public TextSearchProvider CreateTextSearchProvider(TextSearchProviderOptions? options = null, ILoggerFactory? loggerFactory = null) =>
        new(SearchAsync, options, loggerFactory);

    /// <summary>The fields read from a matching document.</summary>
    public sealed class SearchHit
    {
        /// <summary>Gets or sets the document id.</summary>
        public string? Id { get; set; }

        /// <summary>Gets or sets the text of the <see cref="RavenDocumentSearchOptions.TextField"/>.</summary>
        public string? Text { get; set; }

        /// <summary>Gets or sets the value of the <see cref="RavenDocumentSearchOptions.TitleField"/>, if one is set.</summary>
        public string? Title { get; set; }
    }
}

/// <summary>Settings for a <see cref="RavenDocumentSearch"/>.</summary>
public sealed class RavenDocumentSearchOptions : RavenOptions
{
    /// <summary>Gets or sets the collection to search, e.g. <c>Articles</c>. Required.</summary>
    public string Collection { get; set; } = string.Empty;

    /// <summary>Gets or sets the field with the text to search and give to the model, e.g. <c>Body</c>. Required.</summary>
    public string TextField { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a field that names each result for the model, e.g. <c>Title</c>. Defaults to the document id.
    /// </summary>
    public string? TitleField { get; set; }

    /// <summary>
    /// Gets or sets the identifier of a RavenDB embeddings generation task to use, e.g. one that calls OpenAI.
    /// <see langword="null"/> uses RavenDB's built-in embedding model.
    /// </summary>
    public string? EmbeddingsTaskIdentifier { get; set; }

    /// <summary>
    /// Gets or sets how similar a document must be to be returned, from 0 to 1. <see langword="null"/> uses the database's default.
    /// </summary>
    public float? MinimumSimilarity { get; set; }

    /// <summary>
    /// Gets or sets how many candidates each search considers. More candidates find better matches but take longer, and
    /// there are never more results than candidates. <see langword="null"/> uses the database's default (16).
    /// </summary>
    public int? NumberOfCandidates { get; set; }

    /// <summary>Gets or sets the maximum number of results. Defaults to 3.</summary>
    public int MaxResults { get; set; } = 3;

    /// <summary>
    /// Gets or sets whether searches wait for RavenDB to finish indexing recent changes. Slower; useful in tests.
    /// </summary>
    public bool WaitForNonStaleResults { get; set; }
}
