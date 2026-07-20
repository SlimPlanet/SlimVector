using System.Buffers;
using System.Text.Json;
using MessagePack;
using MessagePack.Formatters;
using SlimVector.Domain;

namespace SlimVector.Protocol;

public sealed class JsonElementMessagePackFormatter : IMessagePackFormatter<JsonElement>
{
    public void Serialize(
        ref MessagePackWriter writer,
        JsonElement value,
        MessagePackSerializerOptions options) => WriteElement(ref writer, value, options);

    public JsonElement Deserialize(
        ref MessagePackReader reader,
        MessagePackSerializerOptions options)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            WriteJson(ref reader, writer, options);
        }

        using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static void WriteElement(
        ref MessagePackWriter writer,
        JsonElement element,
        MessagePackSerializerOptions options)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Undefined:
            case JsonValueKind.Null:
                writer.WriteNil();
                break;
            case JsonValueKind.True:
                writer.Write(true);
                break;
            case JsonValueKind.False:
                writer.Write(false);
                break;
            case JsonValueKind.String:
                writer.Write(element.GetString());
                break;
            case JsonValueKind.Number when element.TryGetInt64(out long integer):
                writer.Write(integer);
                break;
            case JsonValueKind.Number:
                writer.Write(element.GetDouble());
                break;
            case JsonValueKind.Array:
                writer.WriteArrayHeader(element.GetArrayLength());
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteElement(ref writer, item, options);
                }

                break;
            case JsonValueKind.Object:
                int count = element.EnumerateObject().Count();
                writer.WriteMapHeader(count);
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    writer.Write(property.Name);
                    WriteElement(ref writer, property.Value, options);
                }

                break;
            default:
                throw new MessagePackSerializationException($"Unsupported JSON value kind '{element.ValueKind}'.");
        }
    }

    private static void WriteJson(
        ref MessagePackReader reader,
        Utf8JsonWriter writer,
        MessagePackSerializerOptions options)
    {
        switch (reader.NextMessagePackType)
        {
            case MessagePackType.Nil:
                reader.ReadNil();
                writer.WriteNullValue();
                break;
            case MessagePackType.Boolean:
                writer.WriteBooleanValue(reader.ReadBoolean());
                break;
            case MessagePackType.Integer:
                writer.WriteNumberValue(reader.ReadInt64());
                break;
            case MessagePackType.Float:
                double number = reader.ReadDouble();
                if (!double.IsFinite(number))
                {
                    throw new MessagePackSerializationException("Metadata numbers must be finite.");
                }

                writer.WriteNumberValue(number);
                break;
            case MessagePackType.String:
                writer.WriteStringValue(reader.ReadString());
                break;
            case MessagePackType.Array:
                options.Security.DepthStep(ref reader);
                try
                {
                    int arrayLength = reader.ReadArrayHeader();
                    writer.WriteStartArray();
                    for (int index = 0; index < arrayLength; index++)
                    {
                        WriteJson(ref reader, writer, options);
                    }

                    writer.WriteEndArray();
                }
                finally
                {
                    reader.Depth--;
                }

                break;
            case MessagePackType.Map:
                options.Security.DepthStep(ref reader);
                try
                {
                    int mapLength = reader.ReadMapHeader();
                    writer.WriteStartObject();
                    for (int index = 0; index < mapLength; index++)
                    {
                        string key = reader.ReadString() ?? throw new MessagePackSerializationException(
                            "Metadata object keys may not be null.");
                        writer.WritePropertyName(key);
                        WriteJson(ref reader, writer, options);
                    }

                    writer.WriteEndObject();
                }
                finally
                {
                    reader.Depth--;
                }

                break;
            default:
                throw new MessagePackSerializationException(
                    $"MessagePack type '{reader.NextMessagePackType}' is not valid JSON metadata.");
        }
    }
}

internal static class EnumMessagePack
{
    public static void Write<TEnum>(ref MessagePackWriter writer, TEnum value)
        where TEnum : struct, Enum => writer.Write(ToCamelCase(value.ToString()));

