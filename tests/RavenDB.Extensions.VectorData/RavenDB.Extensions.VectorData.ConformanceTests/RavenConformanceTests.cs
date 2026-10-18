using VectorData.ConformanceTests;
using VectorData.ConformanceTests.ModelTests;
using VectorData.ConformanceTests.Support;
using VectorData.ConformanceTests.TypeTests;
using Xunit;

namespace RavenDB.Extensions.VectorData.ConformanceTests;

public class RavenCollectionManagementTests(RavenCollectionManagementTests.Fixture fixture)
    : CollectionManagementTests<string>(fixture), IClassFixture<RavenCollectionManagementTests.Fixture>
{
    public class Fixture : VectorStoreFixture
    {
        public override TestStore TestStore => RavenTestStore.Instance;
    }
}

public class RavenFilterTests(RavenFilterTests.Fixture fixture)
    : FilterTests<string>(fixture), IClassFixture<RavenFilterTests.Fixture>
{
    // Unsupported by RavenDB's LINQ: values.Contains(r.Field) (write r.Field.In(values)) and
    // r.Tags.Any(t => values.Contains(t)) (write r.Tags.ContainsAny(values)). Filters go to RavenDB as-is.
    public override Task Contains_over_inline_string_array() => Assert.ThrowsAsync<InvalidOperationException>(base.Contains_over_inline_string_array);

    public override Task Contains_over_inline_string_array_with_weird_chars() => Assert.ThrowsAsync<InvalidOperationException>(base.Contains_over_inline_string_array_with_weird_chars);

    public override Task Contains_over_inline_int_array() => Assert.ThrowsAsync<InvalidOperationException>(base.Contains_over_inline_int_array);

    public override Task Contains_over_captured_string_array() => Assert.ThrowsAsync<NotSupportedException>(base.Contains_over_captured_string_array);

    public override Task Any_with_Contains_over_inline_string_array() => Assert.ThrowsAsync<InvalidOperationException>(base.Any_with_Contains_over_inline_string_array);

    public override Task Any_with_Contains_over_captured_string_array() => Assert.ThrowsAsync<InvalidOperationException>(base.Any_with_Contains_over_captured_string_array);

    public override Task Any_over_List_with_Contains_over_captured_string_array() => Assert.ThrowsAsync<InvalidOperationException>(base.Any_over_List_with_Contains_over_captured_string_array);

    public override Task Any_with_Contains_over_captured_string_list() => Assert.ThrowsAsync<InvalidOperationException>(base.Any_with_Contains_over_captured_string_list);

    public new class Fixture : FilterTests<string>.Fixture
    {
        public override TestStore TestStore => RavenTestStore.Instance;
    }
}

public class RavenIndexKindTests(RavenIndexKindTests.Fixture fixture)
    : IndexKindTests<string>(fixture), IClassFixture<RavenIndexKindTests.Fixture>
{
    public new class Fixture : IndexKindTests<string>.Fixture
    {
        public override TestStore TestStore => RavenTestStore.Instance;
    }
}

public class RavenDistanceFunctionTests(RavenDistanceFunctionTests.Fixture fixture)
    : DistanceFunctionTests<string>(fixture), IClassFixture<RavenDistanceFunctionTests.Fixture>
{
    // RavenDB vector search supports cosine similarity only.
    public override Task CosineDistance() => Assert.ThrowsAsync<NotSupportedException>(base.CosineDistance);

    public override Task DotProductSimilarity() => Assert.ThrowsAsync<NotSupportedException>(base.DotProductSimilarity);

    public override Task NegativeDotProductSimilarity() => Assert.ThrowsAsync<NotSupportedException>(base.NegativeDotProductSimilarity);

    public override Task EuclideanDistance() => Assert.ThrowsAsync<NotSupportedException>(base.EuclideanDistance);

    public override Task EuclideanSquaredDistance() => Assert.ThrowsAsync<NotSupportedException>(base.EuclideanSquaredDistance);

    public override Task HammingDistance() => Assert.ThrowsAsync<NotSupportedException>(base.HammingDistance);

    public override Task ManhattanDistance() => Assert.ThrowsAsync<NotSupportedException>(base.ManhattanDistance);

    public new class Fixture : DistanceFunctionTests<string>.Fixture
    {
        public override TestStore TestStore => RavenTestStore.Instance;
    }
}

