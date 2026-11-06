using System.Text.Json;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using TrueFluentPro.Views;

// 独立控制台回归：真实 ConfigurationService / AzureSpeechConfig / 三字段 patch；
// 不实例化主窗，不读真实配置，不调用云端，不运行主项目构建。
internal static class Program
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    private static string _root = "";

    private static async Task<int> Main()
    {
        _root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("同对象连续保存保留面板三字段，并回填内存和更新基线", RepeatedSameObjectAsync),
            ("逐字段主动编辑优先，其余字段合并磁盘", EditedFieldsAsync),
            ("主窗/齿轮独立加载与跨服务保存、并发保存", IndependentOwnersAsync),
            ("本地保存后仍能合并面板更新及再次主动编辑", AdvanceBaselineAsync),
            ("导入克隆/新配置首次完整保存，随后保护生效", ImportAndNewConfigAsync),
            ("首次默认配置、旧JSON缺字段及大小写/注释兼容", DefaultsAndLegacyAsync),
            ("备份/默认回退恢复保存，回退对象也受保护", RecoveryAsync),
            ("写入失败不污染内存/基线，重试合并最新值", FailedSaveAsync)
        };
        var failures = 0;
        foreach (var test in tests)
        {
            try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
            catch (Exception ex) { failures++; Console.WriteLine($"FAIL {test.Name}\n{ex}"); }
        }
        Console.WriteLine($"RESULT passed={tests.Length - failures} failed={failures}");
        Console.WriteLine($"FIXTURES {_root}");
        return failures == 0 ? 0 : 1;
    }

    private static async Task RepeatedSameObjectAsync()
    {
        var (path, service) = await SeedAsync("repeat");
        var config = await service.LoadConfigAsync();
        var originalVoiceIme = config.VoiceIme;
        await PatchAsync(path, "zh-CN", true, "fr-FR");
        config.ThemeMode = ThemeModePreference.Dark;
        config.VoiceIme.MicrophoneDeviceId = "local-microphone";
        await service.SaveConfigAsync(config);
        var saved = await ReadAsync(path);
        Languages(saved, "zh-CN", true, "fr-FR");
        Languages(config, "zh-CN", true, "fr-FR");
        Equal(true, ReferenceEquals(originalVoiceIme, config.VoiceIme), "VoiceIme对象身份");
        Equal(ThemeModePreference.Dark, saved.ThemeMode, "主窗其它设置");
        Equal("local-microphone", saved.VoiceIme.MicrophoneDeviceId, "其它输入法设置");
        Equal("zh-Hans", saved.TargetLanguage, "顶层翻译目标不混淆");
        Languages(await ReadAsync(path + ".bak"), "zh-CN", true, "fr-FR");

        await PatchAsync(path, "de-DE", false, "ja-JP");
        config.IsMainNavPaneOpen = true;
        await service.SaveConfigAsync(config);
        Languages(await ReadAsync(path), "de-DE", false, "ja-JP");
        Languages(config, "de-DE", false, "ja-JP");
        await service.SaveConfigAsync(config);
        Languages(await ReadAsync(path), "de-DE", false, "ja-JP");
    }

    private static async Task EditedFieldsAsync()
    {
        foreach (var field in new[] { "recognition", "translation", "target", "all" })
        {
            var seed = new AzureSpeechConfig();
            seed.VoiceIme.EnableTranslation = true;
            var (path, service) = await SeedAsync("edited-" + field, seed);
            var config = await service.LoadConfigAsync();
            await PatchAsync(path, "zh-CN", true, "fr-FR");
            if (field is "recognition" or "all") config.VoiceIme.RecognitionLanguage = "ja-JP";
            if (field is "translation" or "all") config.VoiceIme.EnableTranslation = false;
            if (field is "target" or "all") config.VoiceIme.TargetLanguage = "ko-KR";
            await service.SaveConfigAsync(config);
            Languages(await ReadAsync(path),
                field is "recognition" or "all" ? "ja-JP" : "zh-CN",
                field is not ("translation" or "all"),
                field is "target" or "all" ? "ko-KR" : "fr-FR");
        }
    }

    private static async Task IndependentOwnersAsync()
    {
        var (path, mainService) = await SeedAsync("owners");
        var gearService = new ConfigurationService();
        var main = await mainService.LoadConfigAsync();
        var gear = await gearService.LoadConfigAsync();
        Equal(false, ReferenceEquals(main, gear), "独立窗口持有不同对象");
        await PatchAsync(path, "zh-CN", true, "fr-FR");
        main.DefaultFontSize = 42;
        gear.VoiceIme.EndSilenceTimeoutSeconds = 1.4;
        await mainService.SaveConfigAsync(main);
        await gearService.SaveConfigAsync(gear);
        Languages(await ReadAsync(path), "zh-CN", true, "fr-FR");
        Equal(1.4, (await ReadAsync(path)).VoiceIme.EndSilenceTimeoutSeconds, "齿轮主动改静音");

        await PatchAsync(path, "ko-KR", false, "de-DE");
        // 对象跟踪不绑定到某一个 ConfigurationService 实例；也不争用原有 .tmp。
        var otherService = new ConfigurationService();
        await Task.WhenAll(otherService.SaveConfigAsync(main), gearService.SaveConfigAsync(gear));
        Languages(await ReadAsync(path), "ko-KR", false, "de-DE");
        Languages(main, "ko-KR", false, "de-DE");
        Languages(gear, "ko-KR", false, "de-DE");
    }

    private static async Task AdvanceBaselineAsync()
    {
        var (path, service) = await SeedAsync("advance");
        var config = await service.LoadConfigAsync();
        config.VoiceIme.RecognitionLanguage = "ja-JP";
        await service.SaveConfigAsync(config);
        await PatchAsync(path, "auto", true, "fr-FR");
        await service.SaveConfigAsync(config);
        Languages(config, "auto", true, "fr-FR");
        config.VoiceIme.RecognitionLanguage = "ja-JP";
        await service.SaveConfigAsync(config);
        Languages(await ReadAsync(path), "ja-JP", true, "fr-FR");
    }

    private static async Task ImportAndNewConfigAsync()
    {
        foreach (var mode in new[] { "clone", "new", "null" })
        {
            var (path, service) = await SeedAsync("import-" + mode);
            var loaded = await service.LoadConfigAsync();
            // 与 SettingsImportExportService.CloneConfig 相同的 JSON 克隆，新对象不能继承基线。
            var imported = mode == "clone"
                ? JsonSerializer.Deserialize<AzureSpeechConfig>(JsonSerializer.Serialize(loaded))!
                : new AzureSpeechConfig();
            if (mode == "null") imported.VoiceIme = null!;
            await PatchAsync(path, "zh-CN", true, "fr-FR");
            await service.SaveConfigAsync(imported);
            var saved = await ReadAsync(path);
            if (mode == "null")
            {
                Equal<VoiceImeConfig?>(null, saved.VoiceIme, "显式空配置不被恢复");
                continue;
            }
            Languages(saved, "auto", false, "en-US");
            await PatchAsync(path, "ko-KR", true, "de-DE");
            await service.SaveConfigAsync(imported);
            Languages(await ReadAsync(path), "ko-KR", true, "de-DE");
        }
    }

    private static async Task DefaultsAndLegacyAsync()
    {
        var path = NewPath("first-run");
        var service = new ConfigurationService();
        var config = await service.LoadConfigAsync();
        Equal(true, service.LastLoadReport!.CreatedDefaultConfig, "首次默认报告");
        await PatchAsync(path, "zh-CN", true, "fr-FR");
        await service.SaveConfigAsync(config);
        Languages(await ReadAsync(path), "zh-CN", true, "fr-FR");

        foreach (var json in new[]
        {
            "{}",
            "{/*旧配置*/\"voiceIME\":{\"recognitionLANGUAGE\":\"auto\",\"enableTRANSLATION\":false,\"targetLANGUAGE\":\"en-US\",},}"
        })
        {
            path = NewPath(Guid.NewGuid().ToString("N"));
            await File.WriteAllTextAsync(path, json);
            service = new ConfigurationService();
            config = await service.LoadConfigAsync();
            await PatchAsync(path, "zh-CN", true, "fr-FR");
            await service.SaveConfigAsync(config);
            Languages(await ReadAsync(path), "zh-CN", true, "fr-FR");
        }
    }

    private static async Task RecoveryAsync()
    {
        foreach (var mode in new[] { "backup-repair", "backup-merge", "default-merge" })
        {
            var path = NewPath(mode);
            await File.WriteAllTextAsync(path, "{invalid");
            if (mode.StartsWith("backup"))
                await File.WriteAllTextAsync(path + ".bak", JsonSerializer.Serialize(new AzureSpeechConfig()));
            var service = new ConfigurationService();
            var config = await service.LoadConfigAsync();
            Equal(true, service.LastLoadReport!.UsedFallbackConfig, "回退报告");
            if (mode != "backup-repair")
            {
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new AzureSpeechConfig()));
                await PatchAsync(path, "zh-CN", true, "fr-FR");
            }
            await service.SaveConfigAsync(config);
            Languages(await ReadAsync(path), mode == "backup-repair" ? "auto" : "zh-CN",
                mode != "backup-repair", mode == "backup-repair" ? "en-US" : "fr-FR");
        }
    }

    private static async Task FailedSaveAsync()
    {
        var (path, service) = await SeedAsync("failed-save");
        var config = await service.LoadConfigAsync();
        config.VoiceIme.RecognitionLanguage = "ja-JP";
        await PatchAsync(path, "zh-CN", true, "fr-FR");
        // 在自有 fixture 中让原有 .tmp 路径成为目录，确定性拒绝写入。
        Directory.CreateDirectory(path + ".tmp");
        var failed = false;
        try { await service.SaveConfigAsync(config); }
        catch (UnauthorizedAccessException) { failed = true; }
        catch (IOException) { failed = true; }
        Equal(true, failed, "必须报告写入失败");
        Languages(config, "ja-JP", false, "en-US");
        Languages(await ReadAsync(path), "zh-CN", true, "fr-FR");
        Directory.Delete(path + ".tmp"); // 仅本用例创建的空目录。
        await PatchAsync(path, "ko-KR", true, "de-DE");
        await service.SaveConfigAsync(config);
        Languages(await ReadAsync(path), "ja-JP", true, "de-DE");
        Languages(config, "ja-JP", true, "de-DE");
    }

    private static string NewPath(string name)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "config.json");
        PathManager.Instance.ConfigFilePath = path;
        return path;
    }

    private static async Task<(string Path, ConfigurationService Service)> SeedAsync(string name, AzureSpeechConfig? seed = null)
    {
        var path = NewPath(name);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(seed ?? new AzureSpeechConfig()));
        return (path, new ConfigurationService());
    }

    private static Task PatchAsync(string path, string recognition, bool translation, string target)
        => VoiceImeLanguageWindow.SaveSettingsAsync(path, new VoiceImeConfig
        {
            RecognitionLanguage = recognition, EnableTranslation = translation, TargetLanguage = target
        });

    private static async Task<AzureSpeechConfig> ReadAsync(string path)
        => JsonSerializer.Deserialize<AzureSpeechConfig>(await File.ReadAllTextAsync(path), Options)!;

    private static void Languages(AzureSpeechConfig config, string recognition, bool translation, string target)
    {
        Equal(recognition, config.VoiceIme.RecognitionLanguage, "RecognitionLanguage");
        Equal(translation, config.VoiceIme.EnableTranslation, "EnableTranslation");
        Equal(target, config.VoiceIme.TargetLanguage, "VoiceIme.TargetLanguage");
    }

    private static void Equal<T>(T expected, T actual, string field)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{field}: expected={expected}; actual={actual}");
    }
}