    public static TEnum Read<TEnum>(ref MessagePackReader reader)
        where TEnum : struct, Enum
    {
        string value = reader.ReadString() ?? throw new MessagePackSerializationException(
            $"The {typeof(TEnum).Name} value may not be null.");
        return Enum.TryParse(value, ignoreCase: true, out TEnum parsed)
            ? parsed
            : throw new MessagePackSerializationException($"Unknown {typeof(TEnum).Name} value '{value}'.");
    }

    private static string ToCamelCase(string value) => value.Length == 0
        ? value
        : char.ToLowerInvariant(value[0]) + value[1..];
}

[ExcludeFormatterFromSourceGeneratedResolver]
public sealed class DistanceMetricMessagePackFormatter : IMessagePackFormatter<DistanceMetric>
{
    public void Serialize(ref MessagePackWriter writer, DistanceMetric value, MessagePackSerializerOptions options) =>
        EnumMessagePack.Write(ref writer, value);

    public DistanceMetric Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options) =>
        EnumMessagePack.Read<DistanceMetric>(ref reader);
}

[ExcludeFormatterFromSourceGeneratedResolver]
public sealed class NullableDistanceMetricMessagePackFormatter : IMessagePackFormatter<DistanceMetric?>
{
    public void Serialize(ref MessagePackWriter writer, DistanceMetric? value, MessagePackSerializerOptions options) =>
        WriteNullable(ref writer, value);

    public DistanceMetric? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options) =>
        reader.TryReadNil() ? null : EnumMessagePack.Read<DistanceMetric>(ref reader);

    private static void WriteNullable(ref MessagePackWriter writer, DistanceMetric? value)
    {
        if (value.HasValue)
        {
            EnumMessagePack.Write(ref writer, value.Value);
        }
        else
        {
            writer.WriteNil();
        }
    }
}

[ExcludeFormatterFromSourceGeneratedResolver]
public sealed class MetadataOperatorMessagePackFormatter : IMessagePackFormatter<MetadataOperator>
{
    public void Serialize(ref MessagePackWriter writer, MetadataOperator value, MessagePackSerializerOptions options) =>
        EnumMessagePack.Write(ref writer, value);

    public MetadataOperator Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options) =>
        EnumMessagePack.Read<MetadataOperator>(ref reader);
}

[ExcludeFormatterFromSourceGeneratedResolver]
public sealed class SearchModeMessagePackFormatter : IMessagePackFormatter<SearchMode>
{
    public void Serialize(ref MessagePackWriter writer, SearchMode value, MessagePackSerializerOptions options) =>
        EnumMessagePack.Write(ref writer, value);

    public SearchMode Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options) =>
        EnumMessagePack.Read<SearchMode>(ref reader);
}

[ExcludeFormatterFromSourceGeneratedResolver]
public sealed class NullableSearchModeMessagePackFormatter : IMessagePackFormatter<SearchMode?>
{
    public void Serialize(ref MessagePackWriter writer, SearchMode? value, MessagePackSerializerOptions options)
    {
        if (value.HasValue)
        {
            EnumMessagePack.Write(ref writer, value.Value);
        }
        else
        {
            writer.WriteNil();
        }
    }

    public SearchMode? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options) =>
        reader.TryReadNil() ? null : EnumMessagePack.Read<SearchMode>(ref reader);
}

[ExcludeFormatterFromSourceGeneratedResolver]
public sealed class NullableReadConsistencyMessagePackFormatter : IMessagePackFormatter<ReadConsistency?>
{
    public void Serialize(ref MessagePackWriter writer, ReadConsistency? value, MessagePackSerializerOptions options)
    {
        if (value.HasValue)
        {
            EnumMessagePack.Write(ref writer, value.Value);
        }
        else
        {
            writer.WriteNil();
        }
    }

    public ReadConsistency? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options) =>
        reader.TryReadNil() ? null : EnumMessagePack.Read<ReadConsistency>(ref reader);
}

