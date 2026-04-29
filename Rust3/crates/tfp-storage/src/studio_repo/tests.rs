use crate::Database;
use tfp_core::{StudioSession, StudioMessage, StudioReferenceImage};

fn make_studio_session(id: &str) -> StudioSession {
    StudioSession {
        id: id.to_string(),
        session_type: "image".into(),
        name: "Test Session".into(),
        directory_path: "/sessions/test".into(),
        canvas_mode: "".into(),
        media_kind: "".into(),
        is_deleted: false,
        created_at: "2026-01-01T00:00:00Z".into(),
        updated_at: "2026-01-01T00:00:00Z".into(),
        last_accessed_at: None,
        source_session_id: None,
        source_session_name: None,
        source_session_directory_name: None,
        source_asset_id: None,
        source_asset_kind: None,
        source_asset_file_name: None,
        source_asset_path: None,
        source_preview_path: None,
        source_reference_role: None,
        message_count: 0,
        task_count: 0,
        asset_count: 0,
        latest_message_preview: None,
        legacy_source_path: None,
        import_batch_id: None,
        imported_at: None,
        is_legacy_import: false,
    }
}

fn make_message(id: &str, session_id: &str, seq: i64) -> StudioMessage {
    StudioMessage {
        id: id.to_string(),
        session_id: session_id.to_string(),
        sequence_no: seq,
        role: "user".into(),
        content_type: "text".into(),
        text: format!("message {seq}"),
        reasoning_text: "".into(),
        prompt_tokens: Some(10),
        completion_tokens: Some(20),
        generate_seconds: None,
        download_seconds: None,
        search_summary: None,
        timestamp: "2026-01-01T00:00:00Z".into(),
        is_deleted: false,
    }
}

#[tokio::test]
async fn test_studio_session_crud() {
    let db = Database::open_in_memory().unwrap();

    // Create
    let s = make_studio_session("s1");
    db.studio_create_session(&s).await.unwrap();

    // Get
    let got = db.studio_get_session("s1").await.unwrap();
    assert!(got.is_some());
    assert_eq!(got.unwrap().name, "Test Session");

    // List (not deleted)
    let list = db.studio_list_sessions(50, 0).await.unwrap();
    assert_eq!(list.len(), 1);

    // Rename
    db.studio_rename_session("s1", "Renamed").await.unwrap();
    let got2 = db.studio_get_session("s1").await.unwrap().unwrap();
    assert_eq!(got2.name, "Renamed");

    // Soft delete
    db.studio_soft_delete_session("s1").await.unwrap();
    let list2 = db.studio_list_sessions(50, 0).await.unwrap();
    assert!(list2.is_empty(), "soft-deleted session should not appear in list");
}

#[tokio::test]
async fn test_studio_message_append() {
    let db = Database::open_in_memory().unwrap();

    let s = make_studio_session("s1");
    db.studio_create_session(&s).await.unwrap();

    // Append first message
    let m1 = make_message("m1", "s1", 1);
    db.studio_append_message(&m1).await.unwrap();

    let max = db.studio_get_max_sequence("s1").await.unwrap();
    assert_eq!(max, 1);

    // Append second message
    let m2 = make_message("m2", "s1", 2);
    db.studio_append_message(&m2).await.unwrap();

    // Get messages before sequence 100 (i.e. all)
    let msgs = db.studio_get_messages_before("s1", 100, 50).await.unwrap();
    assert_eq!(msgs.len(), 2);
    // Should be in ascending order
    assert_eq!(msgs[0].sequence_no, 1);
    assert_eq!(msgs[1].sequence_no, 2);

    // Verify session message_count was incremented
    let sess = db.studio_get_session("s1").await.unwrap().unwrap();
    assert_eq!(sess.message_count, 2);
}

#[tokio::test]
async fn test_studio_reference_image_crud() {
    let db = Database::open_in_memory().unwrap();

    let s = make_studio_session("s1");
    db.studio_create_session(&s).await.unwrap();

    let img = StudioReferenceImage {
        id: "ref1".into(),
        session_id: "s1".into(),
        file_path: "/images/ref1.png".into(),
        sort_order: 0,
        width: Some(1024),
        height: Some(768),
        created_at: "2026-01-01T00:00:00Z".into(),
    };
    db.studio_add_reference_image(&img).await.unwrap();

    // List
    let list = db.studio_list_reference_images("s1").await.unwrap();
    assert_eq!(list.len(), 1);
    assert_eq!(list[0].file_path, "/images/ref1.png");
    assert_eq!(list[0].width, Some(1024));

    // Delete
    db.studio_delete_reference_image("ref1").await.unwrap();
    let list2 = db.studio_list_reference_images("s1").await.unwrap();
    assert!(list2.is_empty());
}

