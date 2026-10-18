using System.Runtime.CompilerServices;
using Raven.Client.Documents;
using Raven.Client.Documents.Conventions;
using Raven.Client.Json.Serialization.NewtonsoftJson;
using RavenDB.Extensions.VectorData.Internal;

namespace RavenDB.Extensions.VectorData;

/// <summary>Sets up a RavenDB document store for vector store records.</summary>
public static class RavenVectorDataDocumentStoreExtensions
{
    private static readonly ConditionalWeakTable<DocumentConventions, object> s_configured = [];

    /// <summary>
    /// Lets the document store save vectors of type <c>ReadOnlyMemory&lt;float&gt;</c> and <c>Embedding&lt;float&gt;</c>, as
    /// arrays of numbers. Call it before <c>Initialize()</c>.
    /// </summary>
    /// <remarks>
    /// You need it only for those two vector types: <c>float[]</c> vectors and <see cref="string"/> vectors (which RavenDB
    /// turns into vectors itself) work without it. A collection with those vector types tells you when it's missing.
    /// </remarks>
    /// <typeparam name="TStore">The document store type.</typeparam>
    /// <param name="store">A document store that isn't initialized yet.</param>
    /// <returns>The same store, so you can chain <c>Initialize()</c>.</returns>
    /// <exception cref="NotSupportedException">The store uses a custom serializer instead of RavenDB's default one.</exception>
    /// <example>
    /// <code><![CDATA[
    /// var store = new DocumentStore { Urls = ["http://localhost:8080"], Database = "Hotels" }.UseVectorData().Initialize();
    /// ]]></code>
    /// </example>
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
