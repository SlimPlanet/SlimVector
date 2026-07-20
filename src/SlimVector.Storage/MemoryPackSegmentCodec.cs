using MemoryPack;
using SlimVector.Domain;

namespace SlimVector.Storage;

internal static class MemoryPackSegmentCodec
{
    private static ReadOnlySpan<byte> Magic => "SVS2"u8;

    public static bool IsMemoryPack(ReadOnlySpan<byte> body) => body.StartsWith(Magic);

    public static byte[] Serialize(SegmentPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        SegmentPayloadV2 serialized = new()
        {
            CollectionId = payload.CollectionId,
            Sequence = payload.Sequence,
            CreatedAt = payload.CreatedAt,
            Operations = payload.Operations.Select(ToSerializable).ToArray(),
        };
        byte[] memoryPack = MemoryPackSerializer.Serialize(serialized);
        byte[] result = GC.AllocateUninitializedArray<byte>(Magic.Length + memoryPack.Length);
        Magic.CopyTo(result);
        memoryPack.CopyTo(result, Magic.Length);
        return result;
    }

    public static SegmentPayload Deserialize(ReadOnlySpan<byte> body)
    {
        if (!IsMemoryPack(body))
        {
            throw new InvalidDataException("The segment does not contain a MemoryPack v2 payload.");
        }

        SegmentPayloadV2 serialized = MemoryPackSerializer.Deserialize<SegmentPayloadV2>(body[Magic.Length..])
            ?? throw new InvalidDataException("The MemoryPack segment payload is empty.");
        if (serialized.FormatVersion != 2)
        {
            throw new InvalidDataException($"MemoryPack segment format '{serialized.FormatVersion}' is unsupported.");
        }

        return new SegmentPayload
        {
            FormatVersion = 2,
            CollectionId = serialized.CollectionId,
            Sequence = serialized.Sequence,
            CreatedAt = serialized.CreatedAt,
            Operations = serialized.Operations.Select(ToDomain).ToList(),
        };
    }

    private static SegmentOperationV2 ToSerializable(StorageOperation operation) => new()
    {
        Kind = operation.Kind,
        Id = operation.Id,
        Document = operation.Document is null ? null : ToSerializable(operation.Document),
        Version = operation.Version,
    };

    private static SegmentDocumentV2 ToSerializable(DocumentRecord document) => new()
    {
        Id = document.Id,
        Text = document.Text,
        Vector = document.Vector,
        Metadata = document.Metadata
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => new SegmentMetadataEntryV2
            {
                Key = pair.Key,
                Value = ToSerializable(pair.Value),
            })
            .ToArray(),
        Version = document.Version,
        UpdatedAt = document.UpdatedAt,
    };

    private static SegmentMetadataValueV2 ToSerializable(MetadataValue value) => new()
    {
        Kind = value.Kind,
        StringValue = value.StringValue,
        BooleanValue = value.BooleanValue,
        IntegerValue = value.IntegerValue,
        NumberValue = value.NumberValue,
        DateTimeValue = value.DateTimeValue,
        GuidValue = value.GuidValue,
        StringArrayValue = value.StringArrayValue,
        BooleanArrayValue = value.BooleanArrayValue,
        IntegerArrayValue = value.IntegerArrayValue,
        NumberArrayValue = value.NumberArrayValue,
    };

    private static StorageOperation ToDomain(SegmentOperationV2 operation) => new()
    {
        Kind = operation.Kind,
        Id = operation.Id,
        Document = operation.Document is null ? null : ToDomain(operation.Document),
        Version = operation.Version,
    };

    private static DocumentRecord ToDomain(SegmentDocumentV2 document) => new()
    {
        Id = document.Id,
        Text = document.Text,
        Vector = document.Vector,
        Metadata = document.Metadata.ToDictionary(
            static entry => entry.Key,
            static entry => ToDomain(entry.Value),
            StringComparer.Ordinal),
        Version = document.Version,
        UpdatedAt = document.UpdatedAt,
    };

    private static MetadataValue ToDomain(SegmentMetadataValueV2 value) => new()
    {
        Kind = value.Kind,
        StringValue = value.StringValue,
        BooleanValue = value.BooleanValue,
        IntegerValue = value.IntegerValue,
        NumberValue = value.NumberValue,
        DateTimeValue = value.DateTimeValue,
        GuidValue = value.GuidValue,
        StringArrayValue = value.StringArrayValue,
        BooleanArrayValue = value.BooleanArrayValue,
        IntegerArrayValue = value.IntegerArrayValue,
        NumberArrayValue = value.NumberArrayValue,
    };
}

[MemoryPackable]
internal sealed partial class SegmentPayloadV2
{
    public int FormatVersion { get; set; } = 2;

    public Guid CollectionId { get; set; }

    public long Sequence { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public SegmentOperationV2[] Operations { get; set; } = [];
}

[MemoryPackable]
internal sealed partial class SegmentOperationV2
{
    public DocumentMutationKind Kind { get; set; }

    public string Id { get; set; } = string.Empty;

    public SegmentDocumentV2? Document { get; set; }

    public long Version { get; set; }
}

[MemoryPackable]
internal sealed partial class SegmentDocumentV2
{
    public string Id { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public float[] Vector { get; set; } = [];

    public SegmentMetadataEntryV2[] Metadata { get; set; } = [];

    public long Version { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

[MemoryPackable]
internal sealed partial class SegmentMetadataEntryV2
{
    public string Key { get; set; } = string.Empty;

    public SegmentMetadataValueV2 Value { get; set; } = new();
}

[MemoryPackable]
internal sealed partial class SegmentMetadataValueV2
{
    public MetadataValueKind Kind { get; set; }

    public string? StringValue { get; set; }

    public bool? BooleanValue { get; set; }

    public long? IntegerValue { get; set; }

    public double? NumberValue { get; set; }

    public DateTimeOffset? DateTimeValue { get; set; }

    public Guid? GuidValue { get; set; }

    public string[]? StringArrayValue { get; set; }

    public bool[]? BooleanArrayValue { get; set; }

    public long[]? IntegerArrayValue { get; set; }

    public double[]? NumberArrayValue { get; set; }
}