#[tokio::test]
async fn test_studio_get_message() {
    let db = Database::open_in_memory().unwrap();
    let s = make_studio_session("s1");
    db.studio_create_session(&s).await.unwrap();

    let m1 = make_message("m1", "s1", 1);
    db.studio_append_message(&m1).await.unwrap();

    let got = db.studio_get_message("m1").await.unwrap();
    assert!(got.is_some());
    assert_eq!(got.unwrap().text, "message 1");

    let missing = db.studio_get_message("nonexistent").await.unwrap();
    assert!(missing.is_none());
}

#[tokio::test]
async fn test_delete_messages_after() {
    let db = Database::open_in_memory().unwrap();
    let s = make_studio_session("s1");
    db.studio_create_session(&s).await.unwrap();

    for i in 1..=5 {
        let m = make_message(&format!("m{i}"), "s1", i);
        db.studio_append_message(&m).await.unwrap();
    }

    // Delete messages after sequence 3 (should delete m4, m5)
    let deleted = db.studio_delete_messages_after("s1", 3).await.unwrap();
    assert_eq!(deleted, 2);

    let count = db.studio_count_messages("s1").await.unwrap();
    assert_eq!(count, 3);

    // Session message_count should also be updated
    let sess = db.studio_get_session("s1").await.unwrap().unwrap();
    assert_eq!(sess.message_count, 3);

    // Messages 1-3 should still exist
    let msgs = db.studio_get_messages_before("s1", 100, 50).await.unwrap();
    assert_eq!(msgs.len(), 3);
    assert_eq!(msgs.last().unwrap().sequence_no, 3);
}

#[tokio::test]
async fn test_count_messages() {
    let db = Database::open_in_memory().unwrap();
    let s = make_studio_session("s1");
    db.studio_create_session(&s).await.unwrap();

    assert_eq!(db.studio_count_messages("s1").await.unwrap(), 0);

    let m1 = make_message("m1", "s1", 1);
    db.studio_append_message(&m1).await.unwrap();
    assert_eq!(db.studio_count_messages("s1").await.unwrap(), 1);

    let m2 = make_message("m2", "s1", 2);
    db.studio_append_message(&m2).await.unwrap();
    assert_eq!(db.studio_count_messages("s1").await.unwrap(), 2);
}

#[tokio::test]
async fn test_fork_session_copies_messages() {
    let db = Database::open_in_memory().unwrap();
    let s = make_studio_session("s1");
    db.studio_create_session(&s).await.unwrap();

    for i in 1..=4 {
        let m = make_message(&format!("m{i}"), "s1", i);
        db.studio_append_message(&m).await.unwrap();
    }

    // Fork up to sequence 2 (should copy m1, m2)
    let forked = db.studio_fork_session("s1", 2, "Fork of s1").await.unwrap();
    assert_eq!(forked.name, "Fork of s1");
    assert_eq!(forked.message_count, 2);
    assert_eq!(forked.source_session_id.as_deref(), Some("s1"));

    // Verify forked messages
    let forked_msgs = db.studio_get_messages_before(&forked.id, 100, 50).await.unwrap();
    assert_eq!(forked_msgs.len(), 2);
    assert_eq!(forked_msgs[0].sequence_no, 1);
    assert_eq!(forked_msgs[0].text, "message 1");
    assert_eq!(forked_msgs[1].sequence_no, 2);
    assert_eq!(forked_msgs[1].text, "message 2");

    // Original session should be unchanged
    let original_msgs = db.studio_get_messages_before("s1", 100, 50).await.unwrap();
    assert_eq!(original_msgs.len(), 4);
}

#[tokio::test]
async fn test_hard_delete_message() {
    let db = Database::open_in_memory().unwrap();
    let s = make_studio_session("s1");
    db.studio_create_session(&s).await.unwrap();

    let m1 = make_message("m1", "s1", 1);
    db.studio_append_message(&m1).await.unwrap();
    let m2 = make_message("m2", "s1", 2);
    db.studio_append_message(&m2).await.unwrap();

    db.studio_hard_delete_message("m1").await.unwrap();

    // Only m2 should remain
    let msgs = db.studio_get_messages_before("s1", 100, 50).await.unwrap();
    assert_eq!(msgs.len(), 1);
    assert_eq!(msgs[0].id, "m2");

    // Session count updated
    let sess = db.studio_get_session("s1").await.unwrap().unwrap();
    assert_eq!(sess.message_count, 1);
}

#[tokio::test]
async fn test_update_message_with_is_deleted() {
    let db = Database::open_in_memory().unwrap();
    let s = make_studio_session("s1");
    db.studio_create_session(&s).await.unwrap();

    let m = make_message("m1", "s1", 1);
    db.studio_append_message(&m).await.unwrap();

    // Soft delete via update
    let mut updated = m;
    updated.is_deleted = true;
    db.studio_update_message(&updated).await.unwrap();

    // Should not appear in non-deleted queries
    let msgs = db.studio_get_messages_before("s1", 100, 50).await.unwrap();
    assert!(msgs.is_empty());

    // But still in raw get
    let got = db.studio_get_message("m1").await.unwrap();
    assert!(got.is_some());
    assert!(got.unwrap().is_deleted);
}
