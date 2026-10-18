using Raven.Client.Documents;
using Raven.TestDriver;

namespace RavenDB.Extensions.VectorData.Tests;

/// <summary>Each test gets a fresh database on a shared embedded RavenDB server.</summary>
public abstract class RavenTestBase : RavenTestDriver
{
    protected IDocumentStore Store { get; }

    // A license (from the environment, never logged) enables RavenDB's built-in embeddings model.
    static RavenTestBase()
    {
        var license = Environment.GetEnvironmentVariable("RAVEN_LICENSE");
        var licensePath = Environment.GetEnvironmentVariable("RAVEN_License_Path");
        if (string.IsNullOrWhiteSpace(license) && string.IsNullOrWhiteSpace(licensePath))
        {
            return;
        }

        var options = new TestServerOptions();
        options.Licensing ??= new Raven.Embedded.ServerOptions.LicensingOptions();
        options.Licensing.License = string.IsNullOrWhiteSpace(license) ? null : license;
        options.Licensing.LicensePath = string.IsNullOrWhiteSpace(license) ? licensePath : null;
        ConfigureServer(options);
    }

    protected RavenTestBase()
    {
        Store = GetDocumentStore();
    }

    // Vector records may use ReadOnlyMemory<float> / Embedding<float>.
    protected override void PreInitialize(IDocumentStore documentStore) => documentStore.UseVectorData();

    // Vector search scores come back only when the database reports them.
    protected override void PreConfigureDatabase(Raven.Client.ServerWide.DatabaseRecord databaseRecord)
    {
        databaseRecord.Settings["Indexing.Corax.IncludeDocumentScore"] = "true";
    }

    public override void Dispose()
    {
        Store.Dispose();
        base.Dispose();
    }
}
