use rusqlite::params;
use tfp_core::{AppConfig, AppError};

use crate::db::{Database, map_db_err};

impl Database {
    pub async fn kv_get(&self, key: &str) -> tfp_core::Result<Option<String>> {
        let conn = self.conn().lock().await;
        let mut stmt = conn
            .prepare("SELECT value FROM kv_store WHERE key = ?1")
            .map_err(map_db_err)?;
        let mut rows = stmt.query(params![key]).map_err(map_db_err)?;
        match rows.next().map_err(map_db_err)? {
            Some(row) => Ok(Some(row.get(0).map_err(map_db_err)?)),
            None => Ok(None),
        }
    }

    pub async fn kv_set(&self, key: &str, value: &str) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute(
            "INSERT OR REPLACE INTO kv_store (key, value) VALUES (?1, ?2)",
            params![key, value],
        )
        .map_err(map_db_err)?;
        Ok(())
    }

    pub fn kv_get_blocking(&self, key: &str) -> tfp_core::Result<Option<String>> {
        let conn = self.conn().blocking_lock();
        let mut stmt = conn
            .prepare("SELECT value FROM kv_store WHERE key = ?1")
            .map_err(map_db_err)?;
        let mut rows = stmt.query(params![key]).map_err(map_db_err)?;
        match rows.next().map_err(map_db_err)? {
            Some(row) => Ok(Some(row.get(0).map_err(map_db_err)?)),
            None => Ok(None),
        }
    }

    pub async fn kv_delete(&self, key: &str) -> tfp_core::Result<()> {
        let conn = self.conn().lock().await;
        conn.execute("DELETE FROM kv_store WHERE key = ?1", params![key])
            .map_err(map_db_err)?;
        Ok(())
    }

    pub async fn load_config(&self) -> tfp_core::Result<AppConfig> {
        match self.kv_get("app_config").await? {
            Some(json) => serde_json::from_str(&json).map_err(|e| {
                AppError::Config(format!("failed to parse app_config: {e}"))
            }),
            None => Ok(AppConfig::default()),
        }
    }

    pub async fn save_config(&self, config: &AppConfig) -> tfp_core::Result<()> {
        let json = serde_json::to_string(config)?;
        self.kv_set("app_config", &json).await
    }
}

#[cfg(test)]
mod tests {
    use crate::Database;

    #[tokio::test]
    async fn test_kv_roundtrip() {
        let db = Database::open_in_memory().unwrap();
        db.kv_set("test_key", "test_value").await.unwrap();
        let val = db.kv_get("test_key").await.unwrap();
        assert_eq!(val, Some("test_value".to_string()));
    }

    #[tokio::test]
    async fn test_kv_get_missing() {
        let db = Database::open_in_memory().unwrap();
        let val = db.kv_get("nonexistent").await.unwrap();
        assert_eq!(val, None);
    }

    #[tokio::test]
    async fn test_config_roundtrip() {
        let db = Database::open_in_memory().unwrap();
        let config = tfp_core::AppConfig::default();
        db.save_config(&config).await.unwrap();
        let loaded = db.load_config().await.unwrap();
        assert_eq!(loaded.default_source_lang, config.default_source_lang);
        assert_eq!(loaded.default_target_langs, config.default_target_langs);
        assert_eq!(loaded.audio.sample_rate, config.audio.sample_rate);
    }
}
