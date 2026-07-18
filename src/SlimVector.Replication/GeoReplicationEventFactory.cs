using SlimVector.Domain;
using SlimVector.Raft;
using SlimVector.Raft.Commands;

namespace SlimVector.Replication;

public static class GeoReplicationEventFactory
{
    public static GeoReplicationEvent CollectionUpsert(CollectionDefinition collection) => new()
    {
        Kind = GeoReplicationEventKind.CollectionUpsert,
        Writes = [Write(collection, [])],
    };

    public static GeoReplicationEvent CollectionDelete(CollectionDefinition collection) => new()
    {
        Kind = GeoReplicationEventKind.CollectionDelete,
        Writes = [Write(collection, [])],
    };

    public static GeoReplicationEvent DataWrites(IReadOnlyList<CollectionWrite> writes) => new()
    {
        Kind = GeoReplicationEventKind.DataWrites,
        Writes = writes.Select(static write => Write(write.Collection, write.Operations)).ToArray(),
    };

    private static GeoCollectionWrite Write(
        CollectionDefinition collection,
        IReadOnlyList<SlimVector.Storage.StorageOperation> operations) => new()
        {
            Collection = RaftCommandCodec.FromDomain(collection),
            Operations = operations.Select(RaftCommandCodec.FromStorage).ToArray(),
        };
}
