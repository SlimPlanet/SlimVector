using SlimVector.Domain;

namespace SlimVector.Indexing.Tests;

public sealed class MetadataIndexTests
{
    [Fact]
    public void EvaluateCombinesComparisonInAndNot()
    {
        MetadataIndex index = new();
        index.Upsert("one", new Dictionary<string, MetadataValue>
        {
            ["year"] = MetadataValue.From(2024L),
            ["tags"] = MetadataValue.From(["dotnet", "vector"]),
        });
        index.Upsert("two", new Dictionary<string, MetadataValue>
        {
            ["year"] = MetadataValue.From(2022L),
            ["tags"] = MetadataValue.From(["legacy"]),
        });

        MetadataFilter filter = new()
        {
            Operator = MetadataOperator.And,
            Operands =
            [
                new MetadataFilter
                {
                    Operator = MetadataOperator.GreaterThanOrEqual,
                    Field = "year",
                    Value = MetadataValue.From(2023L),
                },
                new MetadataFilter
                {
                    Operator = MetadataOperator.In,
                    Field = "tags",
                    Values = [MetadataValue.From("vector")],
                },
            ],
        };

        Assert.Equal(["one"], index.Evaluate(filter));
    }

    [Fact]
    public void ExistsUsesIndexAndNotReturnsComplement()
    {
        MetadataIndex index = new();
        index.Upsert("one", new Dictionary<string, MetadataValue> { ["active"] = MetadataValue.From(true) });
        index.Upsert("two", new Dictionary<string, MetadataValue>());

        MetadataFilter filter = new()
        {
            Operator = MetadataOperator.Not,
            Operands =
            [
                new MetadataFilter { Operator = MetadataOperator.Exists, Field = "active" },
            ],
        };

        Assert.Equal(["two"], index.Evaluate(filter));
    }
}
