using System.Collections.Generic;

namespace TrueFluentPro.Services.Storage
{
    /// <summary>
    /// 会话内容仓储：session_tasks + session_assets + reference_images。
    /// 管理会话中的任务生命周期、生成产物和参考图。
    /// </summary>
    public interface ISessionContentRepository
    {
        // ── 任务 ──
        void UpsertTask(TaskRecord record);
        TaskRecord? GetTaskById(string id);
        List<TaskRecord> GetSessionTasks(string sessionId);
        void UpdateTaskStatus(string id, string status, double progress, string? errorMessage);

        // ── 资产 ──
        void UpsertAsset(AssetRecord record);
        AssetRecord? GetAssetById(string assetId);
        List<AssetRecord> GetSessionAssets(string sessionId, int limit = 20, int offset = 0);
        int GetSessionAssetCount(string sessionId);
        void DeleteAsset(string assetId);

        // ── 参考图 ──
        void UpsertReferenceImage(ReferenceImageRecord record);
        List<ReferenceImageRecord> GetSessionReferenceImages(string sessionId);
        void DeleteReferenceImage(string id);
        void ClearSessionReferenceImages(string sessionId);
    }
}
