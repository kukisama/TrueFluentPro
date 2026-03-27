namespace TrueFluentPro.Services.Storage
{
    /// <summary>
    /// P1.5: 将旧 JSON 资源（session.json / workspace.json / 音频文件）扫描并导入 SQLite。
    /// 只导入元数据，不移动、不复制、不重命名任何原始文件。
    /// </summary>
    public interface ILegacyImportService
    {
        /// <summary>执行一次全量扫描导入。可重复调用（幂等），已存在的记录会被跳过。</summary>
        void ImportAll();

        /// <summary>上一次导入的批次 ID，null 表示尚未执行过。</summary>
        string? LastImportBatchId { get; }

        /// <summary>上一次导入的统计摘要。</summary>
        LegacyImportStats LastStats { get; }
    }

    public class LegacyImportStats
    {
        public int SessionsImported { get; set; }
        public int MessagesImported { get; set; }
        public int TasksImported { get; set; }
        public int AssetsImported { get; set; }
        public int AudioFilesImported { get; set; }
        public int Skipped { get; set; }
        public int Errors { get; set; }
    }
}
