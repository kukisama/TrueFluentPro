//! Redis cache backend using the redis crate with connection manager.

use crate::CacheBackend;
use redis::AsyncCommands;
use std::time::Duration;

pub struct RedisCache {
    conn: redis::aio::ConnectionManager,
}

impl RedisCache {
    pub async fn new(redis_url: &str) -> anyhow::Result<Self> {
        let client = redis::Client::open(redis_url)
            .map_err(|e| anyhow::anyhow!("Redis client creation failed: {e}"))?;
        let conn = redis::aio::ConnectionManager::new(client)
            .await
            .map_err(|e| anyhow::anyhow!("Redis connection failed: {e}"))?;
        Ok(Self { conn })
    }
}

#[async_trait::async_trait]
impl CacheBackend for RedisCache {
    async fn get(&self, key: &str) -> Option<String> {
        let mut conn = self.conn.clone();
        conn.get(key).await.ok()
    }

    async fn set(&self, key: &str, value: &str, ttl: Duration) -> anyhow::Result<()> {
        let mut conn = self.conn.clone();
        let ttl_secs = ttl.as_secs().max(1);
        conn.set_ex(key, value, ttl_secs)
            .await
            .map_err(|e| anyhow::anyhow!("Redis SET failed: {e}"))
    }

    async fn delete(&self, key: &str) -> anyhow::Result<()> {
        let mut conn = self.conn.clone();
        conn.del(key)
            .await
            .map_err(|e| anyhow::anyhow!("Redis DEL failed: {e}"))
    }

    async fn increment(&self, key: &str, amount: i64) -> anyhow::Result<i64> {
        let mut conn = self.conn.clone();
        let val: i64 = conn
            .incr(key, amount)
            .await
            .map_err(|e| anyhow::anyhow!("Redis INCR failed: {e}"))?;
        // Set TTL if this is the first increment (key didn't have TTL)
        let ttl: i64 = redis::cmd("TTL")
            .arg(key)
            .query_async(&mut conn)
            .await
            .unwrap_or(-1);
        if ttl < 0 {
            // Set 60-second TTL for rate limiting counters
            let _: () = conn.expire(key, 60).await.unwrap_or(());
        }
        Ok(val)
    }

    async fn get_counter(&self, key: &str) -> anyhow::Result<i64> {
        let mut conn = self.conn.clone();
        let val: Option<i64> = conn
            .get(key)
            .await
            .map_err(|e| anyhow::anyhow!("Redis GET counter failed: {e}"))?;
        Ok(val.unwrap_or(0))
    }

    async fn health_check(&self) -> anyhow::Result<()> {
        let mut conn = self.conn.clone();
        let pong: String = redis::cmd("PING")
            .query_async(&mut conn)
            .await
            .map_err(|e| anyhow::anyhow!("Redis PING failed: {e}"))?;
        if pong == "PONG" {
            Ok(())
        } else {
            Err(anyhow::anyhow!("Redis PING returned: {pong}"))
        }
    }
}
