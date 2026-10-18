using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.VectorData;
using Raven.Client.Documents;

namespace RavenDB.Extensions.VectorData;

/// <summary>
/// Registers the RavenDB vector store and collections for dependency injection. They use the <see cref="IDocumentStore"/>
/// you've registered.
/// </summary>
/// <remarks>
/// An embedding generator registered in your app isn't used: RavenDB turns text into vectors itself.
/// </remarks>
/// <example>
/// <code><![CDATA[
/// services.AddSingleton(store);
/// services.AddRavenVectorStore();
/// services.AddRavenCollection<string, Hotel>("Hotels");   // inject VectorStoreCollection<string, Hotel>
/// ]]></code>
/// </example>
public static class RavenVectorDataServiceCollectionExtensions
{
    /// <summary>
    /// Registers a <see cref="RavenVectorStore"/>, which you can then inject as <see cref="RavenVectorStore"/> or
    /// <see cref="VectorStore"/>.
    /// </summary>
    /// <param name="services">The services to add to.</param>
    /// <param name="options">Optional settings.</param>
    /// <param name="lifetime">How long each instance lives. Defaults to one instance for the whole app.</param>
    /// <returns>The same services, so you can chain calls.</returns>
    public static IServiceCollection AddRavenVectorStore(this IServiceCollection services, RavenVectorStoreOptions? options = null, ServiceLifetime lifetime = ServiceLifetime.Singleton) =>
        AddKeyedRavenVectorStore(services, null, options, lifetime);

    /// <summary>
    /// Registers a <see cref="RavenVectorStore"/> under a key, e.g. to use two databases. Inject it with that key as
    /// <see cref="RavenVectorStore"/> or <see cref="VectorStore"/>.
    /// </summary>
    /// <param name="services">The services to add to.</param>
    /// <param name="serviceKey">The key to inject it with; <see langword="null"/> registers it without a key.</param>
    /// <param name="options">Optional settings.</param>
    /// <param name="lifetime">How long each instance lives. Defaults to one instance for the whole app.</param>
    /// <returns>The same services, so you can chain calls.</returns>
    public static IServiceCollection AddKeyedRavenVectorStore(this IServiceCollection services, object? serviceKey, RavenVectorStoreOptions? options = null, ServiceLifetime lifetime = ServiceLifetime.Singleton)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.Add(new ServiceDescriptor(typeof(RavenVectorStore), serviceKey, (sp, _) => new RavenVectorStore(sp.GetRequiredService<IDocumentStore>(), options), lifetime));
        services.TryAdd(new ServiceDescriptor(typeof(VectorStore), serviceKey, (sp, key) => sp.GetRequiredKeyedService<RavenVectorStore>(key), lifetime));
        return services;
    }

    /// <summary>
    /// Registers a collection, which you can then inject as <see cref="RavenCollection{TKey, TRecord}"/>,
    /// <see cref="VectorStoreCollection{TKey, TRecord}"/>,
    /// <see cref="IVectorSearchable{TRecord}"/> and <see cref="IKeywordHybridSearchable{TRecord}"/>.
    /// </summary>
    /// <typeparam name="TKey">The record key type.</typeparam>
    /// <typeparam name="TRecord">The record type.</typeparam>
    /// <param name="services">The services to add to.</param>
    /// <param name="name">The RavenDB collection name, e.g. <c>Hotels</c>.</param>
    /// <param name="options">Optional settings.</param>
    /// <param name="lifetime">How long each instance lives. Defaults to one instance for the whole app.</param>
    /// <returns>The same services, so you can chain calls.</returns>
    [RequiresDynamicCode("RavenCollection's constructor is incompatible with NativeAOT.")]
    [RequiresUnreferencedCode("RavenCollection's constructor is incompatible with trimming.")]
    public static IServiceCollection AddRavenCollection<TKey, TRecord>(this IServiceCollection services, string name, RavenCollectionOptions? options = null, ServiceLifetime lifetime = ServiceLifetime.Singleton)
        where TKey : notnull
        where TRecord : class =>
        AddKeyedRavenCollection<TKey, TRecord>(services, null, name, options, lifetime);

    /// <summary>
    /// Registers a collection under a key. Inject it with that key as <see cref="RavenCollection{TKey, TRecord}"/>,
    /// <see cref="VectorStoreCollection{TKey, TRecord}"/>, <see cref="IVectorSearchable{TRecord}"/> and
    /// <see cref="IKeywordHybridSearchable{TRecord}"/>.
    /// </summary>
    /// <typeparam name="TKey">The record key type.</typeparam>
    /// <typeparam name="TRecord">The record type.</typeparam>
    /// <param name="services">The services to add to.</param>
    /// <param name="serviceKey">The key to inject it with; <see langword="null"/> registers it without a key.</param>
    /// <param name="name">The RavenDB collection name, e.g. <c>Hotels</c>.</param>
    /// <param name="options">Optional settings.</param>
    /// <param name="lifetime">How long each instance lives. Defaults to one instance for the whole app.</param>
    /// <returns>The same services, so you can chain calls.</returns>
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
