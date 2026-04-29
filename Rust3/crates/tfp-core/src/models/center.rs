use serde::{Deserialize, Serialize};

use super::studio::{StudioReferenceImage, StudioTask};

/// 工作区元数据（复用 studio_sessions，靠 session_type='canvas_image'/'canvas_video' 区分）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CenterWorkspace {
    pub id: String,
    pub session_type: String,
    pub name: String,
    pub is_deleted: bool,
    pub created_at: String,
    pub updated_at: String,
    pub last_accessed_at: Option<String>,
    pub current_round_id: Option<String>,
    pub round_count: i64,
    pub asset_count: i64,
    pub has_running_task: bool,
    /// Draw mode: ""/"draw"/"edit"
    pub canvas_mode: String,
    /// Media kind: ""/"image"/"video"
    pub media_kind: String,
    /// Lineage: source workspace id
    pub source_session_id: Option<String>,
    /// Lineage: source workspace name
    pub source_session_name: Option<String>,
    /// Lineage: source asset id
    pub source_asset_id: Option<String>,
    /// Lineage: source asset file name
    pub source_asset_file_name: Option<String>,
    /// Lineage: source asset kind ("image"/"video")
    pub source_asset_kind: Option<String>,
    /// Lineage: reference role ("direct_image"/"video_last_frame")
    pub source_reference_role: Option<String>,
}

/// 生成轮次（对应 canvas_rounds 表）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CanvasRound {
    pub id: String,
    pub session_id: String,
    pub round_index: i64,
    pub prompt: String,
    pub params_json: String,
    pub model_ref: String,
    pub created_at: String,
    pub status: String,
}

/// 轮次资产关联（对应 canvas_round_assets 表）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CanvasRoundAsset {
    pub id: String,
    pub round_id: String,
    pub asset_id: String,
    pub sequence: i64,
    pub is_selected: bool,
}

/// 工作区完整 bundle（懒加载时一次性拉取）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CenterWorkspaceBundle {
    pub workspace: CenterWorkspace,
    pub rounds: Vec<CanvasRound>,
    pub current_round_assets: Vec<CenterAssetDetail>,
    pub reference_images: Vec<StudioReferenceImage>,
    pub running_tasks: Vec<StudioTask>,
    /// Total asset count across all rounds
    pub all_asset_count: i64,
    /// Whether there are more rounds not loaded (for pagination)
    pub has_more_rounds: bool,
    /// Per-round prompt summaries
    pub round_prompts: Vec<RoundPromptSummary>,
}

/// Summary of a round's prompt for timeline display
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct RoundPromptSummary {
    pub round_id: String,
    pub round_index: i64,
    pub prompt_preview: String,
    pub status: String,
    pub asset_count: i64,
    pub created_at: String,
}

/// 资产详情（用于前端网格展示）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CenterAssetDetail {
    pub id: String,
    pub round_id: String,
    pub asset_id: String,
    pub sequence: i64,
    pub is_selected: bool,
    pub file_path: String,
    pub preview_path: String,
    pub kind: String,
    pub width: Option<i64>,
    pub height: Option<i64>,
    pub duration_ms: Option<i64>,
    pub created_at: String,
}

/// 视频能力组合条目
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct VideoCapabilityEntry {
    pub aspect_ratio: String,
    pub resolution: String,
    pub duration_seconds: Vec<i64>,
    pub max_count: i64,
}

/// 批量导出结果
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ExportResult {
    pub copied: i64,
    pub failed: i64,
}
