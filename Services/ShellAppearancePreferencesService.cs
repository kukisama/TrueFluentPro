using System;
using System.IO;
using System.Security;
using System.Text.Json;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services;

/// <summary>
/// 只读写独立的 appearance.json。调用方在 UI 线程串行保存，并负责报告保存异常。
/// 文件格式为单个 appearance 字段，不含任何业务配置。
/// </summary>
public sealed class ShellAppearancePreferencesService
{
    private readonly string _filePath;

    public ShellAppearancePreferencesService(string? filePath = null)
    {
        _filePath = Path.GetFullPath(filePath ?? PathManager.Instance.GetConfigFile("appearance.json"));
    }

    public ShellAppearancePreference Load()
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_filePath));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("appearance", out var appearance) ||
                appearance.ValueKind != JsonValueKind.String)
            {
                return ShellAppearancePreference.Classic;
            }

            return appearance.GetString() switch
            {
                nameof(ShellAppearancePreference.SongStudy) => ShellAppearancePreference.SongStudy,
                nameof(ShellAppearancePreference.InkStudy) => ShellAppearancePreference.InkStudy,
                _ => ShellAppearancePreference.Classic
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or SecurityException)
        {
            return ShellAppearancePreference.Classic;
        }
    }

    public void Save(ShellAppearancePreference appearance)
    {
        if (appearance is not ShellAppearancePreference.Classic and not ShellAppearancePreference.SongStudy
            and not ShellAppearancePreference.InkStudy)
        {
            throw new ArgumentOutOfRangeException(nameof(appearance), appearance, "无法保存未定义的窗口外观。");
        }

        var directory = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { appearance = appearance.ToString() });
        var temporaryCreated = false;

        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                temporaryCreated = true;
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            // 只清理本次成功创建的临时文件；清理失败不掩盖原始保存异常。
            if (temporaryCreated)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
                {
                    System.Diagnostics.Debug.WriteLine($"清理外观临时文件失败：{ex.Message}");
                }
            }
        }
    }
}