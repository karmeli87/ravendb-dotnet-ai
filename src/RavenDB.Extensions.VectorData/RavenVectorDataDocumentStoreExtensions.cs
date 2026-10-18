using System.Runtime.CompilerServices;
using Raven.Client.Documents;
using Raven.Client.Documents.Conventions;
using Raven.Client.Json.Serialization.NewtonsoftJson;
using RavenDB.Extensions.VectorData.Internal;

namespace RavenDB.Extensions.VectorData;

/// <summary>Prepares a RavenDB document store for Microsoft.Extensions.VectorData records.</summary>
public static class RavenVectorDataDocumentStoreExtensions
{
    private static readonly ConditionalWeakTable<DocumentConventions, object> s_configured = [];

    /// <summary>
    /// Teaches RavenDB's serializer the vector types Microsoft.Extensions.VectorData uses, <see cref="ReadOnlyMemory{T}"/> of
    /// <see cref="float"/> and <c>Embedding&lt;float&gt;</c>, stored as JSON arrays. Call it before <c>Initialize()</c>; records
    /// with <c>float[]</c> or RavenDB-embedded <see cref="string"/> vectors don't need it.
    /// </summary>
    /// <typeparam name="TStore">The document store type.</typeparam>
    /// <param name="store">A document store that is not initialized yet.</param>
    /// <returns>The same store.</returns>
    /// <exception cref="NotSupportedException">The store doesn't use RavenDB's Newtonsoft serialization.</exception>
    public static TStore UseVectorData<TStore>(this TStore store)
        where TStore : IDocumentStore
    {
        ArgumentNullException.ThrowIfNull(store);
        if (store.Conventions.Serialization is not NewtonsoftJsonSerializationConventions serialization)
        {
            throw new NotSupportedException($"UseVectorData needs RavenDB's {nameof(NewtonsoftJsonSerializationConventions)}, but the store uses {store.Conventions.Serialization.GetType().Name}.");
        }

        var serialize = serialization.CustomizeJsonSerializer;
        var deserialize = serialization.CustomizeJsonDeserializer;
        serialization.CustomizeJsonSerializer = serializer =>
        {
            serialize?.Invoke(serializer);
            serializer.Converters.Add(VectorJsonConverter.Instance);
        };
        serialization.CustomizeJsonDeserializer = serializer =>
        {
            deserialize?.Invoke(serializer);
            serializer.Converters.Add(VectorJsonConverter.Instance);
        };

        s_configured.AddOrUpdate(store.Conventions, true);
        return store;
    }

    internal static bool UsesVectorData(DocumentConventions conventions) => s_configured.TryGetValue(conventions, out _);
}
