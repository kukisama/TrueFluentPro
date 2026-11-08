using GptImageCli;

// Legacy tests may omit connection values: enforce isolation at both entry points.
internal static class OfflineCli
{
    private static string[] Isolate(string[] args) => args.Length == 0 || (args.Length == 1 && args[0] is "-h" or "help")
        ? args : ["--no-config", .. args];
    public static Task<CliOptions> ParseAsync(string[] args) => GptImageCli.CommandLineParser.ParseAsync(Isolate(args));
    public static Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, HttpMessageHandler? handler = null)
        => GptImageCli.CliApplication.RunAsync(Isolate(args), output, error, handler);
}