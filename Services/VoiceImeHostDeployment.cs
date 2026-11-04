using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace TrueFluentPro.Services;

/// <summary>仅规划/同步独立 Host 文件；不访问注册表、进程或系统队列。</summary>
internal sealed class VoiceImeHostDeployment
{
    internal sealed record Change(string Source, string Destination, string Hash);
    private readonly string _source, _destination;
    internal IReadOnlyList<Change> Changes { get; }
    internal IReadOnlyList<string> DestinationPaths { get; }

    private VoiceImeHostDeployment(string source, string destination, List<Change> changes, HashSet<string> paths)
    {
        _source = source;
        _destination = destination;
        Changes = changes.AsReadOnly();
        DestinationPaths = Array.AsReadOnly(paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    internal static VoiceImeHostDeployment Create(string source, string destination)
    {
        // 先验证原始路径，再规范化；相对路径、UNC、ADS 和任一父级 reparse 都不能被洗白。
        VoiceImeMaintenance.GuardPath(source);
        VoiceImeMaintenance.GuardPath(destination);
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        if (!Same(source, destination) && (Within(source, destination) || Within(destination, source)))
            throw new IOException("独立后台源目录与目标目录重叠，未规划复制。");

        var changes = new List<Change>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { destination };
        foreach (var file in Directory.EnumerateFiles(source))
        {
            // Host 同时承载设置页：保留主程序及运行依赖，不带更新器、调试/文档或其他工具。
            var name = Path.GetFileName(file).ToLowerInvariant();
            if (name is "truefluentpro.exe" or "truefluentpro.deps.json" or "truefluentpro.runtimeconfig.json"
                || (Path.GetExtension(name) == ".dll" && name != "updater.dll"))
                AddFile(file);
        }
        var runtimes = Path.Combine(source, "runtimes");
        VoiceImeMaintenance.GuardPath(runtimes);
        if (VoiceImeMaintenance.Exists(runtimes)) AddDirectory(runtimes);
        // Assets 已嵌入程序集；这里只同步包中实际存在的固定架构组件，不递归源码/中间文件。
        var voiceIme = Path.Combine(source, "voice-ime");
        VoiceImeMaintenance.GuardPath(voiceIme);
        if (VoiceImeMaintenance.Exists(voiceIme))
        {
            _ = Target(voiceIme);
            foreach (var architecture in new[] { "win-x64", "win-x86", "win-arm64" })
            {
                var directory = Path.Combine(voiceIme, architecture);
                VoiceImeMaintenance.GuardPath(directory);
                if (!VoiceImeMaintenance.Exists(directory)) continue;
                _ = Target(directory);
                // 来自 Host 的设置页也要安装/修复；不能只保留当前进程架构或丢掉 helper。
                foreach (var name in new[] { "TrueFluentVoiceIme.dll", "VoiceImeRegister.exe" })
                {
                    var file = Path.Combine(directory, name);
                    VoiceImeMaintenance.GuardPath(file);
                    if (VoiceImeMaintenance.Exists(file)) AddFile(file);
                }
            }
        }
        return new VoiceImeHostDeployment(source, destination, changes, paths);

        string Target(string path)
        {
            VoiceImeMaintenance.GuardPath(path);
            if (!Within(source, path)) throw new IOException("独立后台源文件越界。");
            var target = Path.GetFullPath(Path.Combine(destination, Path.GetRelativePath(source, path)));
            if (!Within(destination, target)) throw new IOException("独立后台目标文件越界。");
            VoiceImeMaintenance.GuardPath(target);
            paths.Add(target);
            return target;
        }

        void AddFile(string path)
        {
            var target = Target(path);
            if (Same(path, target)) return; // 包含不同写法的同一路径；绝不自行复制。
            var hash = Digest(path);
            if (!VoiceImeMaintenance.Exists(target) || Digest(target) != hash)
                changes.Add(new Change(path, target, hash));
        }

        void AddDirectory(string directory)
        {
            _ = Target(directory); // 进入子目录前验证，而非 AllDirectories 跟随链接后才检查。
            foreach (var file in Directory.EnumerateFiles(directory)) AddFile(file);
            foreach (var child in Directory.EnumerateDirectories(directory)) AddDirectory(child);
        }
    }

    internal int Synchronize()
    {
        // 先复核整个差异计划；过期源文件或路径防护失败时不写任何目标文件。
        foreach (var file in Changes)
        {
            Guard(file);
            if (Digest(file.Source) != file.Hash)
                throw new IOException("独立后台源文件在规划后发生变化，未继续同步。");
        }
        var copied = 0;
        foreach (var file in Changes)
        {
            Guard(file);
            if (VoiceImeMaintenance.Exists(file.Destination) && Digest(file.Destination) == file.Hash) continue;
            if (Digest(file.Source) != file.Hash) throw new IOException("独立后台源文件在同步时发生变化。");
            Directory.CreateDirectory(Path.GetDirectoryName(file.Destination)!);
            VoiceImeMaintenance.GuardPath(file.Destination);
            File.Copy(file.Source, file.Destination, overwrite: true);
            if (Digest(file.Destination) != file.Hash) throw new IOException("独立后台同步后摘要不符，未确认部署完成。");
            copied++;
        }
        return copied;
    }

    private void Guard(Change file)
    {
        if (!Within(_source, file.Source) || !Within(_destination, file.Destination) || Same(file.Source, file.Destination))
            throw new IOException("独立后台复制计划越界或源目标相同。");
        VoiceImeMaintenance.GuardPath(file.Source);
        VoiceImeMaintenance.GuardPath(file.Destination);
    }

    private static bool Within(string root, string path)
        => Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))
            + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool Same(string left, string right)
        => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    private static string Digest(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}