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
        Ok(*self.counters.get(key).map(|v| *v).get_or_insert(0))
    }

    async fn health_check(&self) -> anyhow::Result<()> {
        Ok(())
    }
}
