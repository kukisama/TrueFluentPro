namespace TrueFluentPro.Services.Storage
{
    /// <summary>
    /// SQLite 功能开关。每个模块可独立控制是否启用 SQLite 读/写路径。
    /// 默认全部关闭，逐步打开以便验收与回退。
    /// </summary>
    public class SqliteFeatureSwitches
    {
        public bool UseSqliteSessionList { get; set; }
        public bool UseSqliteSessionWrite { get; set; }
        public bool UseSqliteMessagePaging { get; set; }
        public bool UseSqliteAssetCatalog { get; set; }
        public bool UseSqliteWorkspaceWrite { get; set; }
        public bool UseSqliteAudioIndexWrite { get; set; }
        public bool EnableLegacyImport { get; set; }
    }
}
