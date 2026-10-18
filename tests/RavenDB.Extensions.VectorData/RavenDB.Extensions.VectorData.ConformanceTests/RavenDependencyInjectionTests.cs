using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.VectorData;
using Raven.Client.Documents;
using VectorData.ConformanceTests;
using Xunit;

namespace RavenDB.Extensions.VectorData.ConformanceTests;

// The suite's own record uses MEVD storage names, which are rejected by design (records are stored as-is).
public class RavenDependencyInjectionTests
    : DependencyInjectionTests<RavenVectorStore, RavenCollection<string, RavenDependencyInjectionTests.AsIsRecord>, string, RavenDependencyInjectionTests.AsIsRecord>
{
    // Registration and resolution only: the store is never contacted.
    private static readonly IDocumentStore s_store = new DocumentStore { Urls = ["http://localhost:1"], Database = "DependencyInjection" }.UseVectorData().Initialize();

    public override IEnumerable<Func<IServiceCollection, object?, ServiceLifetime, IServiceCollection>> StoreDelegates =>
    [
        (services, serviceKey, lifetime) => (serviceKey is null
            ? services.AddRavenVectorStore(lifetime: lifetime)
            : services.AddKeyedRavenVectorStore(serviceKey, lifetime: lifetime)).AddSingleton(s_store),
    ];

    public override IEnumerable<Func<IServiceCollection, object?, string, ServiceLifetime, IServiceCollection>> CollectionDelegates =>
    [
        (services, serviceKey, name, lifetime) => (serviceKey is null
            ? services.AddRavenCollection<string, AsIsRecord>(name, lifetime: lifetime)
            : services.AddKeyedRavenCollection<string, AsIsRecord>(serviceKey, name, lifetime: lifetime)).AddSingleton(s_store),
    ];

    protected override void PopulateConfiguration(ConfigurationManager configuration, object? serviceKey = null)
    {
    }

    // RavenDB embeds text itself, so a generator registered for something else in the app is deliberately not picked up.
    public override void EmbeddingGeneratorIsResolved(ServiceLifetime lifetime, object? serviceKey)
    {
        foreach (var registrationDelegate in CollectionDelegates)
        {
            var services = CreateServices(serviceKey);
            var resolved = false;
            services.AddSingleton<IEmbeddingGenerator>(_ =>
            {
                resolved = true;
                return null!;
            });
            registrationDelegate(services, serviceKey, CollectionName, lifetime);

            using var provider = services.BuildServiceProvider();
            using var collection = serviceKey is null
                ? provider.GetRequiredService<RavenCollection<string, AsIsRecord>>()
                : provider.GetRequiredKeyedService<RavenCollection<string, AsIsRecord>>(serviceKey);
            Assert.False(resolved);
        }
    }

    public sealed class AsIsRecord
    {
        [VectorStoreKey]
        public string Key { get; set; } = string.Empty;

        [VectorStoreData]
        public int Number { get; set; }

        [VectorStoreVector(3)]
        public ReadOnlyMemory<float> Floats { get; set; }
    }
}
