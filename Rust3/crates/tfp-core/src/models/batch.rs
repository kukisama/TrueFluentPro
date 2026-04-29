use serde::{Deserialize, Serialize};

/// Batch package lifecycle state.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum BatchPackageState {
    Pending,
    Running,
    Partial,
    Completed,
    Failed,
    Removed,
}

impl BatchPackageState {
    pub fn as_str(&self) -> &'static str {
        match self {
            Self::Pending => "pending",
            Self::Running => "running",
            Self::Partial => "partial",
            Self::Completed => "completed",
            Self::Failed => "failed",
            Self::Removed => "removed",
        }
    }

    pub fn from_str(s: &str) -> Self {
        match s {
            "pending" => Self::Pending,
            "running" => Self::Running,
            "partial" => Self::Partial,
            "completed" => Self::Completed,
            "failed" => Self::Failed,
            "removed" => Self::Removed,
            _ => Self::Pending,
        }
    }
}

/// Type of item in the batch processing queue.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum BatchQueueItemType {
    SpeechSubtitle,
    ReviewSheet,
}

impl BatchQueueItemType {
    pub fn as_str(&self) -> &'static str {
        match self {
            Self::SpeechSubtitle => "speech_subtitle",
            Self::ReviewSheet => "review_sheet",
        }
    }

    pub fn from_str(s: &str) -> Self {
        match s {
            "speech_subtitle" => Self::SpeechSubtitle,
            "review_sheet" => Self::ReviewSheet,
            _ => Self::ReviewSheet,
        }
    }
}

/// Status of an individual batch queue item.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum BatchQueueItemStatus {
    Pending,
    Running,
    Responding,
    Completed,
    Failed,
    Paused,
}

impl BatchQueueItemStatus {
    pub fn as_str(&self) -> &'static str {
        match self {
            Self::Pending => "pending",
            Self::Running => "running",
            Self::Responding => "responding",
            Self::Completed => "completed",
            Self::Failed => "failed",
            Self::Paused => "paused",
        }
    }

    pub fn from_str(s: &str) -> Self {
        match s {
            "pending" => Self::Pending,
            "running" => Self::Running,
            "responding" => Self::Responding,
            "completed" => Self::Completed,
            "failed" => Self::Failed,
            "paused" => Self::Paused,
            _ => Self::Pending,
        }
    }
}

/// A batch processing package grouping queue items for one audio file.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct BatchPackage {
    pub id: String,
    pub session_id: String,
    pub audio_file_id: String,
    pub display_name: String,
    pub state: BatchPackageState,
    pub is_paused: bool,
    pub is_removed: bool,
    pub total_count: i32,
    pub completed_count: i32,
    pub failed_count: i32,
    pub progress: f64,
    pub created_at: String,
    pub updated_at: String,
}

/// An individual work item in the batch queue.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct BatchQueueItem {
    pub id: String,
    pub package_id: String,
    pub queue_type: BatchQueueItemType,
    pub file_name: String,
    pub full_path: String,
    pub sheet_name: String,
    pub sheet_tag: String,
    pub prompt: String,
    pub status: BatchQueueItemStatus,
    pub progress: f64,
    pub status_message: String,
    pub error: Option<String>,
    pub created_at: String,
    pub updated_at: String,
}

/// Subtask view projection for UI display.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct BatchSubtaskView {
    pub title: String,
    pub tag: String,
    pub state: BatchPackageState,
    pub status_text: String,
    pub progress: f64,
    pub is_speech_subtask: bool,
}

/// Bucket navigation item for sidebar counts.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct BatchBucketNav {
    pub key: String,
    pub title: String,
    pub count: i32,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn batch_package_state_serde_roundtrip() {
        let states = vec![
            BatchPackageState::Pending,
            BatchPackageState::Running,
            BatchPackageState::Partial,
            BatchPackageState::Completed,
            BatchPackageState::Failed,
            BatchPackageState::Removed,
        ];
        for state in states {
            let json = serde_json::to_string(&state).unwrap();
            let restored: BatchPackageState = serde_json::from_str(&json).unwrap();
            assert_eq!(state, restored);
        }
    }

    #[test]
    fn queue_item_type_serde() {
        let t1 = BatchQueueItemType::SpeechSubtitle;
        let json1 = serde_json::to_value(&t1).unwrap();
        assert_eq!(json1, "speech_subtitle");

        let t2 = BatchQueueItemType::ReviewSheet;
        let json2 = serde_json::to_value(&t2).unwrap();
        assert_eq!(json2, "review_sheet");

        let restored: BatchQueueItemType = serde_json::from_str("\"speech_subtitle\"").unwrap();
        assert_eq!(restored, BatchQueueItemType::SpeechSubtitle);
    }

    #[test]
    fn batch_queue_item_status_roundtrip() {
        let statuses = vec![
            BatchQueueItemStatus::Pending,
            BatchQueueItemStatus::Running,
            BatchQueueItemStatus::Responding,
            BatchQueueItemStatus::Completed,
            BatchQueueItemStatus::Failed,
            BatchQueueItemStatus::Paused,
        ];
        for s in statuses {
            let json = serde_json::to_string(&s).unwrap();
            let restored: BatchQueueItemStatus = serde_json::from_str(&json).unwrap();
            assert_eq!(s, restored);
        }
    }

    #[test]
    fn batch_package_state_from_str() {
        assert_eq!(BatchPackageState::from_str("pending"), BatchPackageState::Pending);
        assert_eq!(BatchPackageState::from_str("running"), BatchPackageState::Running);
        assert_eq!(BatchPackageState::from_str("partial"), BatchPackageState::Partial);
        assert_eq!(BatchPackageState::from_str("completed"), BatchPackageState::Completed);
        assert_eq!(BatchPackageState::from_str("failed"), BatchPackageState::Failed);
        assert_eq!(BatchPackageState::from_str("removed"), BatchPackageState::Removed);
        assert_eq!(BatchPackageState::from_str("unknown"), BatchPackageState::Pending);
    }

    #[test]
    fn batch_package_serialize() {
        let pkg = BatchPackage {
            id: "pkg-1".into(),
            session_id: "sess-1".into(),
            audio_file_id: "af-1".into(),
            display_name: "Test Package".into(),
            state: BatchPackageState::Running,
            is_paused: false,
            is_removed: false,
            total_count: 5,
            completed_count: 2,
            failed_count: 0,
            progress: 0.4,
            created_at: "2026-01-01T00:00:00Z".into(),
            updated_at: "2026-01-01T00:01:00Z".into(),
        };
        let json = serde_json::to_string(&pkg).unwrap();
        let restored: BatchPackage = serde_json::from_str(&json).unwrap();
        assert_eq!(restored.id, "pkg-1");
        assert_eq!(restored.state, BatchPackageState::Running);
        assert_eq!(restored.total_count, 5);
    }
}
