use tauri::State;
use tfp_storage::{Message, Session};

use crate::state::AppState;

#[tauri::command]
pub async fn list_sessions(
    state: State<'_, AppState>,
    session_type: Option<String>,
) -> Result<Vec<Session>, String> {
    match session_type.as_deref() {
        Some(st) if !st.is_empty() => {
            state.db.list_sessions_by_type(st).await.map_err(|e| e.to_string())
        }
        _ => state.db.list_sessions().await.map_err(|e| e.to_string()),
    }
}

#[tauri::command]
pub async fn create_session(
    state: State<'_, AppState>,
    title: String,
    session_type: String,
) -> Result<Session, String> {
    let now = now_utc_string();
    let session = Session {
        id: uuid::Uuid::new_v4().to_string(),
        title,
        session_type,
        message_count: 0,
        token_total: 0,
        created_at: now.clone(),
        updated_at: now,
    };
    state
        .db
        .create_session(&session)
        .await
        .map_err(|e| e.to_string())?;
    Ok(session)
}

#[tauri::command]
pub async fn delete_session(
    state: State<'_, AppState>,
    session_id: String,
) -> Result<(), String> {
    state
        .db
        .delete_messages_by_session(&session_id)
        .await
        .map_err(|e| e.to_string())?;
    state
        .db
        .delete_session(&session_id)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn rename_session(
    state: State<'_, AppState>,
    session_id: String,
    new_title: String,
) -> Result<(), String> {
    state
        .db
        .rename_session(&session_id, &new_title)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn get_session_messages(
    state: State<'_, AppState>,
    session_id: String,
) -> Result<Vec<Message>, String> {
    state
        .db
        .list_messages(&session_id)
        .await
        .map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn add_session_message(
    state: State<'_, AppState>,
    message: Message,
) -> Result<(), String> {
    let mut msg = message;
    if msg.id.is_empty() {
        msg.id = uuid::Uuid::new_v4().to_string();
    }
    if msg.created_at.is_empty() {
        msg.created_at = now_utc_string();
    }
    state
        .db
        .add_message(&msg)
        .await
        .map_err(|e| e.to_string())
}

/// Returns the current UTC time as an ISO 8601 string without external crate.
pub(crate) fn now_utc_string() -> String {
    use std::time::SystemTime;
    let dur = SystemTime::now()
        .duration_since(SystemTime::UNIX_EPOCH)
        .unwrap_or_default();
    let secs = dur.as_secs();
    // Simple UTC timestamp: seconds since epoch → readable format
    // Format: YYYY-MM-DDTHH:MM:SSZ
    let days = secs / 86400;
    let time_of_day = secs % 86400;
    let hours = time_of_day / 3600;
    let minutes = (time_of_day % 3600) / 60;
    let seconds = time_of_day % 60;

    // Calculate year/month/day from days since epoch (1970-01-01)
    let (year, month, day) = days_to_ymd(days);
    format!("{year:04}-{month:02}-{day:02}T{hours:02}:{minutes:02}:{seconds:02}Z")
}

pub(crate) fn days_to_ymd(mut days: u64) -> (u64, u64, u64) {
    // Algorithm from Howard Hinnant's civil_from_days
    days += 719_468;
    let era = days / 146_097;
    let doe = days - era * 146_097;
    let yoe = (doe - doe / 1460 + doe / 36524 - doe / 146_096) / 365;
    let y = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let m = if mp < 10 { mp + 3 } else { mp - 9 };
    let y = if m <= 2 { y + 1 } else { y };
    (y, m, d)
}
