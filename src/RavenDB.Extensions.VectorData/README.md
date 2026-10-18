# RavenDB.Extensions.VectorData

A RavenDB vector store for [Microsoft.Extensions.VectorData](https://learn.microsoft.com/dotnet/ai/vector-stores/overview)
(10.11, .NET 8+). Use it with Semantic Kernel, the Agent Framework's memory, or your own code. It depends only on the
VectorData abstractions and the RavenDB client.

- **No embedding service needed.** Make a vector property a `string` and RavenDB turns the text into vectors itself.
- **Plain documents.** Each record is a RavenDB document you can read, query and index like any other.
- **Vector, filtered and hybrid search** (vectors plus keywords) in one query.

```bash
dotnet add package RavenDB.Extensions.VectorData
```

## Quick start

```csharp
public sealed class Hotel
{
    [VectorStoreKey] public string HotelId { get; set; } = "";
    [VectorStoreData(IsIndexed = true)] public string? Name { get; set; }
    [VectorStoreData(IsIndexed = true)] public int Rating { get; set; }
    [VectorStoreData(IsFullTextIndexed = true)] public string? Summary { get; set; }
    [VectorStoreVector(384)] public string? Description { get; set; }   // RavenDB turns this text into a vector
}

var vectorStore = new RavenVectorStore(store);
var hotels = vectorStore.GetCollection<string, Hotel>("Hotels");
await hotels.UpsertAsync(new Hotel { HotelId = "h1", Name = "Seaside", Rating = 5, Description = "A quiet place by the sea" });

await foreach (var hit in hotels.SearchAsync("calm hotel near the beach", top: 5, new() { Filter = h => h.Rating >= 4 }))
    Console.WriteLine($"{hit.Record.Name} {hit.Score}");
```

## Your own vectors

```csharp
[VectorStoreVector(1536)] public float[]? Embedding { get; set; }   // or ReadOnlyMemory<float>, Embedding<float>

await foreach (var hit in hotels.SearchAsync(queryVector, top: 5))
    Console.WriteLine(hit.Record.Name);
```

`ReadOnlyMemory<float>` and `Embedding<float>` need one line when you create the document store:

```csharp
var store = new DocumentStore { Urls = ["http://localhost:8080"], Database = "Hotels" }.UseVectorData().Initialize();
```

## Hybrid search

Vector matches, with the records whose full-text property contains the keywords ranked first:

```csharp
var hybrid = (IKeywordHybridSearchable<Hotel>)hotels;
await foreach (var hit in hybrid.HybridSearchAsync(queryVector, ["pool", "spa"], top: 5))
    Console.WriteLine(hit.Record.Name);
```

## Records defined at runtime

```csharp
var notes = vectorStore.GetDynamicCollection("Notes", new VectorStoreCollectionDefinition
{
    Properties =
    [
        new VectorStoreKeyProperty("Key", typeof(string)),
        new VectorStoreDataProperty("Owner", typeof(string)) { IsIndexed = true },
        new VectorStoreVectorProperty("Text", typeof(string), 384),
    ],
});
await notes.UpsertAsync(new Dictionary<string, object?> { ["Key"] = "n1", ["Owner"] = "alice", ["Text"] = "Buy milk" });
```

## Dependency injection

```csharp
services.AddSingleton(store);
services.AddRavenVectorStore();
services.AddRavenCollection<string, Hotel>("Hotels");   // inject VectorStoreCollection<string, Hotel>
```

## Settings

```csharp
var vectorStore = new RavenVectorStore(store, new RavenVectorStoreOptions
{
    EmbeddingsTaskIdentifier = "openai-embeddings",   // an embeddings generation task instead of RavenDB's built-in model
    WaitForNonStaleResults = true,                    // see your own writes in searches right away (slower)
});

var tuned = new RavenCollection<string, Hotel>(store, "Hotels", new RavenCollectionOptions
{
    NumberOfCandidates = 100,   // better matches, and up to 100 results (default 16)
    ExactSearch = true,         // compare with every record instead of using the vector index
});
```

Search scores come back only when the database has the setting `Indexing.Corax.IncludeDocumentScore = true`; otherwise
`Score` is `null`.

## Good to know

- Each record is the document `{collection}/{key}`. Keys are case-insensitive.
- A record type can't have a property named `Id` (RavenDB uses it for the document id). To store a property under
  another name, use `[JsonProperty("name")]`; `StorageName` isn't supported.
- Filters are RavenDB LINQ queries, and comparisons are case-sensitive. Write "is one of these values" as
  `h.Name.In(names)` and "has any of these values" as `h.Tags.ContainsAny(tags)` (both from `Raven.Client.Documents.Linq`).
  A property that isn't part of the record throws.
- Getting records by key always returns the latest data. Filtered gets and searches use indexes that update in the
  background; set `WaitForNonStaleResults` to see your own changes right away.
- Stored vectors are returned only when you ask for them (`IncludeVectors = true`).
- Embedding generators in your app aren't supported, and only cosine similarity is supported as the distance function.

The package runs Microsoft's VectorData conformance suite: everything RavenDB supports passes, and the one test waiting
on a RavenDB fix is skipped with its issue number.
