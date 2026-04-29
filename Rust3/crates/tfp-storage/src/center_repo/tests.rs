use crate::Database;
use tfp_core::StudioReferenceImage;

#[tokio::test]
async fn test_center_workspace_crud() {
    let db = Database::open_in_memory().unwrap();

    // Create
    let ws = db.center_create_workspace("canvas_image", "Test WS").await.unwrap();
    assert_eq!(ws.name, "Test WS");
    assert_eq!(ws.session_type, "canvas_image");
    assert!(!ws.is_deleted);
    let ws_id = ws.id.clone();

    // List
    let list = db.center_list_workspaces(50, 0).await.unwrap();
    assert_eq!(list.len(), 1);
    assert_eq!(list[0].id, ws_id);

    // Rename
    db.center_rename_workspace(&ws_id, "Renamed WS").await.unwrap();
    let list2 = db.center_list_workspaces(50, 0).await.unwrap();
    assert_eq!(list2[0].name, "Renamed WS");

    // Soft delete
    db.center_soft_delete_workspace(&ws_id).await.unwrap();
    let list3 = db.center_list_workspaces(50, 0).await.unwrap();
    assert!(list3.is_empty(), "soft-deleted workspace should not appear in list");
}

#[tokio::test]
async fn test_center_round_crud() {
    let db = Database::open_in_memory().unwrap();

    // Create workspace
    let ws = db.center_create_workspace("canvas_image", "Round Test").await.unwrap();
    let ws_id = ws.id.clone();

    // Create round
    let round = db.center_create_round(&ws_id, "a dog", "{}", "dall-e-3").await.unwrap();
    assert_eq!(round.session_id, ws_id);
    assert_eq!(round.round_index, 1);
    assert_eq!(round.prompt, "a dog");
    assert_eq!(round.status, "pending");
    let round_id = round.id.clone();

    // List rounds
    let rounds = db.center_list_rounds(&ws_id).await.unwrap();
    assert_eq!(rounds.len(), 1);

    // Get round
    let got = db.center_get_round(&round_id).await.unwrap();
    assert!(got.is_some());
    assert_eq!(got.unwrap().model_ref, "dall-e-3");

    // Set active round (center_create_round already sets it, verify via another round)
    let round2 = db.center_create_round(&ws_id, "a cat", "{}", "dall-e-3").await.unwrap();
    db.center_set_active_round(&ws_id, &round_id).await.unwrap();

    // Verify workspace current_round_id is updated via list
    let ws_list = db.center_list_workspaces(50, 0).await.unwrap();
    assert_eq!(ws_list[0].current_round_id.as_deref(), Some(round_id.as_str()));

    // Verify second round exists
    assert_eq!(round2.round_index, 2);
}

#[tokio::test]
async fn test_workspace_with_canvas_mode_and_media_kind() {
    let db = Database::open_in_memory().unwrap();

    let ws = db.center_create_workspace_full("canvas_image", "Mode Test", "draw", "image").await.unwrap();
    assert_eq!(ws.canvas_mode, "draw");
    assert_eq!(ws.media_kind, "image");

    // Verify persisted via list
    let list = db.center_list_workspaces(50, 0).await.unwrap();
    assert_eq!(list[0].canvas_mode, "draw");
    assert_eq!(list[0].media_kind, "image");
}

#[tokio::test]
async fn test_workspace_lineage_fields() {
    let db = Database::open_in_memory().unwrap();

    // Create source workspace
    let source = db.center_create_workspace("canvas_image", "Source WS").await.unwrap();

    // Insert a fake asset for the source
    let conn = db.conn().lock().await;
    conn.execute(
        "INSERT INTO studio_assets (asset_id, session_id, file_path, preview_path, kind, created_at)
         VALUES ('asset-001', ?1, '/tmp/sunset.png', '/tmp/sunset_thumb.png', 'image', '2026-01-01T00:00:00Z')",
        rusqlite::params![source.id],
    ).unwrap();
    drop(conn);

    // Derive workspace
    let derived = db.center_derive_workspace(&source.id, "asset-001", "image", "Derived WS", "/tmp/sunset.png").await.unwrap();
    assert_eq!(derived.canvas_mode, "edit");
    assert_eq!(derived.media_kind, "image");
    assert_eq!(derived.source_session_id.as_deref(), Some(source.id.as_str()));
    assert_eq!(derived.source_session_name.as_deref(), Some("Source WS"));
    assert_eq!(derived.source_asset_id.as_deref(), Some("asset-001"));
    assert_eq!(derived.source_asset_file_name.as_deref(), Some("sunset.png"));
    assert_eq!(derived.source_asset_kind.as_deref(), Some("image"));
    assert_eq!(derived.source_reference_role.as_deref(), Some("direct_image"));
}

