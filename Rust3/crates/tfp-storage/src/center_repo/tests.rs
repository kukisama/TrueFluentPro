use crate::Database;

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
