using System;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace TrueFluentPro.Services;

/// <summary>
/// 统一读取 Assets/versions.json（优先编译期嵌入资源，回退程序集元数据），
/// 同时承载主程序版本（appVersion）与语音输入法版本（voiceImeVersion）。
/// </summary>
public static class AppVersionInfo
{
    private const string FallbackVersion = "2.2.3";
    private const string EmbeddedResourceName = "TrueFluentPro.Assets.versions.json";

    private static readonly Lazy<(string AppVersion, string VoiceImeVersion)> _versions = new(LoadVersions);

    public static string AppVersionString => _versions.Value.AppVersion;

    public static string VoiceImeVersionString => _versions.Value.VoiceImeVersion;

    public static Version ParseAppVersion()
        => TryParseThreePartVersion(AppVersionString, out var version) ? version : new Version(0, 0, 0);

    public static Version ParseVoiceImeVersion()
        => TryParseThreePartVersion(VoiceImeVersionString, out var version) ? version : new Version(0, 0, 0);

    private static (string AppVersion, string VoiceImeVersion) LoadVersions()
    {
        var fallback = GetAssemblyFallbackVersion();
        string? appVersion = null;
        string? voiceImeVersion = null;

        try
        {
            using var stream = typeof(AppVersionInfo).Assembly.GetManifestResourceStream(EmbeddedResourceName);
            if (stream != null)
            {
                using var doc = JsonDocument.Parse(stream);
                var root = doc.RootElement;
                appVersion = TryReadVersionProperty(root, "appVersion");
                voiceImeVersion = TryReadVersionProperty(root, "voiceImeVersion");
            }
        }
        catch
        {
            // Ignore malformed stream and fall back to assembly version.
        }

        if (appVersion == null || voiceImeVersion == null)
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Assets", "versions.json"),
                Path.Combine(AppContext.BaseDirectory, "versions.json"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Assets", "versions.json")
            };

            foreach (var path in candidates)
            {
                if (!File.Exists(path)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    var root = doc.RootElement;
                    appVersion ??= TryReadVersionProperty(root, "appVersion");
                    voiceImeVersion ??= TryReadVersionProperty(root, "voiceImeVersion");
                    if (appVersion != null && voiceImeVersion != null) break;
                }
                catch
                {
                    // Ignore and continue fallback.
                }
            }
        }

        var resolvedApp = appVersion ?? fallback;
        var resolvedVoiceIme = voiceImeVersion ?? resolvedApp;
        return (resolvedApp, resolvedVoiceIme);
    }

    private static string? TryReadVersionProperty(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(propertyName, out var prop)
            || prop.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var raw = prop.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (raw.StartsWith('v') || raw.StartsWith('V'))
            raw = raw[1..].Trim();

        return TryParseThreePartVersion(raw, out var ver)
            ? $"{ver.Major}.{ver.Minor}.{Math.Max(0, ver.Build)}"
            : null;
    }

    private static bool TryParseThreePartVersion(string? raw, out Version version)
    {
        if (!string.IsNullOrWhiteSpace(raw) && Version.TryParse(raw, out var parsed)
            && (parsed.Major > 0 || parsed.Minor > 0 || parsed.Build > 0))
        {
            version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
            return true;
        }

        version = new Version(0, 0, 0);
        return false;
    }

    private static string GetAssemblyFallbackVersion()
    {
        var asmVer = typeof(AppVersionInfo).Assembly.GetName().Version;
        if (asmVer != null && (asmVer.Major > 0 || asmVer.Minor > 0 || asmVer.Build > 0))
            return $"{asmVer.Major}.{asmVer.Minor}.{Math.Max(0, asmVer.Build)}";
        return FallbackVersion;
    }
}
