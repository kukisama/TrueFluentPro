using System.Reflection;
using System.Text.Json;
using GptImageCli;

internal static class HelpTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        check(CliVersion.Current == typeof(CliVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
            "help: runtime version comes from assembly metadata");
        using var injected = new StringWriter();
        Usage.Write(injected, "9.8.7-test");
        check(injected.ToString().StartsWith("gpt-image 9.8.7-test - "),
            "help: renderer uses its supplied version, not a hard-coded release");

        foreach (var args in new string[][] { [], ["--help"], ["help"], ["-h"], ["--help", "--json"],
            ["queue", "--help"], ["submit", "--help"], ["queue", "--help", "--json"] })
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var exit = QueueApplication.Handles(args)
                ? await QueueApplication.RunAsync(args, output, error)
                : await CliApplication.RunAsync(args, output, error);
            var json = args.Contains("--json");
            var text = (json ? error : output).ToString();
            check(exit == 0 && text.StartsWith($"gpt-image {CliVersion.Current} - ") && text.Contains("CHANGELOG.md"),
                "help: version and changelog visible for " + string.Join(' ', args));
            if (json)
            {
                using var result = JsonDocument.Parse(output.ToString());
                check(result.RootElement.GetProperty("ok").GetBoolean(), "help: JSON stdout remains a valid success report");
            }
            else check(error.ToString() == "", "help: no stderr for normal help");
        }
    }
}