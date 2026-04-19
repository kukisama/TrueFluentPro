#[derive(Debug, thiserror::Error)]
pub enum StorageError {
    #[error("database error: {0}")]
    Database(#[from] sqlx::Error),

    #[error("not found: {0}")]
    NotFound(String),

    #[error("migration error: {0}")]
    Migration(String),

    #[error("internal: {0}")]
    Internal(String),
}
