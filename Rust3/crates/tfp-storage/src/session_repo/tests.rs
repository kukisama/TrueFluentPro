use crate::Database;
use super::*;

fn make_session(id: &str) -> Session {
    Session {
        id: id.to_string(),
        title: "Test".into(),
        session_type: "chat".into(),
        message_count: 0,
        token_total: 0,
        created_at: "2026-01-01T00:00:00".into(),
        updated_at: "2026-01-01T00:00:00".into(),
    }
}

fn make_message(id: &str, session_id: &str) -> Message {
    Message {
        id: id.to_string(),
        session_id: session_id.to_string(),
        role: "user".into(),
        content: "hello".into(),
        mode: "text".into(),
        reasoning_text: None,
        prompt_tokens: Some(10),
        completion_tokens: Some(20),
        image_base64: None,
        attachments: None,
        content_hash: None,
        created_at: "2026-01-01T00:00:00".into(),
    }
}

#[tokio::test]
async fn test_session_crud() {
    let db = Database::open_in_memory().unwrap();
    let s = make_session("s1");

    db.create_session(&s).await.unwrap();
    let got = db.get_session("s1").await.unwrap();
    assert!(got.is_some());
    assert_eq!(got.unwrap().title, "Test");

    let list = db.list_sessions().await.unwrap();
    assert_eq!(list.len(), 1);

    db.delete_session("s1").await.unwrap();
    let got2 = db.get_session("s1").await.unwrap();
    assert!(got2.is_none());
}

#[tokio::test]
async fn test_message_crud() {
    let db = Database::open_in_memory().unwrap();
    let s = make_session("s1");
    db.create_session(&s).await.unwrap();

    let m = make_message("m1", "s1");
    db.add_message(&m).await.unwrap();

    let msgs = db.list_messages("s1").await.unwrap();
    assert_eq!(msgs.len(), 1);
    assert_eq!(msgs[0].content, "hello");

    db.delete_messages_by_session("s1").await.unwrap();
    let msgs2 = db.list_messages("s1").await.unwrap();
    assert_eq!(msgs2.len(), 0);
}

#[tokio::test]
async fn test_rename_session() {
    let db = Database::open_in_memory().unwrap();
    let s = make_session("s1");
    db.create_session(&s).await.unwrap();

    db.rename_session("s1", "Renamed Title").await.unwrap();

    let got = db.get_session("s1").await.unwrap().unwrap();
    assert_eq!(got.title, "Renamed Title");
    assert_ne!(got.updated_at, got.created_at);
}

#[tokio::test]
async fn test_list_sessions_by_type() {
    let db = Database::open_in_memory().unwrap();

    let mut s1 = make_session("s1");
    s1.session_type = "chat".into();
    db.create_session(&s1).await.unwrap();

    let mut s2 = make_session("s2");
    s2.session_type = "translation".into();
    db.create_session(&s2).await.unwrap();

    let mut s3 = make_session("s3");
    s3.session_type = "chat".into();
    db.create_session(&s3).await.unwrap();

    let chat_sessions = db.list_sessions_by_type("chat").await.unwrap();
    assert_eq!(chat_sessions.len(), 2);
    for s in &chat_sessions {
        assert_eq!(s.session_type, "chat");
    }

    let trans_sessions = db.list_sessions_by_type("translation").await.unwrap();
    assert_eq!(trans_sessions.len(), 1);
    assert_eq!(trans_sessions[0].id, "s2");

    let empty = db.list_sessions_by_type("nonexistent").await.unwrap();
    assert!(empty.is_empty());
}