public class RavenHybridSearchTests(RavenHybridSearchTests.VectorAndStringFixture vectorAndStringFixture, RavenHybridSearchTests.MultiTextFixture multiTextFixture)
    : HybridSearchTests<string>(vectorAndStringFixture, multiTextFixture),
      IClassFixture<RavenHybridSearchTests.VectorAndStringFixture>, IClassFixture<RavenHybridSearchTests.MultiTextFixture>
{
    public new class VectorAndStringFixture : HybridSearchTests<string>.VectorAndStringFixture
    {
        public override TestStore TestStore => RavenTestStore.Instance;
    }

    public new class MultiTextFixture : HybridSearchTests<string>.MultiTextFixture
    {
        public override TestStore TestStore => RavenTestStore.Instance;
    }
}

public class RavenBasicModelTests(RavenBasicModelTests.Fixture fixture)
    : BasicModelTests<string>(fixture), IClassFixture<RavenBasicModelTests.Fixture>
{
    public new class Fixture : BasicModelTests<string>.Fixture
    {
        public override TestStore TestStore => RavenTestStore.Instance;
    }
}

public class RavenDynamicModelTests(RavenDynamicModelTests.Fixture fixture)
    : DynamicModelTests<string>(fixture), IClassFixture<RavenDynamicModelTests.Fixture>
{
    public new class Fixture : DynamicModelTests<string>.Fixture
    {
        public override TestStore TestStore => RavenTestStore.Instance;
    }
}

public class RavenMultiVectorModelTests(RavenMultiVectorModelTests.Fixture fixture)
    : MultiVectorModelTests<string>(fixture), IClassFixture<RavenMultiVectorModelTests.Fixture>
{
    public new class Fixture : MultiVectorModelTests<string>.Fixture
    {
        public override TestStore TestStore => RavenTestStore.Instance;
    }
}

public class RavenNoDataModelTests(RavenNoDataModelTests.Fixture fixture)
    : NoDataModelTests<string>(fixture), IClassFixture<RavenNoDataModelTests.Fixture>
{
    public new class Fixture : NoDataModelTests<string>.Fixture
    {
        public override TestStore TestStore => RavenTestStore.Instance;
    }
}

public class RavenNoVectorModelTests(RavenNoVectorModelTests.Fixture fixture)
    : NoVectorModelTests<string>(fixture), IClassFixture<RavenNoVectorModelTests.Fixture>
{
    public new class Fixture : NoVectorModelTests<string>.Fixture
    {
        public override TestStore TestStore => RavenTestStore.Instance;
    }
}

public class RavenDataTypeTests(RavenDataTypeTests.Fixture fixture)
    : DataTypeTests<string, DataTypeTests<string>.DefaultRecord>(fixture), IClassFixture<RavenDataTypeTests.Fixture>
{
    public new class Fixture : DataTypeTests<string, DataTypeTests<string>.DefaultRecord>.Fixture
    {
        public override TestStore TestStore => RavenTestStore.Instance;
    }
}

public class RavenEmbeddingTypeTests(RavenEmbeddingTypeTests.Fixture fixture)
    : EmbeddingTypeTests<string>(fixture), IClassFixture<RavenEmbeddingTypeTests.Fixture>
{
    public new class Fixture : EmbeddingTypeTests<string>.Fixture
    {
        public override TestStore TestStore => RavenTestStore.Instance;
    }
}

public class RavenKeyTypeTests(RavenKeyTypeTests.Fixture fixture)
    : KeyTypeTests(fixture), IClassFixture<RavenKeyTypeTests.Fixture>
{
    public new class Fixture : KeyTypeTests.Fixture
    {
        public override TestStore TestStore => RavenTestStore.Instance;
    }
}

public class RavenTestSuiteImplementationTests : TestSuiteImplementationTests
{
    // Not implemented: client-side embedding generators are rejected (RavenDB embeds strings itself).
    protected override ICollection<Type> IgnoredTestBases { get; } = [typeof(EmbeddingGenerationTests<>)];
}
