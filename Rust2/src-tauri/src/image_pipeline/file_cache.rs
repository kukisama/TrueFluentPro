use std::collections::HashMap;
use std::sync::RwLock;
use sha2::{Digest, Sha256};

/// P3-6: FileIdCache — 文件上传去重缓存
///
/// 对齐 C# FileIdCache.cs
/// key = SHA256(endpoint_id) + "_" + SHA256(file_content)
/// value = (file_id, uploaded_at)
/// TTL = 12 小时

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

    /// 生成缓存 key
    fn cache_key(endpoint_id: &str, file_content: &[u8]) -> String {
        let ep_hash = hex_sha256(endpoint_id.as_bytes());
        let file_hash = hex_sha256(file_content);
        format!("{ep_hash}_{file_hash}")
    }

    /// 尝试从缓存获取 file_id（未过期才返回）
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

    /// 设置缓存条目
    pub fn set(&self, endpoint_id: &str, file_content: &[u8], file_id: String) {
        let key = Self::cache_key(endpoint_id, file_content);
        if let Ok(mut inner) = self.inner.write() {
            inner.insert(key, CacheEntry {
                file_id,
                uploaded_at: std::time::Instant::now(),
            });
        }
    }

    /// 使指定条目失效
    pub fn invalidate(&self, endpoint_id: &str, file_content: &[u8]) {
        let key = Self::cache_key(endpoint_id, file_content);
        if let Ok(mut inner) = self.inner.write() {
            inner.remove(&key);
        }
    }

    /// 清除所有缓存
    pub fn clear(&self) {
        if let Ok(mut inner) = self.inner.write() {
            inner.clear();
        }
    }

    /// 清除已过期条目
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
