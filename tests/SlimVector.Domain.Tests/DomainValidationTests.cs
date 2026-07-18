namespace SlimVector.Domain.Tests;

public sealed class DomainValidationTests
{
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
}
