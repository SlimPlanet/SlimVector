using System.Buffers.Binary;

namespace SlimVector.Raft;

public static class RaftGroupAssignment
{
    public static string GetDataGroupId(Guid collectionId, IReadOnlyList<string> sortedDataGroupIds)
    {
        ArgumentNullException.ThrowIfNull(sortedDataGroupIds);
        if (sortedDataGroupIds.Count == 0)
        {
            throw new ArgumentException("At least one data group is required.", nameof(sortedDataGroupIds));
        }

        Span<byte> bytes = stackalloc byte[16];
        collectionId.TryWriteBytes(bytes);
        uint hash = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        return sortedDataGroupIds[(int)(hash % (uint)sortedDataGroupIds.Count)];
    }
}