#[tokio::test]
async fn test_derive_workspace_creates_reference() {
    let db = Database::open_in_memory().unwrap();

    let source = db.center_create_workspace("canvas_image", "Src").await.unwrap();

    // Insert fake asset
    let conn = db.conn().lock().await;
    conn.execute(
        "INSERT INTO studio_assets (asset_id, session_id, file_path, preview_path, kind, created_at)
         VALUES ('a1', ?1, '/img/photo.jpg', '/img/photo_t.jpg', 'image', '2026-01-01T00:00:00Z')",
        rusqlite::params![source.id],
    ).unwrap();
    drop(conn);

    let derived = db.center_derive_workspace(&source.id, "a1", "image", "Edit photo", "/img/photo.jpg").await.unwrap();

    // Verify reference image was inserted
    let refs = db.studio_list_reference_images(&derived.id).await.unwrap();
    assert_eq!(refs.len(), 1);
    assert_eq!(refs[0].file_path, "/img/photo.jpg");
}

#[tokio::test]
async fn test_get_all_assets_multi_round() {
    let db = Database::open_in_memory().unwrap();

    let ws = db.center_create_workspace("canvas_image", "Multi").await.unwrap();
    let r1 = db.center_create_round(&ws.id, "prompt 1", "{}", "gpt-image-2").await.unwrap();
    let r2 = db.center_create_round(&ws.id, "prompt 2", "{}", "gpt-image-2").await.unwrap();

    // Insert assets
    let conn = db.conn().lock().await;
    for (i, rid) in [&r1.id, &r1.id, &r2.id].iter().enumerate() {
        let aid = format!("asset-{}", i);
        conn.execute(
            "INSERT INTO studio_assets (asset_id, session_id, file_path, preview_path, kind, created_at)
             VALUES (?1, ?2, ?3, ?3, 'image', '2026-01-01T00:00:00Z')",
            rusqlite::params![aid, ws.id, format!("/img/{}.png", i)],
        ).unwrap();
        conn.execute(
            "INSERT INTO canvas_round_assets (id, round_id, asset_id, sequence, is_selected)
             VALUES (?1, ?2, ?3, ?4, 0)",
            rusqlite::params![format!("cra-{}", i), rid, aid, i],
        ).unwrap();
    }
    drop(conn);

    let all = db.center_get_all_assets(&ws.id, 100).await.unwrap();
    assert_eq!(all.len(), 3);
}

#[tokio::test]
async fn test_round_asset_operations() {
    let db = Database::open_in_memory().unwrap();

    let ws = db.center_create_workspace("canvas_image", "Asset Ops").await.unwrap();
    let round = db.center_create_round(&ws.id, "test", "{}", "gpt-image-2").await.unwrap();

    // Insert a studio_asset
    let conn = db.conn().lock().await;
    conn.execute(
        "INSERT INTO studio_assets (asset_id, session_id, file_path, preview_path, kind, created_at)
         VALUES ('ast-1', ?1, '/tmp/a.png', '/tmp/a_t.png', 'image', '2026-01-01T00:00:00Z')",
        rusqlite::params![ws.id],
    ).unwrap();
    drop(conn);

    // Add round asset
    let cra_id = db.center_add_round_asset(&round.id, "ast-1", 0).await.unwrap();
    assert!(!cra_id.is_empty());

    // Get round assets
    let assets = db.center_get_round_assets(&round.id).await.unwrap();
    assert_eq!(assets.len(), 1);
    assert_eq!(assets[0].asset_id, "ast-1");
    assert!(!assets[0].is_selected);

    // Select
    db.center_select_assets(&round.id, &["ast-1".to_string()], true).await.unwrap();
    let assets2 = db.center_get_round_assets(&round.id).await.unwrap();
    assert!(assets2[0].is_selected);

    // Delete
    db.center_delete_assets(&["ast-1".to_string()]).await.unwrap();
    let assets3 = db.center_get_round_assets(&round.id).await.unwrap();
    assert!(assets3.is_empty());
}

