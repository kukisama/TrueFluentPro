use std::path::Path;
use rusqlite::Connection;
use tokio::sync::Mutex;
use tfp_core::AppError;

pub struct Database {
    conn: Mutex<Connection>,
}

pub(crate) fn map_db_err(e: rusqlite::Error) -> AppError {
    AppError::Database(e.to_string())
}

impl Database {
    pub fn open(path: &Path) -> tfp_core::Result<Self> {
        let conn = Connection::open(path).map_err(map_db_err)?;
        conn.execute_batch("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;")
            .map_err(map_db_err)?;
        Self::run_migrations(&conn)?;
        Ok(Self { conn: Mutex::new(conn) })
    }

    pub fn open_in_memory() -> tfp_core::Result<Self> {
        let conn = Connection::open_in_memory().map_err(map_db_err)?;
        conn.execute_batch("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;")
            .map_err(map_db_err)?;
        Self::run_migrations(&conn)?;
        Ok(Self { conn: Mutex::new(conn) })
    }

    fn run_migrations(conn: &Connection) -> tfp_core::Result<()> {
        conn.execute_batch(
            "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER PRIMARY KEY);"
        ).map_err(map_db_err)?;

        let current: i64 = conn
            .query_row(
                "SELECT COALESCE(MAX(version), 0) FROM schema_version",
                [],
                |row| row.get(0),
            )
            .map_err(map_db_err)?;

        if current < 1 {
            conn.execute_batch(include_str!("migrations/v1.sql")).map_err(map_db_err)?;
            conn.execute("INSERT INTO schema_version VALUES(1)", []).map_err(map_db_err)?;
        }
        if current < 2 {
            conn.execute_batch(include_str!("migrations/v2.sql")).map_err(map_db_err)?;
            conn.execute("INSERT INTO schema_version VALUES(2)", []).map_err(map_db_err)?;
        }
        if current < 3 {
            conn.execute_batch(include_str!("migrations/v3.sql")).map_err(map_db_err)?;
            conn.execute("INSERT INTO schema_version VALUES(3)", []).map_err(map_db_err)?;
        }
        if current < 4 {
            conn.execute_batch(include_str!("migrations/v4.sql")).map_err(map_db_err)?;
            conn.execute("INSERT INTO schema_version VALUES(4)", []).map_err(map_db_err)?;
        }
        if current < 5 {
            conn.execute_batch(include_str!("migrations/v5.sql")).map_err(map_db_err)?;
            conn.execute("INSERT INTO schema_version VALUES(5)", []).map_err(map_db_err)?;
        }
        if current < 6 {
            conn.execute_batch(include_str!("migrations/v6.sql")).map_err(map_db_err)?;
            conn.execute("INSERT INTO schema_version VALUES(6)", []).map_err(map_db_err)?;
        }
        if current < 7 {
            conn.execute_batch(include_str!("migrations/v7.sql")).map_err(map_db_err)?;
            conn.execute("INSERT INTO schema_version VALUES(7)", []).map_err(map_db_err)?;
        }

        Ok(())
    }

    pub(crate) fn conn(&self) -> &Mutex<Connection> {
        &self.conn
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_open_in_memory() {
        let db = Database::open_in_memory();
        assert!(db.is_ok(), "open_in_memory failed: {:?}", db.err());
    }

    #[test]
    fn test_migration_idempotent() {
        let db1 = Database::open_in_memory().unwrap();
        drop(db1);
        let db2 = Database::open_in_memory();
        assert!(db2.is_ok(), "second open_in_memory failed: {:?}", db2.err());
    }

    #[test]
    fn test_schema_version() {
        let db = Database::open_in_memory().unwrap();
        let conn = db.conn.blocking_lock();
        let version: i64 = conn
            .query_row("SELECT MAX(version) FROM schema_version", [], |r| r.get(0))
            .unwrap();
        assert_eq!(version, 7);
    }
}
