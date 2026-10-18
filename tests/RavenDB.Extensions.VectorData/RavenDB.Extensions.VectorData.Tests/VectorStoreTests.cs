using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Raven.Client.Documents.Linq;
using RavenDB.Extensions.VectorData;
using Xunit;

namespace RavenDB.Extensions.VectorData.Tests;

public sealed class Hotel
{
    [VectorStoreKey]
    public string HotelId { get; set; } = string.Empty;

    [VectorStoreData(IsIndexed = true)]
    public string? Name { get; set; }

    [VectorStoreData(IsIndexed = true)]
    public int Rating { get; set; }

    [VectorStoreData(IsIndexed = true)]
    public bool Parking { get; set; }

    [VectorStoreData(IsIndexed = true)]
    public string[] Tags { get; set; } = [];

    [VectorStoreData]
    public DateTimeOffset OpenedAt { get; set; }

    [VectorStoreVector(3)]
    public float[]? Embedding { get; set; }
}

public sealed class Article
{
    [VectorStoreKey]
    public Guid ArticleId { get; set; }

    [VectorStoreData]
    public string? Title { get; set; }

    // No embedding generator configured: RavenDB embeds this text with its built-in model.
    [VectorStoreVector(384)]
    public string? Body { get; set; }
}

public class VectorStoreTests : RavenTestBase
{
    private static readonly Hotel[] s_hotels =
    [
        new() { HotelId = "h1", Name = "Seaside", Rating = 5, Parking = true, Tags = ["pool", "beach"], OpenedAt = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.FromHours(2)), Embedding = new[] { 1f, 0f, 0f } },
        new() { HotelId = "h2", Name = "Downtown", Rating = 3, Parking = false, Tags = ["business"], Embedding = new[] { 0f, 1f, 0f } },
        new() { HotelId = "h3", Name = "Lagoon", Rating = 4, Parking = true, Tags = ["pool"], Embedding = new[] { 0.9f, 0.1f, 0f } },
    ];

    private VectorStoreCollection<string, Hotel> Hotels() =>
        new RavenVectorStore(Store, new RavenVectorStoreOptions { WaitForNonStaleResults = true }).GetCollection<string, Hotel>("Hotels");

    private async Task<VectorStoreCollection<string, Hotel>> SeedAsync()
    {
        var hotels = Hotels();
        await hotels.EnsureCollectionExistsAsync();
        await hotels.UpsertAsync(s_hotels);
        return hotels;
    }

    private static async Task<List<string>> SearchIds(VectorStoreCollection<string, Hotel> hotels, float[] query, int top, VectorSearchOptions<Hotel>? options = null) =>
        await hotels.SearchAsync(new ReadOnlyMemory<float>(query), top, options).Select(r => r.Record.HotelId).ToListAsync();

    [Fact]
    public async Task Crud_round_trips_records()
    {
        var hotels = await SeedAsync();

        var h1 = await hotels.GetAsync("h1", new RecordRetrievalOptions { IncludeVectors = true });
        Assert.NotNull(h1);
        Assert.Equal("Seaside", h1.Name);
        Assert.Equal(["pool", "beach"], h1.Tags);
        Assert.Equal(s_hotels[0].OpenedAt, h1.OpenedAt);
        Assert.Equal(s_hotels[0].Embedding, h1.Embedding);

        Assert.Null(await hotels.GetAsync("missing"));
        Assert.Equal(["h1", "h3"], (await hotels.GetAsync(["h1", "h3", "nope"]).Select(h => h.HotelId).ToListAsync()).Order());

        await hotels.UpsertAsync(new Hotel { HotelId = "h2", Name = "Downtown Renovated", Rating = 4, Embedding = new[] { 0f, 1f, 0f } });
        Assert.Equal("Downtown Renovated", (await hotels.GetAsync("h2"))!.Name);

        await hotels.DeleteAsync("h2");
        Assert.Null(await hotels.GetAsync("h2"));

        var store = new RavenVectorStore(Store);
        Assert.True(await store.CollectionExistsAsync("Hotels"));
        Assert.Contains("Hotels", await store.ListCollectionNamesAsync().ToListAsync());
        await hotels.EnsureCollectionDeletedAsync();
        Assert.False(await hotels.CollectionExistsAsync());
        await hotels.EnsureCollectionDeletedAsync(); // deleting a missing collection is a no-op
    }

    [Fact]
    public async Task Search_ranks_by_similarity_with_scores_threshold_and_paging()
    {
        var hotels = await SeedAsync();

        var results = await hotels.SearchAsync(new ReadOnlyMemory<float>([1f, 0f, 0f]), 3).ToListAsync();
        Assert.Equal(["h1", "h3", "h2"], results.Select(r => r.Record.HotelId));
        Assert.Equal(1.0, results[0].Score!.Value, 3);
        Assert.True(results[1].Score > results[2].Score);

        Assert.Equal(["h1", "h3"], await SearchIds(hotels, [1f, 0f, 0f], 3, new() { ScoreThreshold = 0.9 }));
        Assert.Equal(["h3"], await SearchIds(hotels, [1f, 0f, 0f], 1, new() { Skip = 1 }));

        var withVectors = await hotels.SearchAsync(new[] { 1f, 0f, 0f }, 1, new() { IncludeVectors = true }).SingleAsync();
        Assert.Equal([1f, 0f, 0f], withVectors.Record.Embedding!);
    }

    [Fact]
    public async Task Search_applies_filters()
    {
        var hotels = await SeedAsync();
        float[] query = [0f, 1f, 0f];

        Assert.Equal(["h3", "h1"], await SearchIds(hotels, query, 5, new() { Filter = h => h.Parking }));
        Assert.Equal(["h2"], await SearchIds(hotels, query, 5, new() { Filter = h => !h.Parking }));
        Assert.Equal(["h3"], await SearchIds(hotels, query, 5, new() { Filter = h => h.Rating > 3 && h.Rating < 5 }));
        Assert.Equal(["h2", "h1"], await SearchIds(hotels, query, 5, new() { Filter = h => h.Rating == 5 || h.Name == "Downtown" }));
        Assert.Equal(["h2", "h3"], await SearchIds(hotels, query, 5, new() { Filter = h => h.Name != "Seaside" }));
        Assert.Equal(["h3", "h1"], await SearchIds(hotels, query, 5, new() { Filter = h => h.Tags.Contains("pool") }));
        Assert.Equal(["h1"], await SearchIds(hotels, query, 5, new() { Filter = h => h.HotelId == "h1" }));
        Assert.Empty(await SearchIds(hotels, query, 5, new() { Filter = h => h.Name == "seaside" })); // exact, case-sensitive
        Assert.Equal(["h2"], await SearchIds(hotels, query, 5, new() { Filter = h => !(h.Rating > 3) }));

        // RavenDB's own extensions for "one of these values" and "array has any of these".
        string[] names = ["Seaside", "Lagoon"];
        Assert.Equal(["h3", "h1"], await SearchIds(hotels, query, 5, new() { Filter = h => h.Name.In(names) }));
        Assert.Equal(["h2", "h1"], await SearchIds(hotels, query, 5, new() { Filter = h => h.Tags.ContainsAny(new[] { "business", "beach" }) }));
    }

    [Fact]
    public async Task Get_with_filter_supports_ordering_and_paging()
    {
        var hotels = await SeedAsync();

        var byRating = await hotels.GetAsync(h => h.Rating >= 3, 10, new() { OrderBy = o => o.Descending(h => h.Rating) }).Select(h => h.HotelId).ToListAsync();
        Assert.Equal(["h1", "h3", "h2"], byRating);

        var page = await hotels.GetAsync(h => h.Rating > 0, 1, new() { Skip = 1, OrderBy = o => o.Ascending(h => h.Name!) }).Select(h => h.HotelId).ToListAsync();
        Assert.Equal(["h3"], page); // Downtown, [Lagoon], Seaside
    }

    [Fact]
    public async Task String_vectors_are_embedded_by_ravendb_when_no_generator_is_configured()
    {
        var articles = new RavenVectorStore(Store, new() { WaitForNonStaleResults = true }).GetCollection<Guid, Article>("Articles");
        var refunds = new Article { Title = "Returns", Body = "Customers may return any product within 30 days for a full refund." };
        await articles.UpsertAsync(
        [
            refunds, // Guid.Empty key => auto-generated
            new Article { ArticleId = Guid.NewGuid(), Title = "Careers", Body = "We are hiring backend engineers in Tel Aviv." },
        ]);
        Assert.NotEqual(Guid.Empty, refunds.ArticleId);

        var top = await articles.SearchAsync("Can I get my money back?", 1).SingleAsync();
        Assert.Equal("Returns", top.Record.Title);
        Assert.True(top.Score > 0); // RavenDB scores text it embeds itself too

        var withText = await articles.GetAsync(refunds.ArticleId, new() { IncludeVectors = true });
        Assert.Equal(refunds.Body, withText!.Body);

        await Assert.ThrowsAsync<NotSupportedException>(async () => await articles.SearchAsync(new[] { 1f }, 1).ToListAsync());
    }

    [Fact]
    public void Unsupported_configuration_is_rejected_with_a_clear_message()
    {
        var store = new RavenVectorStore(Store);
        VectorStoreCollectionDefinition Definition(params VectorStoreProperty[] properties) => new() { Properties = [new VectorStoreKeyProperty("Key", typeof(string)), .. properties] };

        Assert.Throws<NotSupportedException>(() => store.GetDynamicCollection("Bad", Definition(new VectorStoreVectorProperty("Vec", typeof(float[]), 3) { DistanceFunction = DistanceFunction.EuclideanDistance })));
        Assert.Throws<InvalidOperationException>(() => store.GetDynamicCollection("Bad", Definition(new VectorStoreVectorProperty("Vec", typeof(double[]), 3)))); // MEVD: unsupported vector type
        var renamed = Assert.Throws<NotSupportedException>(() => store.GetDynamicCollection("Bad", Definition(new VectorStoreDataProperty("Name", typeof(string)) { StorageName = "name" })));
        Assert.Contains("JsonProperty", renamed.Message, StringComparison.Ordinal); // RavenDB's own way to rename a field
        Assert.Throws<NotSupportedException>(() => store.GetCollection<string, Dictionary<string, object?>>("Bad"));

        // RavenDB generates embeddings itself: a client-side generator is pointed at EmbeddingsTaskIdentifier instead.
        var generated = Assert.Throws<NotSupportedException>(() => store.GetDynamicCollection("Bad", Definition(new VectorStoreVectorProperty("Text", typeof(string), 3) { EmbeddingGenerator = new BagOfWordsEmbeddingGenerator() })));
        Assert.Contains(nameof(RavenVectorStoreOptions.EmbeddingsTaskIdentifier), generated.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Memory_and_embedding_vectors_need_UseVectorData_on_the_store()
    {
        // A store set up without UseVectorData() (RavenTestBase calls it for every other test).
        using var plain = new Raven.Client.Documents.DocumentStore { Urls = Store.Urls, Database = Store.Database }.Initialize();
        var error = Assert.Throws<InvalidOperationException>(() => new RavenVectorStore(plain).GetCollection<string, Note>("Notes"));
        Assert.Contains("UseVectorData", error.Message, StringComparison.Ordinal);

        // float[] vectors need nothing.
        Assert.NotNull(new RavenVectorStore(plain).GetCollection<string, Hotel>("Hotels"));
    }

    [Fact]
    public async Task Memory_and_embedding_vectors_are_stored_as_arrays_and_read_back()
    {
        var notes = new RavenVectorStore(Store, new RavenVectorStoreOptions { WaitForNonStaleResults = true }).GetCollection<string, Note>("Notes");
        await notes.EnsureCollectionExistsAsync();
        await notes.UpsertAsync(new Note { Key = "n1", Comment = "not in the MEVD model", Memory = new float[] { 1, 0, 0 }, Embedding = new Embedding<float>(new float[] { 0, 1, 0 }) });

        // RavenDB's serializer alone writes ReadOnlyMemory<float> as {"Length":3,"IsEmpty":false}, losing the vector.
        using (var session = Store.OpenAsyncSession())
        {
            var raw = await session.LoadAsync<Newtonsoft.Json.Linq.JObject>("Notes/n1");
            Assert.Equal([1f, 0f, 0f], raw["Memory"]!.ToObject<float[]>()!);
            Assert.Equal([0f, 1f, 0f], raw["Embedding"]!.ToObject<float[]>()!);
        }

        var note = await notes.GetAsync("n1", new() { IncludeVectors = true });
        Assert.Equal([1f, 0f, 0f], note!.Memory.ToArray());
        Assert.Equal([0f, 1f, 0f], note.Embedding!.Vector.ToArray());

        // MEVD's default: RavenDB leaves the vectors out; every other field, in the model or not, comes back.
        var withoutVectors = await notes.GetAsync("n1");
        Assert.True(withoutVectors!.Memory.IsEmpty);
        Assert.Null(withoutVectors.Embedding);
        Assert.Equal("not in the MEVD model", withoutVectors.Comment);

        var found = await notes.SearchAsync(new float[] { 0, 1, 0 }, 1, new() { VectorProperty = n => n.Embedding, IncludeVectors = true }).SingleAsync();
        Assert.Equal([1f, 0f, 0f], found.Record.Memory.ToArray());
    }

    [Fact]
    public async Task Hybrid_search_ranks_keyword_matches_first_within_the_filter()
    {
        var snacks = new RavenVectorStore(Store, new RavenVectorStoreOptions { WaitForNonStaleResults = true }).GetCollection<string, Snack>("Snacks");
        await snacks.EnsureCollectionExistsAsync();
        float[] same = [1, 0, 0];
        await snacks.UpsertAsync(
        [
            new Snack { Key = "apples", Text = "Apples are a healthy and nourishing snack", Healthy = true, Vector = same },
            new Snack { Key = "oranges", Text = "Oranges are tangy and contain vitamin c", Healthy = true, Vector = same },
            new Snack { Key = "fries", Text = "Fries are salty and crunchy", Healthy = false, Vector = same },
        ]);
        var hybrid = (IKeywordHybridSearchable<Snack>)snacks;

        // Every vector match comes back; the keyword match ranks first.
        var ranked = await hybrid.HybridSearchAsync(same, ["tangy"], top: 3).Select(r => r.Record.Key).ToListAsync();
        Assert.Equal(3, ranked.Count);
        Assert.Equal("oranges", ranked[0]);

        // The filter applies to both halves: a keyword match outside it is not returned.
        var filtered = await hybrid.HybridSearchAsync(same, ["salty"], top: 3, new() { Filter = s => s.Healthy }).Select(r => r.Record.Key).ToListAsync();
        Assert.Equal(["apples", "oranges"], filtered.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_vector_field_renamed_with_JsonProperty_is_stored_and_searched_under_that_name()
    {
        var docs = new RavenVectorStore(Store, new RavenVectorStoreOptions { WaitForNonStaleResults = true }).GetCollection<string, RenamedVector>("Renamed");
        await docs.EnsureCollectionExistsAsync();
        await docs.UpsertAsync([new RenamedVector { Key = "a", Vector = new float[] { 1, 0, 0 } }, new RenamedVector { Key = "b", Vector = new float[] { 0, 1, 0 } }]);

        using (var session = Store.OpenAsyncSession())
        {
            Assert.NotNull((await session.LoadAsync<Newtonsoft.Json.Linq.JObject>("Renamed/a"))["text_embedding"]);
        }

        var nearest = await docs.SearchAsync(new float[] { 0, 1, 0 }, 1, new() { IncludeVectors = true }).SingleAsync();
        Assert.Equal("b", nearest.Record.Key);
        Assert.Equal([0f, 1f, 0f], nearest.Record.Vector.ToArray());
    }
}

/// <summary>A client-side embedding generator, which the RavenDB vector store rejects.</summary>
public sealed class BagOfWordsEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(_ => new Embedding<float>(new float[3]))));

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

public sealed class Note
{
    [VectorStoreKey]
    public string Key { get; set; } = string.Empty;

    public string? Comment { get; set; }

    [VectorStoreVector(3)]
    public ReadOnlyMemory<float> Memory { get; set; }

    [VectorStoreVector(3)]
    public Embedding<float>? Embedding { get; set; }
}

public sealed class RenamedVector
{
    [VectorStoreKey]
    public string Key { get; set; } = string.Empty;

    [VectorStoreVector(3)]
    [Newtonsoft.Json.JsonProperty("text_embedding")]
    public ReadOnlyMemory<float> Vector { get; set; }
}

public sealed class Snack
{
    [VectorStoreKey]
    public string Key { get; set; } = string.Empty;

    [VectorStoreData(IsFullTextIndexed = true)]
    public string Text { get; set; } = string.Empty;

    [VectorStoreData(IsIndexed = true)]
    public bool Healthy { get; set; }

    [VectorStoreVector(3)]
    public float[]? Vector { get; set; }
}