#[tokio::test]
async fn test_bundle_with_rounds_and_assets() {
    let db = Database::open_in_memory().unwrap();

    let ws = db.center_create_workspace("canvas_image", "Bundle Test").await.unwrap();
    let round = db.center_create_round(&ws.id, "a sunset painting", "{}", "gpt-image-2").await.unwrap();

    // Insert asset
    let conn = db.conn().lock().await;
    conn.execute(
        "INSERT INTO studio_assets (asset_id, session_id, file_path, preview_path, kind, created_at)
         VALUES ('b-ast-1', ?1, '/tmp/sunset.png', '/tmp/sunset_t.png', 'image', '2026-01-01T00:00:00Z')",
        rusqlite::params![ws.id],
    ).unwrap();
    conn.execute(
        "INSERT INTO canvas_round_assets (id, round_id, asset_id, sequence, is_selected)
         VALUES ('b-cra-1', ?1, 'b-ast-1', 0, 0)",
        rusqlite::params![round.id],
    ).unwrap();
    drop(conn);

    let bundle = db.center_get_workspace_bundle(&ws.id).await.unwrap();
    assert_eq!(bundle.workspace.name, "Bundle Test");
    assert_eq!(bundle.rounds.len(), 1);
    assert_eq!(bundle.current_round_assets.len(), 1);
    assert_eq!(bundle.all_asset_count, 1);
    assert!(!bundle.has_more_rounds);
}

#[tokio::test]
async fn test_update_round_status() {
    let db = Database::open_in_memory().unwrap();

    let ws = db.center_create_workspace("canvas_image", "Status").await.unwrap();
    let round = db.center_create_round(&ws.id, "test prompt", "{}", "gpt-image-2").await.unwrap();
    assert_eq!(round.status, "pending");

    db.center_update_round_status(&round.id, "completed").await.unwrap();
    let updated = db.center_get_round(&round.id).await.unwrap().unwrap();
    assert_eq!(updated.status, "completed");
}

#[tokio::test]
async fn test_workspace_pagination() {
    let db = Database::open_in_memory().unwrap();

    for i in 0..5 {
        db.center_create_workspace("canvas_image", &format!("WS {}", i)).await.unwrap();
    }

    let page1 = db.center_list_workspaces(3, 0).await.unwrap();
    assert_eq!(page1.len(), 3);

    let page2 = db.center_list_workspaces(3, 3).await.unwrap();
    assert_eq!(page2.len(), 2);

    let all = db.center_list_workspaces(100, 0).await.unwrap();
    assert_eq!(all.len(), 5);
}

#[tokio::test]
async fn test_workspace_soft_delete_hides_from_list() {
    let db = Database::open_in_memory().unwrap();

    let ws1 = db.center_create_workspace("canvas_image", "Keep").await.unwrap();
    let ws2 = db.center_create_workspace("canvas_video", "Remove").await.unwrap();

    db.center_soft_delete_workspace(&ws2.id).await.unwrap();

    let list = db.center_list_workspaces(50, 0).await.unwrap();
    assert_eq!(list.len(), 1);
    assert_eq!(list[0].id, ws1.id);
}

#[tokio::test]
async fn test_bundle_round_prompt_summary() {
    let db = Database::open_in_memory().unwrap();

    let ws = db.center_create_workspace("canvas_image", "Summary").await.unwrap();
    db.center_create_round(&ws.id, "a beautiful landscape with mountains and rivers flowing through a valley at sunset in autumn", "{}", "gpt-image-2").await.unwrap();
    db.center_create_round(&ws.id, "short prompt", "{}", "gpt-image-2").await.unwrap();

    let bundle = db.center_get_workspace_bundle(&ws.id).await.unwrap();
    assert_eq!(bundle.round_prompts.len(), 2);
    // First prompt is > 80 chars → truncated
    assert!(bundle.round_prompts[0].prompt_preview.len() <= 80);
    assert_eq!(bundle.round_prompts[0].round_index, 1);
    // Second prompt is short
    assert_eq!(bundle.round_prompts[1].prompt_preview, "short prompt");
    assert_eq!(bundle.round_prompts[1].round_index, 2);
}

