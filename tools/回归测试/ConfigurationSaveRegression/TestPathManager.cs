// 仅替换路径环境；配置服务、配置模型和语言窗口 patch 均使用真实源码。
// 不初始化生产 PathManager，不读取/创建用户 AppData 下的目录。
namespace TrueFluentPro.Services;

public sealed class PathManager
{
    public static PathManager Instance { get; } = new();
    public string ConfigFilePath { get; set; } = "";
    public string SessionsPath { get; private set; } = "";

    public void SetSessionsPath(string? path)
        => SessionsPath = path ?? Path.Combine(Path.GetDirectoryName(ConfigFilePath)!, "Sessions");
}