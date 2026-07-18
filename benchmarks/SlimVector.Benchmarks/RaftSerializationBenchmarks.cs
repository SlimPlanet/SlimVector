using BenchmarkDotNet.Attributes;
using SlimVector.Domain;
using SlimVector.Raft.Commands;
using SlimVector.Storage;

namespace SlimVector.Benchmarks;

[MemoryDiagnoser]
public class RaftSerializationBenchmarks
{
    private RaftCommandEnvelope _command = null!;
    private byte[] _payload = null!;

    [Params(1, 32, 256)]
    public int BatchSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        CollectionDefinition collection = CollectionDefinition.Create("serialization", 32, DistanceMetric.Cosine);
        StorageOperation[] operations = Enumerable.Range(0, BatchSize).Select(index => StorageOperation.Upsert(new DocumentRecord
        {
            Id = $"document-{index}",
            Text = "serialized raft document",
            Vector = Enumerable.Repeat((float)index, 32).ToArray(),
            Metadata = new Dictionary<string, MetadataValue>(StringComparer.Ordinal)
            {
                ["ordinal"] = MetadataValue.From((long)index),
            },
            Version = 1,
        })).ToArray();
        _command = RaftCommandCodec.DataBatch(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), "data-0", collection, operations);
        _payload = RaftCommandCodec.Serialize(_command);
    }

    [Benchmark]
    public byte[] Serialize() => RaftCommandCodec.Serialize(_command);

    [Benchmark]
    public RaftCommandEnvelope Deserialize() => RaftCommandCodec.Deserialize(_payload);
}
