//! Cache crate — cache abstraction with in-memory implementation.

pub mod memory;
pub mod redis_backend;

use std::time::Duration;
use async_trait::async_trait;

/// Abstract cache backend.
#[async_trait]
pub trait CacheBackend: Send + Sync {
    async fn get(&self, key: &str) -> Option<String>;
    async fn set(&self, key: &str, value: &str, ttl: Duration) -> anyhow::Result<()>;
    async fn delete(&self, key: &str) -> anyhow::Result<()>;
    async fn increment(&self, key: &str, amount: i64) -> anyhow::Result<i64>;
    async fn get_counter(&self, key: &str) -> anyhow::Result<i64>;
    async fn health_check(&self) -> anyhow::Result<()>;
}
