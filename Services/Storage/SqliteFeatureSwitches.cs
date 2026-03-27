namespace TrueFluentPro.Services.Storage
{
    /// <summary>
    /// SQLite 就绪状态。InitializeSqliteStorage 完成后置 true，
    /// 所有 SQLite 读写路径在此为 false 时静默跳过（后台初始化尚未完成）。
    /// </summary>
    public class SqliteFeatureSwitches
    {
        public bool IsReady { get; set; }
    }
}
