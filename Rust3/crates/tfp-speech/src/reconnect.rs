//! Auto-reconnect policy with exponential backoff + jitter.
//!
//! Aligned with C# SpeechTranslationService flow 3.

use std::sync::atomic::{AtomicU32, Ordering};
use std::time::Duration;

/// Configurable reconnect policy with exponential backoff.
pub struct ReconnectPolicy {
    pub max_attempts: u32,
    pub base_delay_ms: u64,
    pub max_delay_ms: u64,
    attempt: AtomicU32,
}

impl ReconnectPolicy {
    pub fn new(max_attempts: u32, base_delay_ms: u64, max_delay_ms: u64) -> Self {
        Self {
            max_attempts,
            base_delay_ms,
            max_delay_ms,
            attempt: AtomicU32::new(0),
        }
    }

    /// Calculate delay for the current attempt, then increment.
    pub fn calculate_delay(&self) -> Duration {
        let attempt = self.attempt.fetch_add(1, Ordering::SeqCst);
        let exp_delay = self.base_delay_ms.saturating_mul(1u64 << attempt.min(10));
        let capped = exp_delay.min(self.max_delay_ms);
        // Add jitter: ±25%
        let jitter_range = capped / 4;
        let jitter = if jitter_range > 0 {
            // Simple deterministic jitter based on attempt number
            let offset = (attempt as u64 * 7919) % (jitter_range * 2);
            offset.saturating_sub(jitter_range)
        } else {
            0
        };
        let final_ms = (capped as i64 + jitter as i64).max(0) as u64;
        Duration::from_millis(final_ms)
    }

    /// Current attempt count.
    pub fn current_attempt(&self) -> u32 {
        self.attempt.load(Ordering::SeqCst)
    }

    /// Whether we should attempt another reconnect.
    pub fn should_reconnect(&self) -> bool {
        self.attempt.load(Ordering::SeqCst) < self.max_attempts
    }

    /// Reset attempt counter (call after successful reconnect).
    pub fn reset(&self) {
        self.attempt.store(0, Ordering::SeqCst);
    }
}

impl Default for ReconnectPolicy {
    fn default() -> Self {
        Self::new(10, 1000, 30000)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_first_delay() {
        let policy = ReconnectPolicy::new(5, 1000, 30000);
        let delay = policy.calculate_delay();
        // First attempt (0): base * 2^0 = 1000ms ± jitter
        assert!(delay.as_millis() >= 750 && delay.as_millis() <= 1250,
            "first delay should be ~1000ms, got {}ms", delay.as_millis());
    }

    #[test]
    fn test_exponential_increase() {
        let policy = ReconnectPolicy::new(10, 1000, 30000);
        let d0 = policy.calculate_delay(); // attempt 0: ~1000
        let d1 = policy.calculate_delay(); // attempt 1: ~2000
        let d2 = policy.calculate_delay(); // attempt 2: ~4000
        // Each should roughly double (within jitter)
        assert!(d1.as_millis() > d0.as_millis(),
            "d1 {}ms should be > d0 {}ms", d1.as_millis(), d0.as_millis());
        assert!(d2.as_millis() > d1.as_millis(),
            "d2 {}ms should be > d1 {}ms", d2.as_millis(), d1.as_millis());
    }

    #[test]
    fn test_max_cap() {
        let policy = ReconnectPolicy::new(20, 1000, 5000);
        for _ in 0..15 {
            let _ = policy.calculate_delay();
        }
        let delay = policy.calculate_delay();
        // Should be capped at max_delay ± jitter
        assert!(delay.as_millis() <= 6300,
            "delay should be capped near 5000ms, got {}ms", delay.as_millis());
    }

    #[test]
    fn test_should_reconnect() {
        let policy = ReconnectPolicy::new(3, 1000, 30000);
        assert!(policy.should_reconnect());
        policy.calculate_delay(); // attempt 0
        policy.calculate_delay(); // attempt 1
        policy.calculate_delay(); // attempt 2
        assert!(!policy.should_reconnect()); // 3 >= 3
    }

    #[test]
    fn test_reset() {
        let policy = ReconnectPolicy::new(3, 1000, 30000);
        policy.calculate_delay();
        policy.calculate_delay();
        assert_eq!(policy.current_attempt(), 2);
        policy.reset();
        assert_eq!(policy.current_attempt(), 0);
        assert!(policy.should_reconnect());
    }
}
