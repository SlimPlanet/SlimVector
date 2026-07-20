using MessagePack;
using MessagePack.Resolvers;
using SlimVector.Protocol;

[assembly: MessagePackKnownFormatter(typeof(JsonElementMessagePackFormatter))]
[assembly: MessagePackKnownFormatter(typeof(VectorIndexConfigurationMessagePackFormatter))]

namespace SlimVector.Client;

[GeneratedMessagePackResolver]
internal sealed partial class ClientMessagePackResolver;

public enum SlimVectorWireFormat
{
    Json,
    MessagePack,
}

internal static class ClientMessagePack
{
    public const string MediaType = "application/vnd.msgpack";

    public static MessagePackSerializerOptions Options { get; } =
        MessagePackSerializerOptions.Standard
            .WithResolver(CompositeResolver.Create(
                [new JsonElementMessagePackFormatter(), new VectorIndexConfigurationMessagePackFormatter()],
                [ClientMessagePackResolver.Instance, StandardResolver.Instance]))
            .WithSecurity(MessagePackSecurity.UntrustedData);
}
