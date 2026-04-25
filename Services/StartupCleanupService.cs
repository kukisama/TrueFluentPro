using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using TrueFluentPro.Services.Storage;

namespace TrueFluentPro.Services
{
    /// <summary>
    /// 启动清理服务：清理 .tmp 残留文件、修正 staging 中断状态。
    /// </summary>
    public sealed class StartupCleanupService
    {
        private readonly ISqliteDbService _db;

        public StartupCleanupService(ISqliteDbService db) => _db = db;

        /// <summary>
        /// 执行全部启动清理。在 SQLite 初始化完成后调用。
        /// </summary>
        public void RunAll()
        {
            try { CleanOrphanedTmpFiles(); } catch { }
            try { FixInterruptedStagingTasks(); } catch { }
            try { CleanOrphanedSessionAssets(); } catch { }
        }

        /// <summary>
        /// 清理 session 目录下超过 1 小时的 .tmp 文件。
        /// </summary>
        private static void CleanOrphanedTmpFiles()
        {
            var sessionsRoot = PathManager.Instance.SessionsPath;
            if (!Directory.Exists(sessionsRoot)) return;

            var cutoff = DateTime.Now.AddHours(-1);
            var tmpFiles = Directory.EnumerateFiles(sessionsRoot, "*.tmp", SearchOption.AllDirectories)
                .Where(f =>
                {
                    try { return File.GetLastWriteTime(f) < cutoff; }
                    catch { return false; }
                });

            foreach (var tmpFile in tmpFiles)
            {
                try { File.Delete(tmpFile); }
                catch { }
            }
        }

        /// <summary>
        /// 将 task_staging 中 pending(0)/running(1) 的记录标记为 failed(3)。
        /// 这些任务在上次运行中因崩溃/强退未能完成。
        /// </summary>
        private void FixInterruptedStagingTasks()
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
UPDATE task_staging 
SET status = 3, 
    error_code = 'app_crash',
    error_message = '程序异常退出，任务未完成',
    finished_at = @now
WHERE status IN (0, 1);";
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// 清理 session_assets 中 file_path 指向的文件已不存在的记录。
        /// 文件可能因用户手动删除或磁盘清理而丢失。
        /// </summary>
        private void CleanOrphanedSessionAssets()
        {
            using var conn = _db.CreateConnection();

            // 1) 查出所有有 file_path 的记录
            var orphanIds = new List<string>();
            using (var selectCmd = conn.CreateCommand())
            {
                selectCmd.CommandText = "SELECT asset_id, file_path FROM session_assets WHERE file_path <> '';";
                using var reader = selectCmd.ExecuteReader();
                while (reader.Read())
                {
                    var assetId = reader.GetString(0);
                    var filePath = reader.GetString(1);
                    if (!string.IsNullOrWhiteSpace(filePath) && !File.Exists(filePath))
                    {
                        orphanIds.Add(assetId);
                    }
                }
            }

            if (orphanIds.Count == 0) return;

            // 2) 批量删除孤儿记录
            using var tx = conn.BeginTransaction();
            using var delCmd = conn.CreateCommand();
            delCmd.Transaction = tx;
            delCmd.CommandText = "DELETE FROM session_assets WHERE asset_id = @id;";
            var param = delCmd.Parameters.Add("@id", Microsoft.Data.Sqlite.SqliteType.Text);
            foreach (var id in orphanIds)
            {
                param.Value = id;
                delCmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }
}
