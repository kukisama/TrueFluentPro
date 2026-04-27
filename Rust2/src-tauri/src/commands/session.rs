use tauri::State;

use crate::models::*;
use crate::state::AppState;

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  P1.2: 会话 & 消息命令
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

#[tauri::command]
pub async fn list_sessions(
    state: State<'_, AppState>,
    session_type: Option<String>,
) -> Result<Vec<Session>, String> {
    state.db.list_sessions(session_type.as_deref()).await.map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn create_session(
    state: State<'_, AppState>,
    title: String,
    session_type: String,
) -> Result<Session, String> {
    let now = chrono::Utc::now().to_rfc3339();
    let session = Session {
        id: uuid::Uuid::new_v4().to_string(),
        title,
        session_type,
        message_count: 0,
        token_total: 0,
        created_at: now.clone(),
        updated_at: now,
    };
    state.db.create_session(&session).await.map_err(|e| e.to_string())?;
    Ok(session)
}

#[tauri::command]
pub async fn delete_session(
    state: State<'_, AppState>,
    session_id: String,
) -> Result<(), String> {
    state.db.delete_session(&session_id).await.map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn rename_session(
    state: State<'_, AppState>,
    session_id: String,
    new_title: String,
) -> Result<(), String> {
    state.db.rename_session(&session_id, &new_title).await.map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn get_session_messages(
    state: State<'_, AppState>,
    session_id: String,
) -> Result<Vec<Message>, String> {
    state.db.get_session_messages(&session_id).await.map_err(|e| e.to_string())
}

#[tauri::command]
pub async fn add_message(
    state: State<'_, AppState>,
    msg: Message,
) -> Result<Message, String> {
    let mut msg = msg;
    if msg.id.is_empty() {
        msg.id = uuid::Uuid::new_v4().to_string();
    }
    if msg.created_at.is_empty() {
        msg.created_at = chrono::Utc::now().to_rfc3339();
    }
    state.db.add_message(&msg).await.map_err(|e| e.to_string())?;
    Ok(msg)
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  存储命令
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

#[tauri::command]
pub async fn get_translation_history(
    state: State<'_, AppState>,
    limit: Option<u32>,
) -> Result<Vec<TranslationHistory>, String> {
    state
        .db
        .list_translations(limit.unwrap_or(50))
        .await
        .map_err(|e| e.to_string())
}
