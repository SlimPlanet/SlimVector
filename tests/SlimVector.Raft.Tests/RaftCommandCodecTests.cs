using SlimVector.Domain;
using SlimVector.Raft.Commands;
using SlimVector.Storage;

namespace SlimVector.Raft.Tests;

public sealed class RaftCommandCodecTests
{
    [Fact]
    public void DataBatchRoundTripsEveryDomainValueDeterministically()
    {
        Guid commandId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        Guid collectionId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        DateTimeOffset timestamp = new(2026, 7, 18, 10, 11, 12, TimeSpan.Zero);
        CollectionDefinition collection = new()
        {
            Id = collectionId,
            Name = "documents",
            Dimension = 2,
            Metric = DistanceMetric.Cosine,
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
        };
        Dictionary<string, MetadataValue> firstMetadata = new(StringComparer.Ordinal)
        {
            ["z-number"] = MetadataValue.From(42.5),
            ["a-text"] = MetadataValue.From("raft"),
            ["guid"] = MetadataValue.From(Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc")),
            ["strings"] = MetadataValue.From(["a", "b"]),
            ["bools"] = MetadataValue.From([true, false]),
            ["integers"] = MetadataValue.From([1L, 2L]),
            ["numbers"] = MetadataValue.From([1.5, 2.5]),
            ["instant"] = MetadataValue.From(timestamp),
            ["null"] = MetadataValue.Null(),
        };
        Dictionary<string, MetadataValue> reversedMetadata = firstMetadata
            .Reverse()
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        DocumentRecord first = Document(firstMetadata, timestamp);
        DocumentRecord second = Document(reversedMetadata, timestamp);

        RaftCommandEnvelope command = RaftCommandCodec.DataBatch(
            commandId,
            "data-0",
            collection,
            [StorageOperation.Upsert(first), StorageOperation.Delete("old")]);
        RaftCommandEnvelope equivalent = RaftCommandCodec.DataBatch(
            commandId,
            "data-0",
            collection,
            [StorageOperation.Upsert(second), StorageOperation.Delete("old")]);

        byte[] payload = RaftCommandCodec.Serialize(command);
        Assert.Equal(payload, RaftCommandCodec.Serialize(equivalent));

        RaftCommandEnvelope decoded = RaftCommandCodec.Deserialize(payload);
        Assert.Equal(commandId, decoded.CommandId);
        Assert.Equal(collectionId, decoded.DataBatch?.CollectionId);
        Assert.Equal(2, decoded.DataBatch?.Operations.Length);
        StorageOperation restored = RaftCommandCodec.ToStorage(Assert.IsType<RaftStorageOperation>(decoded.DataBatch?.Operations[0]));
        Assert.Equal(first.Id, restored.Document?.Id);
        Assert.Equal(first.Vector, restored.Document?.Vector);
        Assert.Equal(first.Metadata.Keys.Order(StringComparer.Ordinal), restored.Document?.Metadata.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(first.Metadata["numbers"].NumberArrayValue, restored.Document?.Metadata["numbers"].NumberArrayValue);
    }

    [Fact]
    public void RejectsUnsupportedVersionAndMismatchedPayload()
    {
        RaftCommandEnvelope unsupported = RaftCommandCodec.CatalogDelete(Guid.NewGuid(), "catalog", Guid.NewGuid(), "docs");
        unsupported.FormatVersion = 99;
        Assert.Throws<InvalidDataException>(() => RaftCommandCodec.Serialize(unsupported));

        RaftCommandEnvelope mismatched = RaftCommandCodec.CatalogDelete(Guid.NewGuid(), "catalog", Guid.NewGuid(), "docs");
        mismatched.Kind = RaftCommandKind.DataBatch;
        Assert.Throws<InvalidDataException>(() => RaftCommandCodec.Serialize(mismatched));
    }

    private static DocumentRecord Document(Dictionary<string, MetadataValue> metadata, DateTimeOffset timestamp) => new()
    {
        Id = "document-1",
        Text = "distributed vector search",
        Vector = [0.25F, 0.75F],
        Metadata = metadata,
        Version = 7,
        UpdatedAt = timestamp,
    };
}