#[tokio::test]
async fn test_update_workspace_mode() {
    let db = Database::open_in_memory().unwrap();

    let ws = db.center_create_workspace("canvas_image", "Mode Update").await.unwrap();
    assert_eq!(ws.canvas_mode, "");
    assert_eq!(ws.media_kind, "");

    db.center_update_workspace_mode(&ws.id, "draw", "image").await.unwrap();

    let list = db.center_list_workspaces(50, 0).await.unwrap();
    assert_eq!(list[0].canvas_mode, "draw");
    assert_eq!(list[0].media_kind, "image");

    // Update to edit
    db.center_update_workspace_mode(&ws.id, "edit", "video").await.unwrap();
    let list2 = db.center_list_workspaces(50, 0).await.unwrap();
    assert_eq!(list2[0].canvas_mode, "edit");
    assert_eq!(list2[0].media_kind, "video");
}

#[tokio::test]
async fn test_center_count_reference_images() {
    let db = Database::open_in_memory().unwrap();

    let ws = db.center_create_workspace("canvas_image", "Ref Count").await.unwrap();
    assert_eq!(db.center_count_reference_images(&ws.id).await.unwrap(), 0);

    // Add reference images
    let img = StudioReferenceImage {
        id: uuid::Uuid::new_v4().to_string(),
        session_id: ws.id.clone(),
        file_path: "/tmp/ref1.png".to_string(),
        sort_order: 0,
        width: Some(512),
        height: Some(512),
        created_at: "2026-01-01T00:00:00Z".to_string(),
    };
    db.studio_add_reference_image(&img).await.unwrap();
    assert_eq!(db.center_count_reference_images(&ws.id).await.unwrap(), 1);

    let img2 = StudioReferenceImage {
        id: uuid::Uuid::new_v4().to_string(),
        session_id: ws.id.clone(),
        file_path: "/tmp/ref2.png".to_string(),
        sort_order: 1,
        width: None,
        height: None,
        created_at: "2026-01-01T00:00:00Z".to_string(),
    };
    db.studio_add_reference_image(&img2).await.unwrap();
    assert_eq!(db.center_count_reference_images(&ws.id).await.unwrap(), 2);
}

#[tokio::test]
async fn test_bundle_all_asset_count() {
    let db = Database::open_in_memory().unwrap();

    let ws = db.center_create_workspace("canvas_image", "Count").await.unwrap();
    let r1 = db.center_create_round(&ws.id, "p1", "{}", "gpt-image-2").await.unwrap();
    let r2 = db.center_create_round(&ws.id, "p2", "{}", "gpt-image-2").await.unwrap();

    // Insert assets in different rounds
    let conn = db.conn().lock().await;
    for (i, rid) in [(0, &r1.id), (1, &r1.id), (2, &r2.id), (3, &r2.id)] {
        let aid = format!("cnt-asset-{}", i);
        conn.execute(
            "INSERT INTO studio_assets (asset_id, session_id, file_path, preview_path, kind, created_at)
             VALUES (?1, ?2, '/tmp/x.png', '/tmp/x.png', 'image', '2026-01-01T00:00:00Z')",
            rusqlite::params![aid, ws.id],
        ).unwrap();
        conn.execute(
            "INSERT INTO canvas_round_assets (id, round_id, asset_id, sequence, is_selected)
             VALUES (?1, ?2, ?3, ?4, 0)",
            rusqlite::params![format!("cnt-cra-{}", i), rid, aid, i],
        ).unwrap();
    }
    drop(conn);

    let bundle = db.center_get_workspace_bundle(&ws.id).await.unwrap();
    assert_eq!(bundle.all_asset_count, 4);
}

