using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Microsoft.Extensions.VectorData.ProviderServices;

namespace RavenDB.Extensions.VectorData;

internal sealed class RavenModelBuilder() : CollectionModelBuilder(s_options)
{
    private const string SupportedVectorTypes = "float[], ReadOnlyMemory<float>, Embedding<float>, or string (embedded by RavenDB itself)";

    private static readonly CollectionModelBuildingOptions s_options = new()
    {
        RequiresAtLeastOneVector = false,
        SupportsMultipleVectors = true,
    };

    internal static bool UsesServerSideEmbeddings(VectorPropertyModel property) => property.Type == typeof(string);

    internal static bool NeedsArrayConversion(VectorPropertyModel property) =>
        property.Type == typeof(ReadOnlyMemory<float>) || property.Type == typeof(ReadOnlyMemory<float>?) || property.Type == typeof(Embedding<float>);

    protected override bool IsDataPropertyTypeValid(Type type, [NotNullWhen(false)] out string? supportedTypes)
    {
        supportedTypes = null;
        return true;
    }

    protected override bool IsVectorPropertyTypeValid(Type type, [NotNullWhen(false)] out string? supportedTypes)
    {
        supportedTypes = SupportedVectorTypes;
        return type == typeof(float[]) || type == typeof(ReadOnlyMemory<float>) || type == typeof(ReadOnlyMemory<float>?) || type == typeof(Embedding<float>);
    }

    protected override void Customize()
    {
        foreach (var vectorProperty in VectorProperties.Where(UsesServerSideEmbeddings))
        {
            vectorProperty.EmbeddingType = typeof(string);
        }
    }

    protected override void ValidateProperty(PropertyModel propertyModel, VectorStoreCollectionDefinition? definition)
    {
        if (propertyModel.StorageName != propertyModel.ModelName)
        {
            throw new NotSupportedException($"Property '{propertyModel.ModelName}' has a custom storage name. Records are stored as-is: rename the field with [JsonProperty(\"...\")] instead, which RavenDB uses for storing and querying.");
        }

        if (propertyModel is VectorPropertyModel vectorProperty)
        {
            if (vectorProperty.EmbeddingGenerator is not null)
            {
                throw new NotSupportedException($"Vector property '{vectorProperty.ModelName}' has an embedding generator. RavenDB generates embeddings itself: make it a string property, and set {nameof(RavenVectorStoreOptions.EmbeddingsTaskIdentifier)} to use an embeddings generation task instead of the built-in model.");
            }

            if (vectorProperty.DistanceFunction is not null and not DistanceFunction.CosineSimilarity)
            {
                throw new NotSupportedException($"Distance function '{vectorProperty.DistanceFunction}' on '{vectorProperty.ModelName}' is not supported. RavenDB vector search uses {DistanceFunction.CosineSimilarity}.");
            }

            if (vectorProperty.IndexKind is not null and not IndexKind.Hnsw and not IndexKind.Flat)
            {
                throw new NotSupportedException($"Index kind '{vectorProperty.IndexKind}' on '{vectorProperty.ModelName}' is not supported. Use {IndexKind.Hnsw} (approximate) or {IndexKind.Flat} (exact).");
            }

            if (UsesServerSideEmbeddings(vectorProperty))
            {
                return;
            }
        }

        base.ValidateProperty(propertyModel, definition);
    }
}
