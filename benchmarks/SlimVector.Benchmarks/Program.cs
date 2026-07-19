using BenchmarkDotNet.Running;

namespace SlimVector.Benchmarks;

internal static class Program
{
    public static int Main(string[] args)
    {
        int marker = Array.FindIndex(args, static argument => string.Equals(argument, "--e2e", StringComparison.OrdinalIgnoreCase));
        if (marker >= 0)
        {
            string[] runnerArguments = args.Where((_, index) => index != marker).ToArray();
            return EndToEndBenchmarkRunner.RunAsync(runnerArguments).GetAwaiter().GetResult();
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return 0;
    }
}
