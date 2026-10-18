using Raven.Client.Documents;
using Raven.Client.Documents.Queries.Vector;
using Raven.Client.Documents.Session;

namespace RavenDB.Agents.AI.Internal;

internal static class RavenHelpers
{
    public static IVectorEmbeddingTextField EmbeddedTextField<T>(IVectorFieldFactory<T> factory, string field, string? taskId) =>
        taskId is null ? factory.WithText(field) : factory.WithText(field).UsingTask(taskId);

    public static IAsyncDocumentSession OpenSession(IDocumentStore store, string? database, bool noTracking = false) =>
        store.OpenAsyncSession(new SessionOptions { Database = database, NoTracking = noTracking });

    public static void SetMetadata(IAsyncDocumentSession session, object entity, string collection, TimeSpan? timeToLive)
    {
        var metadata = session.Advanced.GetMetadataFor(entity);
        metadata[Raven.Client.Constants.Documents.Metadata.Collection] = collection;
        if (timeToLive is { } ttl)
        {
            metadata[Raven.Client.Constants.Documents.Metadata.Expires] = DateTime.UtcNow.Add(ttl);
        }
    }

    public static async Task<List<T>> LoadAllStartingWithAsync<T>(IAsyncDocumentSession session, string prefix, CancellationToken cancellationToken)
    {
        var results = new List<T>();
        await using var stream = await session.Advanced.StreamAsync<T>(prefix, token: cancellationToken).ConfigureAwait(false);
        while (await stream.MoveNextAsync().ConfigureAwait(false))
        {
            results.Add(stream.Current.Document);
        }

        return results;
    }
}