[ExcludeFormatterFromSourceGeneratedResolver]
public sealed class DateTimeOffsetMessagePackFormatter : IMessagePackFormatter<DateTimeOffset>
{
    public void Serialize(ref MessagePackWriter writer, DateTimeOffset value, MessagePackSerializerOptions options) =>
        writer.Write(value.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

    public DateTimeOffset Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        string value = reader.ReadString() ?? throw new MessagePackSerializationException(
            "A DateTimeOffset value may not be null.");
        return DateTimeOffset.TryParseExact(
            value,
            "O",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out DateTimeOffset parsed)
            ? parsed
            : throw new MessagePackSerializationException($"Invalid ISO 8601 DateTimeOffset value '{value}'.");
    }
}

[ExcludeFormatterFromSourceGeneratedResolver]
public sealed class VectorIndexConfigurationMessagePackFormatter : IMessagePackFormatter<VectorIndexConfiguration?>
{
    public void Serialize(
        ref MessagePackWriter writer,
        VectorIndexConfiguration? value,
        MessagePackSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNil();
            return;
        }

        writer.WriteMapHeader(19);
        WriteEnum(ref writer, "kind", value.Kind);
        Write(ref writer, "hnswM", value.HnswM);
        Write(ref writer, "hnswEfConstruction", value.HnswEfConstruction);
        Write(ref writer, "hnswEfSearch", value.HnswEfSearch);
        WriteEnum(ref writer, "quantization", value.Quantization);
        Write(ref writer, "rerankCandidateMultiplier", value.RerankCandidateMultiplier);
        Write(ref writer, "ivfListCount", value.IvfListCount);
        Write(ref writer, "ivfProbeCount", value.IvfProbeCount);
        Write(ref writer, "ivfTrainingIterations", value.IvfTrainingIterations);
        Write(ref writer, "pqSubvectorCount", value.PqSubvectorCount);
        Write(ref writer, "pqCentroidCount", value.PqCentroidCount);
        Write(ref writer, "pqTrainingIterations", value.PqTrainingIterations);
        Write(ref writer, "diskAnnMaxDegree", value.DiskAnnMaxDegree);
        Write(ref writer, "diskAnnSearchListSize", value.DiskAnnSearchListSize);
        Write(ref writer, "diskAnnBeamWidth", value.DiskAnnBeamWidth);
        Write(ref writer, "diskAnnDeltaThreshold", value.DiskAnnDeltaThreshold);
        Write(ref writer, "diskAnnPageSize", value.DiskAnnPageSize);
        Write(ref writer, "diskAnnCachePages", value.DiskAnnCachePages);
        Write(ref writer, "diskAnnRetainedGenerations", value.DiskAnnRetainedGenerations);
    }

    public VectorIndexConfiguration? Deserialize(
        ref MessagePackReader reader,
        MessagePackSerializerOptions options)
    {
        if (reader.TryReadNil())
        {
            return null;
        }

        VectorIndexConfiguration defaults = new();
        VectorIndexKind kind = defaults.Kind;
        int hnswM = defaults.HnswM;
        int hnswEfConstruction = defaults.HnswEfConstruction;
        int hnswEfSearch = defaults.HnswEfSearch;
        VectorQuantizationKind quantization = defaults.Quantization;
        int rerankCandidateMultiplier = defaults.RerankCandidateMultiplier;
        int ivfListCount = defaults.IvfListCount;
        int ivfProbeCount = defaults.IvfProbeCount;
        int ivfTrainingIterations = defaults.IvfTrainingIterations;
        int pqSubvectorCount = defaults.PqSubvectorCount;
        int pqCentroidCount = defaults.PqCentroidCount;
        int pqTrainingIterations = defaults.PqTrainingIterations;
        int diskAnnMaxDegree = defaults.DiskAnnMaxDegree;
        int diskAnnSearchListSize = defaults.DiskAnnSearchListSize;
        int diskAnnBeamWidth = defaults.DiskAnnBeamWidth;
        int diskAnnDeltaThreshold = defaults.DiskAnnDeltaThreshold;
        int diskAnnPageSize = defaults.DiskAnnPageSize;
        int diskAnnCachePages = defaults.DiskAnnCachePages;
        int diskAnnRetainedGenerations = defaults.DiskAnnRetainedGenerations;

        options.Security.DepthStep(ref reader);
        try
        {
            int count = reader.ReadMapHeader();
            for (int index = 0; index < count; index++)
            {
                switch (reader.ReadString())
                {
                    case "kind": kind = EnumMessagePack.Read<VectorIndexKind>(ref reader); break;
                    case "hnswM": hnswM = reader.ReadInt32(); break;
                    case "hnswEfConstruction": hnswEfConstruction = reader.ReadInt32(); break;
                    case "hnswEfSearch": hnswEfSearch = reader.ReadInt32(); break;
                    case "quantization": quantization = EnumMessagePack.Read<VectorQuantizationKind>(ref reader); break;
                    case "rerankCandidateMultiplier": rerankCandidateMultiplier = reader.ReadInt32(); break;
                    case "ivfListCount": ivfListCount = reader.ReadInt32(); break;
                    case "ivfProbeCount": ivfProbeCount = reader.ReadInt32(); break;
                    case "ivfTrainingIterations": ivfTrainingIterations = reader.ReadInt32(); break;
                    case "pqSubvectorCount": pqSubvectorCount = reader.ReadInt32(); break;
                    case "pqCentroidCount": pqCentroidCount = reader.ReadInt32(); break;
                    case "pqTrainingIterations": pqTrainingIterations = reader.ReadInt32(); break;
                    case "diskAnnMaxDegree": diskAnnMaxDegree = reader.ReadInt32(); break;
                    case "diskAnnSearchListSize": diskAnnSearchListSize = reader.ReadInt32(); break;
                    case "diskAnnBeamWidth": diskAnnBeamWidth = reader.ReadInt32(); break;
                    case "diskAnnDeltaThreshold": diskAnnDeltaThreshold = reader.ReadInt32(); break;
                    case "diskAnnPageSize": diskAnnPageSize = reader.ReadInt32(); break;
                    case "diskAnnCachePages": diskAnnCachePages = reader.ReadInt32(); break;
                    case "diskAnnRetainedGenerations": diskAnnRetainedGenerations = reader.ReadInt32(); break;
                    default: reader.Skip(); break;
                }
            }
        }
        finally
        {
            reader.Depth--;
        }

        return new VectorIndexConfiguration
        {
            Kind = kind,
            HnswM = hnswM,
            HnswEfConstruction = hnswEfConstruction,
            HnswEfSearch = hnswEfSearch,
            Quantization = quantization,
            RerankCandidateMultiplier = rerankCandidateMultiplier,
            IvfListCount = ivfListCount,
            IvfProbeCount = ivfProbeCount,
            IvfTrainingIterations = ivfTrainingIterations,
            PqSubvectorCount = pqSubvectorCount,
            PqCentroidCount = pqCentroidCount,
            PqTrainingIterations = pqTrainingIterations,
            DiskAnnMaxDegree = diskAnnMaxDegree,
            DiskAnnSearchListSize = diskAnnSearchListSize,
            DiskAnnBeamWidth = diskAnnBeamWidth,
            DiskAnnDeltaThreshold = diskAnnDeltaThreshold,
            DiskAnnPageSize = diskAnnPageSize,
            DiskAnnCachePages = diskAnnCachePages,
            DiskAnnRetainedGenerations = diskAnnRetainedGenerations,
        };
    }

    private static void Write(ref MessagePackWriter writer, string key, int value)
    {
        writer.Write(key);
        writer.Write(value);
    }

    private static void WriteEnum<TEnum>(
        ref MessagePackWriter writer,
        string key,
        TEnum value)
        where TEnum : struct, Enum
    {
        writer.Write(key);
        EnumMessagePack.Write(ref writer, value);
    }
}
