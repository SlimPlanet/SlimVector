namespace SlimVector.Domain.Tests;

public sealed class DomainValidationTests
{
    [Fact]
    public void ProductQuantizationRequiresDimensionDivisibility()
    {
        VectorIndexConfiguration configuration = new()
        {
            Kind = VectorIndexKind.IvfPq,
            PqSubvectorCount = 8,
        };

        DomainException exception = Assert.Throws<DomainException>(() =>
            DomainValidation.ValidateVectorIndex(configuration, dimension: 10));

        Assert.Equal(ErrorCodes.InvalidIndexConfiguration, exception.Code);
    }

    [Fact]
    public void CreateCollectionRejectsUnsafeName()
    {
        DomainException exception = Assert.Throws<DomainException>(() =>
            CollectionDefinition.Create("../escape", 3, DistanceMetric.Cosine));

        Assert.Equal(ErrorCodes.InvalidCollectionName, exception.Code);
    }

    [Fact]
    public void ValidateDocumentRejectsNonFiniteVector()
    {
        DocumentRecord document = new()
        {
            Id = "doc-1",
            Text = "text",
            Vector = [1, float.NaN],
            Metadata = [],
        };

        DomainException exception = Assert.Throws<DomainException>(() => DomainValidation.ValidateDocument(document, 2));
        Assert.Equal(ErrorCodes.InvalidVector, exception.Code);
    }

    [Fact]
    public void MetadataValueRejectsNonFiniteNumber()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MetadataValue.From(double.PositiveInfinity));
    }

    [Fact]
    public void VirtualShardRoutingIsStableAndUsesPersistedPlacement()
    {
        Guid collectionId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        CollectionPlacement placement = CollectionPlacement.Create(collectionId, ["data-0", "data-1"], 64);

        ShardRoute first = placement.Resolve(collectionId, "document-42");
        ShardRoute second = placement.Resolve(collectionId, "document-42");

        Assert.Equal(first, second);
        Assert.InRange(first.ShardId, 0, 63);
        Assert.Equal(1, first.RoutingEpoch);
        Assert.True(first.DataGroupId is "data-0" or "data-1");
        Assert.Equal(64, placement.ReadRoutes().Count);
    }
}
