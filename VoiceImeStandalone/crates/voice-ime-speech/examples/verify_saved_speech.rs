//! Manual opt-in acceptance: saved subscription + one local synthetic PCM fixture.
use std::path::PathBuf;
use std::sync::Arc;
use std::time::{Duration, Instant};
use tokio::sync::mpsc;
use voice_ime_config::{ConfigStore, SpeechCloud};
use voice_ime_speech::audio::{ActiveCaptureStop, AudioCaptureProvider};
use voice_ime_speech::{SpeechEngineFactory, SpeechEvent, StsTokenCache, UspSpeechEngineFactory};

struct FixtureAudio(Arc<Vec<u8>>);
struct Stop(tokio::task::JoinHandle<()>);

impl ActiveCaptureStop for Stop {
    fn stop(&mut self) { self.0.abort(); }
}

impl Drop for Stop {
    fn drop(&mut self) { self.stop(); }
}

impl AudioCaptureProvider for FixtureAudio {
    fn start_capture(&self, _: &str) -> Result<(mpsc::Receiver<Vec<u8>>, Box<dyn ActiveCaptureStop>), String> {
        let (tx, rx) = mpsc::channel(8);
        let pcm = Arc::clone(&self.0);
        let job = tokio::spawn(async move {
            for chunk in pcm.chunks(640) {
                if tx.send(chunk.to_vec()).await.is_err() { return; }
                tokio::time::sleep(Duration::from_millis(20)).await;
            }
            for _ in 0..150 {
                if tx.send(vec![0; 640]).await.is_err() { return; }
                tokio::time::sleep(Duration::from_millis(20)).await;
            }
            std::future::pending::<()>().await;
        });
        Ok((rx, Box::new(Stop(job))))
    }
}

#[tokio::main]
async fn main() -> Result<(), String> {
    let pcm_path = std::env::args_os().nth(1).map(PathBuf::from)
        .ok_or_else(|| "Usage: verify_saved_speech <local synthetic 16 kHz PCM16LE file>".to_owned())?;
    let bytes = std::fs::read(pcm_path).map_err(|_| "Cannot read synthetic fixture".to_owned())?;
    if bytes.is_empty() || bytes.len() > 480_000 || bytes.len() % 2 != 0 {
        return Err("Expected at most 15 seconds of 16 kHz mono PCM16LE".into());
    }
    let mut cfg = ConfigStore::default_store().load_readonly().map_err(|e| e.to_string())?
        .freeze_runtime_session()?;
    if !matches!(cfg.endpoint.cloud, SpeechCloud::Global | SpeechCloud::China21V) {
        return Err("Cloud acceptance requires a saved Microsoft Speech endpoint".into());
    }
    if let Some(mode) = std::env::args().nth(2) {
        match mode.as_str() {
            "zh-CN" | "auto" => { cfg.recognition_language = mode; cfg.enable_translation = false; }
            "auto-translate" => { cfg.recognition_language = "auto".into(); cfg.enable_translation = true; cfg.target_language = "en-US".into(); }
            _ => return Err("Expected zh-CN, auto or auto-translate".into()),
        }
    }
    let start = Instant::now();
    let sts = StsTokenCache::new();
    sts.get_or_issue_token(&cfg.endpoint, &cfg.subscription_key).await?;
    println!("STS=ok region={} language={} translation={} elapsed_ms={}", cfg.endpoint.region,
        cfg.recognition_language, cfg.enable_translation, start.elapsed().as_millis());
    let engine = UspSpeechEngineFactory::new(sts, Arc::new(FixtureAudio(Arc::new(bytes))), true);
    let (tx, mut rx) = mpsc::unbounded_channel();
    let round = Instant::now();
    let handle = engine.start_session(cfg, None, tx);
    let result = tokio::time::timeout(Duration::from_secs(20), async {
        let mut finals = Vec::new();
        while let Some(event) = rx.recv().await {
            match event {
                SpeechEvent::Ready => println!("REALTIME_READY_MS={}", round.elapsed().as_millis()),
                SpeechEvent::OutputText { text, is_final: true } if !text.trim().is_empty() => {
                    println!("FINAL={text}");
                    finals.push(text);
                    if finals.len() == 2 { return Ok(()); }
                }
                SpeechEvent::Failed { message } => return Err(message),
                _ => {}
            }
        }
        Err("Realtime session ended before recognizing both synthetic phrases".to_owned())
    }).await;
    handle.stop_and_wait_released().await;
    result.map_err(|_| "Timed out waiting for two realtime final results".to_owned())?
}