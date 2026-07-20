using MessagePack;
using MessagePack.Resolvers;

namespace SlimVector.Benchmarks;

[GeneratedMessagePackResolver]
internal sealed partial class BenchmarkMessagePackResolver;

internal static class BenchmarkMessagePack
{
    public const string MediaType = "application/vnd.msgpack";

    public static MessagePackSerializerOptions Options { get; } =
        MessagePackSerializerOptions.Standard
            .WithResolver(CompositeResolver.Create(
                [],
                [BenchmarkMessagePackResolver.Instance, StandardResolver.Instance]))
            .WithSecurity(MessagePackSecurity.UntrustedData);
}
