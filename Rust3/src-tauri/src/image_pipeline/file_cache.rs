use std::collections::HashMap;
use std::sync::RwLock;
use sha2::{Digest, Sha256};

/// FileIdCache — file upload deduplication cache
///
/// Aligned with C# FileIdCache.cs
/// key = SHA256(endpoint_id) + "_" + SHA256(file_content)
/// value = (file_id, uploaded_at)
/// TTL = 12 hours
const TTL_SECS: u64 = 12 * 3600;

struct CacheEntry {
    file_id: String,
    uploaded_at: std::time::Instant,
}

pub struct FileIdCache {
    inner: RwLock<HashMap<String, CacheEntry>>,
}

#[allow(dead_code)]
impl FileIdCache {
    pub fn new() -> Self {
        Self {
            inner: RwLock::new(HashMap::new()),
        }
    }

    /// Generate cache key from endpoint_id and file content
    fn cache_key(endpoint_id: &str, file_content: &[u8]) -> String {
        let ep_hash = hex_sha256(endpoint_id.as_bytes());
        let file_hash = hex_sha256(file_content);
        format!("{ep_hash}_{file_hash}")
    }

    /// Try to get file_id from cache (returns None if expired)
    pub fn try_get(&self, endpoint_id: &str, file_content: &[u8]) -> Option<String> {
        let key = Self::cache_key(endpoint_id, file_content);
        let inner = self.inner.read().ok()?;
        let entry = inner.get(&key)?;
        if entry.uploaded_at.elapsed().as_secs() < TTL_SECS {
            Some(entry.file_id.clone())
        } else {
            None
        }
    }

    /// Set a cache entry
    pub fn set(&self, endpoint_id: &str, file_content: &[u8], file_id: String) {
        let key = Self::cache_key(endpoint_id, file_content);
        if let Ok(mut inner) = self.inner.write() {
            inner.insert(key, CacheEntry {
                file_id,
                uploaded_at: std::time::Instant::now(),
            });
        }
    }

    /// Invalidate a specific cache entry
    pub fn invalidate(&self, endpoint_id: &str, file_content: &[u8]) {
        let key = Self::cache_key(endpoint_id, file_content);
        if let Ok(mut inner) = self.inner.write() {
            inner.remove(&key);
        }
    }

    /// Clear all cache entries
    pub fn clear(&self) {
        if let Ok(mut inner) = self.inner.write() {
            inner.clear();
        }
    }

    /// Evict expired cache entries
    pub fn evict_expired(&self) {
        if let Ok(mut inner) = self.inner.write() {
            inner.retain(|_, entry| entry.uploaded_at.elapsed().as_secs() < TTL_SECS);
        }
    }
}

fn hex_sha256(data: &[u8]) -> String {
    let mut hasher = Sha256::new();
    hasher.update(data);
    let result = hasher.finalize();
    hex::encode(result)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_cache_hit_and_miss() {
        let cache = FileIdCache::new();
        cache.set("ep1", b"data", "fid-1".into());

        // Hit: same endpoint + same content
        assert_eq!(cache.try_get("ep1", b"data"), Some("fid-1".into()));
        // Miss: same endpoint + different content
        assert_eq!(cache.try_get("ep1", b"other"), None);
        // Miss: different endpoint + same content
        assert_eq!(cache.try_get("ep2", b"data"), None);
    }

    #[test]
    fn test_invalidate_and_clear() {
        let cache = FileIdCache::new();

        // invalidate removes a specific entry
        cache.set("ep1", b"data", "fid-1".into());
        cache.invalidate("ep1", b"data");
        assert_eq!(cache.try_get("ep1", b"data"), None);

        // clear removes all entries
        cache.set("ep1", b"a", "fid-a".into());
        cache.set("ep2", b"b", "fid-b".into());
        cache.clear();
        assert_eq!(cache.try_get("ep1", b"a"), None);
        assert_eq!(cache.try_get("ep2", b"b"), None);
    }

    #[test]
    fn test_cache_key_deterministic() {
        // Same inputs produce the same key
        let k1 = FileIdCache::cache_key("ep1", b"content");
        let k2 = FileIdCache::cache_key("ep1", b"content");
        assert_eq!(k1, k2);

        // Different inputs produce different keys
        let k3 = FileIdCache::cache_key("ep1", b"other");
        assert_ne!(k1, k3);

        let k4 = FileIdCache::cache_key("ep2", b"content");
        assert_ne!(k1, k4);
    }
}
