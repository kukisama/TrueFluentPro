using System.Reflection;

namespace GptImageCli;

internal static class CliVersion
{
    // The SDK generates this attribute from the single Version property in the project file.
    public static string Current { get; } = typeof(CliVersion).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
}