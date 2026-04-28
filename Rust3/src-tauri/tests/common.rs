use serde::Deserialize;

#[derive(Debug, Deserialize)]
pub struct TestAppConfig {
    pub endpoints: Vec<TestEndpoint>,
}

#[derive(Debug, Deserialize)]
pub struct TestEndpoint {
    pub id: String,
    pub name: String,
    pub endpoint_type: String,
    pub url: String,
    pub api_key: String,
    pub enabled: bool,
    #[serde(flatten)]
    pub extra: serde_json::Value,
}

/// Load endpoints from Rust2's SQLite database (kv_store).
/// Requires the Rust2 database to exist at the default app data path.
pub fn load_rust2_endpoints() -> Vec<TestEndpoint> {
    let db_path = dirs::data_dir()
        .expect("could not resolve data directory")
        .join("com.truefluent.pro")
        .join("truefluent.db");

    let conn = rusqlite::Connection::open_with_flags(
        &db_path,
        rusqlite::OpenFlags::SQLITE_OPEN_READ_ONLY,
    )
    .expect("failed to open Rust2 database");

    let json_str: String = conn
        .query_row(
            "SELECT value FROM kv_store WHERE key = 'app_config'",
            [],
            |row| row.get(0),
        )
        .expect("failed to read app_config from kv_store");

    let config: TestAppConfig =
        serde_json::from_str(&json_str).expect("failed to parse app_config JSON");

    config
        .endpoints
        .into_iter()
        .filter(|e| e.enabled)
        .collect()
}

#[test]
#[ignore] // Requires Rust2 database to exist
fn can_load_rust2_endpoints() {
    let endpoints = load_rust2_endpoints();
    assert!(!endpoints.is_empty(), "Expected at least one enabled endpoint");
    for ep in &endpoints {
        assert!(!ep.id.is_empty());
        assert!(!ep.url.is_empty());
    }
}
