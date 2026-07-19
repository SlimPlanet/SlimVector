using System.Buffers.Binary;
using SlimVector.Domain;

namespace SlimVector.Application.Indexes;

internal sealed record IndexGenerationManifest(
    long ActiveGeneration,
    long? PreviousGeneration,
    VectorIndexKind ActiveKind,
    VectorIndexKind? PreviousKind,
    DateTimeOffset ActivatedAt);

internal static class IndexGenerationManifestCodec
{
    private const int FormatVersion = 2;
    private const int VersionOneSize = sizeof(int) + sizeof(long) + sizeof(long) + sizeof(int) + sizeof(long);
    private const int Size = VersionOneSize + sizeof(int);

    public static byte[] Serialize(IndexGenerationManifest manifest)
    {
        byte[] data = new byte[Size];
        Span<byte> span = data;
        BinaryPrimitives.WriteInt32LittleEndian(span, FormatVersion);
        BinaryPrimitives.WriteInt64LittleEndian(span[4..], manifest.ActiveGeneration);
        BinaryPrimitives.WriteInt64LittleEndian(span[12..], manifest.PreviousGeneration ?? -1);
        BinaryPrimitives.WriteInt32LittleEndian(span[20..], (int)manifest.ActiveKind);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], manifest.PreviousKind.HasValue ? (int)manifest.PreviousKind.Value : -1);
        BinaryPrimitives.WriteInt64LittleEndian(span[28..], manifest.ActivatedAt.UtcTicks);
        return data;
    }

    public static IndexGenerationManifest? Deserialize(ReadOnlySpan<byte> data)
    {
        if (data.Length < sizeof(int))
        {
            return null;
        }

        int version = BinaryPrimitives.ReadInt32LittleEndian(data);
        if (version is not 1 and not FormatVersion ||
            version == 1 && data.Length != VersionOneSize ||
            version == FormatVersion && data.Length != Size)
        {
            return null;
        }

        long active = BinaryPrimitives.ReadInt64LittleEndian(data[4..]);
        long previous = BinaryPrimitives.ReadInt64LittleEndian(data[12..]);
        VectorIndexKind kind = (VectorIndexKind)BinaryPrimitives.ReadInt32LittleEndian(data[20..]);
        int previousKindValue = version == 1 ? -1 : BinaryPrimitives.ReadInt32LittleEndian(data[24..]);
        VectorIndexKind? previousKind = previousKindValue < 0 ? null : (VectorIndexKind)previousKindValue;
        long ticks = BinaryPrimitives.ReadInt64LittleEndian(data[(version == 1 ? 24 : 28)..]);
        if (active < 1 || previous < -1 || !Enum.IsDefined(kind) || kind == VectorIndexKind.Auto ||
            previousKind.HasValue && (!Enum.IsDefined(previousKind.Value) || previousKind == VectorIndexKind.Auto) ||
            version != 1 && (previous < 0) != !previousKind.HasValue ||
            ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks)
        {
            return null;
        }

        return new IndexGenerationManifest(
            active,
            previous < 0 ? null : previous,
            kind,
            previousKind,
            new DateTimeOffset(ticks, TimeSpan.Zero));
    }
}
