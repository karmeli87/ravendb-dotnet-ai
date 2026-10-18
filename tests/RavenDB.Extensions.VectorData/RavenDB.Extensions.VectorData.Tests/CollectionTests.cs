using Microsoft.Extensions.VectorData;
using Xunit;

namespace RavenDB.Extensions.VectorData.Tests;

/// <summary>Collection behavior: ids, dates, existence and listing.</summary>
public class CollectionTests : RavenTestBase
{
    public sealed class Event
    {
        [VectorStoreKey]
        public string Key { get; set; } = string.Empty;

        [VectorStoreData(IsIndexed = true)]
        public DateTimeOffset At { get; set; }

        [VectorStoreData(IsIndexed = true)]
        public DateTime Local { get; set; }

        [VectorStoreVector(2)]
        public float[]? Embedding { get; set; }
    }

    private VectorStoreCollection<string, Event> Events() =>
        new RavenVectorStore(Store, new RavenVectorStoreOptions { WaitForNonStaleResults = true }).GetCollection<string, Event>("Events");

    [Fact]
    public async Task Date_range_filters_compare_instants_across_offsets()
    {
        var events = Events();
        await events.UpsertAsync(
        [
            new Event { Key = "1", At = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.FromHours(2)), Embedding = [1f, 0f] }, // 08:00Z
            new Event { Key = "2", At = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero), Embedding = [1f, 0f] },          // 09:00Z
        ]);

        var cutoff = new DateTimeOffset(2026, 1, 1, 8, 30, 0, TimeSpan.Zero);
        Assert.Equal(["1"], await events.GetAsync(e => e.At < cutoff, 10).Select(e => e.Key).ToListAsync());
        Assert.Equal(["2"], await events.GetAsync(e => e.At > cutoff, 10).Select(e => e.Key).ToListAsync());

        // Round-trips as the same instant.
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.FromHours(2)), (await events.GetAsync("1"))!.At);
    }

    [Fact]
    public void A_record_with_an_Id_property_is_rejected()
    {
        // RavenDB maps an Id property to the document id, which filters can't compare exactly.
        var rejected = Assert.Throws<NotSupportedException>(() => new RavenVectorStore(Store).GetCollection<string, IdKeyed>("Items"));
        Assert.Contains("IdKeyed.Id", rejected.Message, StringComparison.Ordinal);
    }

    public sealed class IdKeyed
    {
        [VectorStoreKey]
        public string Id { get; set; } = string.Empty;

        [VectorStoreVector(2)]
        public float[]? Embedding { get; set; }
    }

    [Fact]
    public async Task Collection_exists_after_ensure_and_listing_only_shows_vector_collections()
    {
        var store = new RavenVectorStore(Store);
        var events = store.GetCollection<string, Event>("Events");

        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new { Name = "not a vector collection" }, "others/1");
            await session.SaveChangesAsync();
        }

        Assert.False(await events.CollectionExistsAsync());
        await events.EnsureCollectionExistsAsync();
        Assert.True(await events.CollectionExistsAsync());
        Assert.True(await store.CollectionExistsAsync("Events"));
        Assert.Equal(["Events"], await store.ListCollectionNamesAsync().ToListAsync());

        await events.EnsureCollectionDeletedAsync();
        Assert.False(await events.CollectionExistsAsync());
        Assert.Empty(await store.ListCollectionNamesAsync().ToListAsync());
    }


    [Fact]
    public async Task Vector_keys_are_document_ids_under_the_collection()
    {
        var store = new RavenVectorStore(Store, new RavenVectorStoreOptions { WaitForNonStaleResults = true });
        var hotels = store.GetCollection<string, Hotel>("Hotels");
        await hotels.UpsertAsync(new Hotel { HotelId = "eu/paris/1", Name = "Paris", Embedding = [1f, 0f, 0f] });

        Assert.Equal("Paris", (await hotels.GetAsync("eu/paris/1"))!.Name);
        Assert.Equal("eu/paris/1", (await hotels.SearchAsync(new[] { 1f, 0f, 0f }, 1).SingleAsync()).Record.HotelId);
        Assert.Equal("Paris", (await hotels.GetAsync(h => h.HotelId == "eu/paris/1", 1).SingleAsync()).Name);

        // Any key type: the document id is {collection}/{key}.
        var things = store.GetDynamicCollection("Things", new() { Properties = [new VectorStoreKeyProperty("Key", typeof(int)), new VectorStoreVectorProperty("Vec", typeof(float[]), 3)] });
        await things.UpsertAsync(new Dictionary<string, object?> { ["Key"] = 42, ["Vec"] = new[] { 1f, 0f, 0f } });

        using var session = Store.OpenAsyncSession();
        Assert.True(await session.Advanced.ExistsAsync("Hotels/eu/paris/1"));
        Assert.True(await session.Advanced.ExistsAsync("Things/42"));
    }

    [Fact]
    public async Task Filters_may_only_use_properties_of_the_definition()
    {
        var store = new RavenVectorStore(Store, new RavenVectorStoreOptions { WaitForNonStaleResults = true });
        var things = store.GetDynamicCollection("Things", new()
        {
            Properties = [new VectorStoreKeyProperty("Key", typeof(int)), new VectorStoreDataProperty("Name", typeof(string)), new VectorStoreVectorProperty("Vec", typeof(float[]), 3)],
        });
        await things.UpsertAsync(new Dictionary<string, object?> { ["Key"] = 1, ["Name"] = "a", ["Vec"] = new[] { 1f, 0f, 0f } });

        Assert.Single(await things.GetAsync(r => (string)r["Name"]! == "a", 5).ToListAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await things.GetAsync(r => (string)r["Nmae"]! == "a", 5).ToListAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await things.SearchAsync(new[] { 1f, 0f, 0f }, 1, new() { Filter = r => (string)r["Name = 'x'; DROP"]! == "" }).ToListAsync());
        await Assert.ThrowsAsync<InvalidCastException>(async () => await things.GetAsync(r => (int)r["Name"]! == 1, 5).ToListAsync());
    }
}
