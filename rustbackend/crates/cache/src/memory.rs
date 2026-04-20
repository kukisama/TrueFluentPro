//! In-memory cache using DashMap.

use crate::CacheBackend;
use dashmap::DashMap;
use std::time::{Duration, Instant};

struct CacheEntry {
    value: String,
    expires_at: Instant,
}

pub struct InMemoryCache {
    data: DashMap<String, CacheEntry>,
    counters: DashMap<String, i64>,
}

impl InMemoryCache {
    pub fn new() -> Self {
        Self {
            data: DashMap::new(),
            counters: DashMap::new(),
        }
    }
}

impl Default for InMemoryCache {
    fn default() -> Self {
        Self::new()
    }
}

#[async_trait::async_trait]
impl CacheBackend for InMemoryCache {
    async fn get(&self, key: &str) -> Option<String> {
        let entry = self.data.get(key)?;
        if Instant::now() > entry.expires_at {
            drop(entry);
            self.data.remove(key);
            return None;
        }
        Some(entry.value.clone())
    }

    async fn set(&self, key: &str, value: &str, ttl: Duration) -> anyhow::Result<()> {
        self.data.insert(
            key.to_string(),
            CacheEntry {
                value: value.to_string(),
                expires_at: Instant::now() + ttl,
            },
        );
        Ok(())
    }

    async fn delete(&self, key: &str) -> anyhow::Result<()> {
        self.data.remove(key);
        Ok(())
    }

    async fn increment(&self, key: &str, amount: i64) -> anyhow::Result<i64> {
        let mut entry = self.counters.entry(key.to_string()).or_insert(0);
        *entry += amount;
        Ok(*entry)
    }

    async fn get_counter(&self, key: &str) -> anyhow::Result<i64> {
        Ok(self.counters.get(key).map(|v| *v).unwrap_or(0))
    }

    async fn health_check(&self) -> anyhow::Result<()> {
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::Duration;

    #[tokio::test]
    async fn test_set_and_get() {
        let cache = InMemoryCache::new();
        cache.set("key1", "value1", Duration::from_secs(60)).await.unwrap();
        assert_eq!(cache.get("key1").await, Some("value1".to_string()));
    }

    #[tokio::test]
    async fn test_get_missing_key() {
        let cache = InMemoryCache::new();
        assert_eq!(cache.get("nonexistent").await, None);
    }

    #[tokio::test]
    async fn test_ttl_expiry() {
        let cache = InMemoryCache::new();
        cache.set("key1", "value1", Duration::from_millis(50)).await.unwrap();
        assert_eq!(cache.get("key1").await, Some("value1".to_string()));
        tokio::time::sleep(Duration::from_millis(100)).await;
        assert_eq!(cache.get("key1").await, None);
    }

    #[tokio::test]
    async fn test_delete() {
        let cache = InMemoryCache::new();
        cache.set("key1", "value1", Duration::from_secs(60)).await.unwrap();
        cache.delete("key1").await.unwrap();
        assert_eq!(cache.get("key1").await, None);
    }

    #[tokio::test]
    async fn test_increment() {
        let cache = InMemoryCache::new();
        let val = cache.increment("counter1", 1).await.unwrap();
        assert_eq!(val, 1);
        let val = cache.increment("counter1", 5).await.unwrap();
        assert_eq!(val, 6);
    }

    #[tokio::test]
    async fn test_get_counter() {
        let cache = InMemoryCache::new();
        assert_eq!(cache.get_counter("c1").await.unwrap(), 0);
        cache.increment("c1", 3).await.unwrap();
        assert_eq!(cache.get_counter("c1").await.unwrap(), 3);
    }

    #[tokio::test]
    async fn test_health_check() {
        let cache = InMemoryCache::new();
        assert!(cache.health_check().await.is_ok());
    }

    #[tokio::test]
    async fn test_overwrite() {
        let cache = InMemoryCache::new();
        cache.set("key1", "v1", Duration::from_secs(60)).await.unwrap();
        cache.set("key1", "v2", Duration::from_secs(60)).await.unwrap();
        assert_eq!(cache.get("key1").await, Some("v2".to_string()));
    }
}
