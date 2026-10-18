using Microsoft.Extensions.VectorData;
using Raven.Client.Documents;
using Raven.TestDriver;
using VectorData.ConformanceTests.Support;

namespace RavenDB.Extensions.VectorData.ConformanceTests;

/// <summary>One embedded RavenDB server and database shared by every conformance test class.</summary>
public sealed class RavenTestStore : TestStore
{
    private Driver? _driver;
    private IDocumentStore? _store;

    private RavenTestStore()
    {
    }

    public static RavenTestStore Instance { get; } = new();

    public IDocumentStore Store => _store ?? throw new InvalidOperationException("Not initialized");

    protected override Task StartAsync()
    {
        _driver = new Driver();
        _store = _driver.GetStore();
        DefaultVectorStore = new RavenVectorStore(_store, new RavenVectorStoreOptions { WaitForNonStaleResults = true });
        return Task.CompletedTask;
    }

    // Records are stored as-is (field names are property names), so MEVD storage names are rejected by design. The suite's
    // records also set them with attributes, which an explicit definition only overrides with a non-null value.
    public override VectorStoreCollection<TKey, TRecord> CreateCollection<TKey, TRecord>(string name, VectorStoreCollectionDefinition definition) =>
        base.CreateCollection<TKey, TRecord>(name, WithPropertyNamesAsStorageNames(definition));

    public override VectorStoreCollection<object, Dictionary<string, object?>> CreateDynamicCollection(string name, VectorStoreCollectionDefinition definition) =>
        base.CreateDynamicCollection(name, WithPropertyNamesAsStorageNames(definition));

    private static VectorStoreCollectionDefinition WithPropertyNamesAsStorageNames(VectorStoreCollectionDefinition definition)
    {
        foreach (var property in definition.Properties)
        {
            property.StorageName = property.Name;
        }

        return definition;
    }

    protected override Task StopAsync()
    {
        _store?.Dispose();
        _driver?.Dispose();
        return Task.CompletedTask;
    }

    // The license (vector search with RavenDB-side embeddings needs one) is read from the environment and never logged.
    private sealed class Driver : RavenTestDriver
    {
        static Driver()
        {
            var license = Environment.GetEnvironmentVariable("RAVEN_LICENSE");
            var licensePath = Environment.GetEnvironmentVariable("RAVEN_License_Path");
            var options = new TestServerOptions();
            if (!string.IsNullOrWhiteSpace(license) || !string.IsNullOrWhiteSpace(licensePath))
            {
                options.Licensing ??= new Raven.Embedded.ServerOptions.LicensingOptions();
                options.Licensing.License = string.IsNullOrWhiteSpace(license) ? null : license;
                options.Licensing.LicensePath = string.IsNullOrWhiteSpace(license) ? licensePath : null;
            }

            ConfigureServer(options);
        }

        public IDocumentStore GetStore() => GetDocumentStore();

        protected override void PreInitialize(IDocumentStore documentStore) => documentStore.UseVectorData();

        protected override void PreConfigureDatabase(Raven.Client.ServerWide.DatabaseRecord databaseRecord)
        {
            databaseRecord.Settings["Indexing.Corax.IncludeDocumentScore"] = "true";
        }
    }
}
