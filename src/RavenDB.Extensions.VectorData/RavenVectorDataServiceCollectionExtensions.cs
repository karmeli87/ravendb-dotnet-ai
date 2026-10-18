using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.VectorData;
using Raven.Client.Documents;

namespace RavenDB.Extensions.VectorData;

/// <summary>
/// Registers the RavenDB vector store and its collections in dependency injection. They use the <see cref="IDocumentStore"/>
/// registered in the container (the app's own store).
/// </summary>
/// <remarks>
/// An <c>IEmbeddingGenerator</c> registered in the container is not used: RavenDB embeds text itself (string vector
/// properties), so a generator meant for something else in the app doesn't conflict with the collections.
/// </remarks>
public static class RavenVectorDataServiceCollectionExtensions
{
    /// <summary>Registers <see cref="RavenVectorStore"/> as itself and as <see cref="VectorStore"/>.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="options">Optional configuration.</param>
    /// <param name="lifetime">The service lifetime.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddRavenVectorStore(this IServiceCollection services, RavenVectorStoreOptions? options = null, ServiceLifetime lifetime = ServiceLifetime.Singleton) =>
        AddKeyedRavenVectorStore(services, null, options, lifetime);

    /// <summary>Registers <see cref="RavenVectorStore"/> as itself and as <see cref="VectorStore"/> under a service key.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="serviceKey">The service key; <see langword="null"/> registers a non-keyed service.</param>
    /// <param name="options">Optional configuration.</param>
    /// <param name="lifetime">The service lifetime.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddKeyedRavenVectorStore(this IServiceCollection services, object? serviceKey, RavenVectorStoreOptions? options = null, ServiceLifetime lifetime = ServiceLifetime.Singleton)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.Add(new ServiceDescriptor(typeof(RavenVectorStore), serviceKey, (sp, _) => new RavenVectorStore(sp.GetRequiredService<IDocumentStore>(), options), lifetime));
        services.TryAdd(new ServiceDescriptor(typeof(VectorStore), serviceKey, (sp, key) => sp.GetRequiredKeyedService<RavenVectorStore>(key), lifetime));
        return services;
    }

    /// <summary>
    /// Registers a <see cref="RavenCollection{TKey, TRecord}"/> as itself, as <see cref="VectorStoreCollection{TKey, TRecord}"/>,
    /// <see cref="IVectorSearchable{TRecord}"/> and <see cref="IKeywordHybridSearchable{TRecord}"/>.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TRecord">The record type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="name">The RavenDB collection name.</param>
    /// <param name="options">Optional configuration.</param>
    /// <param name="lifetime">The service lifetime.</param>
    /// <returns>The service collection.</returns>
    [RequiresDynamicCode("RavenCollection's constructor is incompatible with NativeAOT.")]
    [RequiresUnreferencedCode("RavenCollection's constructor is incompatible with trimming.")]
    public static IServiceCollection AddRavenCollection<TKey, TRecord>(this IServiceCollection services, string name, RavenCollectionOptions? options = null, ServiceLifetime lifetime = ServiceLifetime.Singleton)
        where TKey : notnull
        where TRecord : class =>
        AddKeyedRavenCollection<TKey, TRecord>(services, null, name, options, lifetime);

    /// <summary>
    /// Registers a <see cref="RavenCollection{TKey, TRecord}"/> under a service key, as itself,
    /// <see cref="VectorStoreCollection{TKey, TRecord}"/>, <see cref="IVectorSearchable{TRecord}"/> and
    /// <see cref="IKeywordHybridSearchable{TRecord}"/>.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TRecord">The record type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="serviceKey">The service key; <see langword="null"/> registers a non-keyed service.</param>
    /// <param name="name">The RavenDB collection name.</param>
    /// <param name="options">Optional configuration.</param>
    /// <param name="lifetime">The service lifetime.</param>
    /// <returns>The service collection.</returns>
    [RequiresDynamicCode("RavenCollection's constructor is incompatible with NativeAOT.")]
    [RequiresUnreferencedCode("RavenCollection's constructor is incompatible with trimming.")]
    public static IServiceCollection AddKeyedRavenCollection<TKey, TRecord>(this IServiceCollection services, object? serviceKey, string name, RavenCollectionOptions? options = null, ServiceLifetime lifetime = ServiceLifetime.Singleton)
        where TKey : notnull
        where TRecord : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(name);
        services.Add(new ServiceDescriptor(typeof(RavenCollection<TKey, TRecord>), serviceKey, (sp, _) => new RavenCollection<TKey, TRecord>(sp.GetRequiredService<IDocumentStore>(), name, options), lifetime));
        services.TryAdd(new ServiceDescriptor(typeof(VectorStoreCollection<TKey, TRecord>), serviceKey, (sp, key) => sp.GetRequiredKeyedService<RavenCollection<TKey, TRecord>>(key), lifetime));
        services.TryAdd(new ServiceDescriptor(typeof(IVectorSearchable<TRecord>), serviceKey, (sp, key) => sp.GetRequiredKeyedService<RavenCollection<TKey, TRecord>>(key), lifetime));
        services.TryAdd(new ServiceDescriptor(typeof(IKeywordHybridSearchable<TRecord>), serviceKey, (sp, key) => sp.GetRequiredKeyedService<RavenCollection<TKey, TRecord>>(key), lifetime));
        return services;
    }
}
