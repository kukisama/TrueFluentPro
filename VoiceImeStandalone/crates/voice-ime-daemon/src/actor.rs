//! 守护进程单线程 Tokio Actor 核心协调器（1:1 严谨复刻 `VoiceImeSharedHost.cs` 全部状态机契约）。
//!
//! 核心安全与稳定性保证：
//! 1. 单一 Actor 串行消费所有客户端命令、悬浮条回调、语音识别回调与定时器事件，零锁竞争死锁；
//! 2. `Epoch + Generation + TargetRevision` 原子共享视图在管道写协程出队写盘前再次核验，杜绝切换窗口后串字；
//! 3. 原文听写模式使用 `slice_from_utf16_boundary` 处理跨目标切换时的代理对安全切片；
//! 4. 语音翻译模式使用 `TranslationTargetQueue` 锁定句首目标，杜绝译文迟到落入新窗口；
//! 5. 60 秒连续无字自动停麦释放计费会话，15 秒用量检查点持久化，`maintenance.lock` 实时熔断。

use std::collections::HashMap;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Weak};
use std::time::{Duration, Instant};

use tokio::io::{AsyncRead, AsyncWrite};
use tokio::sync::{mpsc, oneshot, watch, Semaphore};
use uuid::Uuid;

use voice_ime_config::{RuntimeConfigCache, RuntimeSpeechSessionConfig};
use voice_ime_protocol::{
    read_raw_frame, slice_from_utf16_boundary, utf16_len, write_raw_frame, ClientCommand,
    ClientFrame, DeliveryFence, PeerId, ServerFrame, ServerKind, TargetBinding,
    TranslationTargetQueue,
};
use voice_ime_speech::{PrewarmedSpeechHandle, SpeechEngineFactory, SpeechEvent, SpeechSessionHandle};
use voice_ime_storage::{StopReason, UsageWriterService};
use voice_ime_win32::{
    ExclusiveFileLock, MaintenanceGate, PipePeerVerifier, ToolbarAction, ToolbarDriver,
};

use crate::launcher::{ToolsTabRequest, ToolsWindowLauncher};

const NO_TEXT_INACTIVITY_TIMEOUT: Duration = Duration::from_secs(60);
const USAGE_CHECKPOINT_INTERVAL: Duration = Duration::from_secs(15);
const HANDSHAKE_TIMEOUT: Duration = Duration::from_secs(5);
const TARGET_REBIND_GRACE: Duration = Duration::from_millis(350);
const PREWARM_HANDOFF_BUDGET: Duration = Duration::from_millis(150);
const PREWARM_REFRESH_AGE: Duration = Duration::from_secs(35);
const MAX_CONCURRENT_PEERS: usize = 64;
const GENERIC_ERROR_TEXT: &str = "语音输入已中断，请在输入法设置中检查语音终结点与麦克风后重试。";

/// 供各客户端写管道协程无锁原子核验的共享投递栅栏快照。
pub struct AtomicFenceView {
    pub epoch: AtomicU64,
    pub target_revision: AtomicU64,
    pub target_peer_id: AtomicU64,
    pub target_generation: AtomicU64,
}

impl Default for AtomicFenceView {
    fn default() -> Self {
        Self {
            epoch: AtomicU64::new(0),
            target_revision: AtomicU64::new(0),
            target_peer_id: AtomicU64::new(0),
            target_generation: AtomicU64::new(0),
        }
    }
}

impl AtomicFenceView {
    fn set_epoch(&self, epoch: u64) {
        self.epoch.store(epoch, Ordering::SeqCst);
    }

    fn set_target(&self, target: Option<TargetBinding>, revision: u64) {
        match target {
            Some(t) => {
                self.target_peer_id.store(t.peer_id, Ordering::SeqCst);
                self.target_generation.store(t.generation, Ordering::SeqCst);
            }
            None => {
                self.target_peer_id.store(0, Ordering::SeqCst);
                self.target_generation.store(0, Ordering::SeqCst);
            }
        }
        self.target_revision.store(revision, Ordering::SeqCst);
    }

    pub fn verify(&self, fence: &DeliveryFence) -> bool {
        let cur_epoch = self.epoch.load(Ordering::SeqCst);
        let cur_rev = self.target_revision.load(Ordering::SeqCst);
        let cur_peer = self.target_peer_id.load(Ordering::SeqCst);
        let cur_gen = self.target_generation.load(Ordering::SeqCst);
        let cur_target = if cur_peer == 0 {
            None
        } else {
            Some(TargetBinding {
                peer_id: cur_peer,
                generation: cur_gen,
            })
        };
        fence.matches(cur_epoch, cur_target, cur_rev)
    }
}

#[derive(Debug, Clone)]
struct OutboundItem {
    frame: ServerFrame,
    text_fence: Option<DeliveryFence>,
}

struct PeerRecord {
    client_pid: u32,
    thread_id: u32,
    generation: u64,
    out_tx: mpsc::Sender<OutboundItem>,
}

enum ActorMessage {
    RegisterPeer {
        peer_id: PeerId,
        client_pid: u32,
        thread_id: u32,
        out_tx: mpsc::Sender<OutboundItem>,
        ack: oneshot::Sender<()>,
    },
    UnregisterPeer {
        peer_id: PeerId,
        ack: oneshot::Sender<()>,
    },
    PeerCommand {
        peer_id: PeerId,
        command: ClientCommand,
        ack: oneshot::Sender<()>,
    },
    Toolbar(ToolbarAction),
    TargetRebindTimeout { round_epoch: u64, target_revision: u64 },
    SpeechRoundEvent {
        round_epoch: u64,
        event: SpeechEvent,
    },
    CheckInactivityTimeout {
        round_epoch: u64,
    },
    UsageCheckpoint {
        round_epoch: u64,
    },
    UsageStatusUpdated,
    Shutdown,
}

struct ActiveAudioRound {
    epoch: u64,
    translating: bool,
    ready: bool,
    last_text_at: Instant,
    usage_session: Option<Uuid>,
    translation_targets: TranslationTargetQueue,
    session_handle: Option<SpeechSessionHandle>,
    mic_lock: Option<Arc<ExclusiveFileLock>>,
}

struct PendingPrewarm {
    config: RuntimeSpeechSessionConfig,
    started: Instant,
    task: tokio::task::JoinHandle<Option<PrewarmedSpeechHandle>>,
}

impl Drop for PendingPrewarm {
    fn drop(&mut self) { self.task.abort(); }
}

#[derive(Clone)]
pub struct SharedDaemonHandle {
    tx: mpsc::Sender<ActorMessage>,
    next_peer_id: Arc<AtomicU64>,
    fence_view: Arc<AtomicFenceView>,
    verifier: Arc<dyn PipePeerVerifier>,
    slots: Arc<Semaphore>,
}

impl SharedDaemonHandle {
    pub fn slots(&self) -> &Arc<Semaphore> {
        &self.slots
    }

    pub async fn shutdown(&self) {
        let _ = self.tx.send(ActorMessage::Shutdown).await;
    }
}

pub struct SharedDaemonActor {
    config_cache: Arc<RuntimeConfigCache>,
    speech_factory: Arc<dyn SpeechEngineFactory>,
    verifier: Arc<dyn PipePeerVerifier>,
    toolbar: Arc<dyn ToolbarDriver>,
    tools_launcher: Arc<dyn ToolsWindowLauncher>,
    usage: Option<Arc<UsageWriterService>>,
    maintenance: MaintenanceGate,
    mic_lock_path: Option<PathBuf>,
    fence_view: Arc<AtomicFenceView>,

    rx: mpsc::Receiver<ActorMessage>,
    self_tx: mpsc::Sender<ActorMessage>,

    peers: HashMap<PeerId, PeerRecord>,
    selection: Option<TargetBinding>,
    target: Option<TargetBinding>,
    target_revision: u64,
    epoch: u64,
    sentence: String,
    boundary_utf16: usize,
    delivered_utf16: usize,
    status: ServerKind,
    visible: bool,
    stopped_flag: bool,
    error_flag: bool,

    prewarm: Option<PendingPrewarm>,
    audio: Option<ActiveAudioRound>,
    previous_release: Option<tokio::task::JoinHandle<()>>,
    releasing_mic: Weak<ExclusiveFileLock>,
}

impl SharedDaemonActor {
    #[allow(clippy::too_many_arguments)]
    pub fn spawn(
        config_cache: Arc<RuntimeConfigCache>,
        speech_factory: Arc<dyn SpeechEngineFactory>,
        verifier: Arc<dyn PipePeerVerifier>,
        toolbar: Arc<dyn ToolbarDriver>,
        tools_launcher: Arc<dyn ToolsWindowLauncher>,
        usage: Option<Arc<UsageWriterService>>,
        maintenance: MaintenanceGate,
        mic_lock_path: Option<PathBuf>,
        mut toolbar_rx: mpsc::UnboundedReceiver<ToolbarAction>,
    ) -> (SharedDaemonHandle, tokio::task::JoinHandle<()>) {
        let (tx, rx) = mpsc::channel::<ActorMessage>(256);
        let fence_view = Arc::new(AtomicFenceView::default());

        // 转发悬浮条动作至统一 Actor 队列
        let tx_toolbar = tx.clone();
        tokio::spawn(async move {
            while let Some(act) = toolbar_rx.recv().await {
                if tx_toolbar.send(ActorMessage::Toolbar(act)).await.is_err() {
                    break;
                }
            }
        });

        // 监听用量数据库隐私状态变更同步悬浮条指示灯
        if let Some(ref u) = usage {
            let mut status_rx = u.subscribe_status();
            let tx_usage = tx.clone();
            tokio::spawn(async move {
                while status_rx.changed().await.is_ok() {
                    if tx_usage.send(ActorMessage::UsageStatusUpdated).await.is_err() {
                        break;
                    }
                }
            });
        }

        // 监听 maintenance.lock 100ms 熔断门禁
        let (maint_rx, maint_task) = maintenance.spawn_monitor();

        let mut actor = Self {
            config_cache,
            speech_factory,
            verifier: Arc::clone(&verifier),
            toolbar,
            tools_launcher,
            usage,
            maintenance,
            mic_lock_path,
            fence_view: Arc::clone(&fence_view),
            rx,
            self_tx: tx.clone(),
            peers: HashMap::new(),
            selection: None,
            target: None,
            target_revision: 0,
            epoch: 0,
            sentence: String::new(),
            boundary_utf16: 0,
            delivered_utf16: 0,
            status: ServerKind::Idle,
            visible: false,
            stopped_flag: false,
            error_flag: false,
            prewarm: None,
            audio: None,
            previous_release: None,
            releasing_mic: Weak::new(),
        };

        let join = tokio::spawn(async move {
            actor.run_loop(maint_rx).await;
            maint_task.abort();
        });

        let handle = SharedDaemonHandle {
            tx,
            next_peer_id: Arc::new(AtomicU64::new(1)),
            fence_view,
            verifier,
            slots: Arc::new(Semaphore::new(MAX_CONCURRENT_PEERS)),
        };
        (handle, join)
    }

    async fn run_loop(&mut self, mut maintenance: watch::Receiver<bool>) {
        self.sync_history_indicator();
        while !*maintenance.borrow() {
            let msg = tokio::select! {
                biased;
                _ = maintenance.changed() => break,
                msg = self.rx.recv() => match msg { Some(msg) => msg, None => break },
            };
            match msg {
                ActorMessage::RegisterPeer {
                    peer_id,
                    client_pid,
                    thread_id,
                    out_tx,
                    ack,
                } => {
                    let peer = PeerRecord {
                        client_pid,
                        thread_id,
                        generation: 0,
                        out_tx,
                    };
                    self.send_status_to_peer(&peer, self.status, "");
                    self.peers.insert(peer_id, peer);
                    let _ = ack.send(());
                }
                ActorMessage::UnregisterPeer { peer_id, ack } => {
                    self.peers.remove(&peer_id);
                    let was_target = self.target.is_some_and(|t| t.peer_id == peer_id);
                    let was_selection = self.selection.is_some_and(|s| s.peer_id == peer_id);
                    self.unbind(peer_id, None);
                    if was_selection {
                        self.selection = self.target.filter(|t| self.is_peer_foreground(t.peer_id));
                        if self.selection.is_none() {
                            self.other_ime();
                        }
                    } else if was_target {
                        // Stop recording for the lost editor, but do not hide or deselect
                        // a newer foreground client that has not bound its editor yet.
                        if self.selection.is_some_and(|s| self.is_peer_foreground(s.peer_id)) {
                            self.stop_audio(StopReason::OtherIme);
                        } else {
                            self.other_ime();
                        }
                    }
                    let _ = ack.send(());
                }
                ActorMessage::PeerCommand {
                    peer_id,
                    command,
                    ack,
                } => {
                    self.handle_peer_command(peer_id, command);
                    let _ = ack.send(());
                }
                ActorMessage::Toolbar(action) => self.handle_toolbar_action(action),
                ActorMessage::TargetRebindTimeout { round_epoch, target_revision } => {
                    if self.epoch == round_epoch && self.target_revision == target_revision && self.target.is_none() {
                        self.stop_audio(StopReason::OtherIme);
                    }
                }
                ActorMessage::SpeechRoundEvent { round_epoch, event } => {
                    self.handle_speech_event(round_epoch, event);
                }
                ActorMessage::CheckInactivityTimeout { round_epoch } => {
                    self.check_inactivity_timeout(round_epoch);
                }
                ActorMessage::UsageCheckpoint { round_epoch } => {
                    self.handle_usage_checkpoint(round_epoch);
                }
                ActorMessage::UsageStatusUpdated => {
                    self.sync_history_indicator();
                }
                ActorMessage::Shutdown => break,
            }
        }

        // 优雅收尾：停麦、清预热、关悬浮条、等待底层设备释放
        self.stop_audio(StopReason::OwnerExit);
        self.discard_prewarm();
        self.visible = false;
        self.update_ui();
        self.peers.clear();
        self.toolbar.close();
        if let Some(rel) = self.previous_release.take() {
            let _ = rel.await;
        }
        if let Some(ref u) = self.usage {
            u.shutdown();
            let _ = u.flush().await;
        }
    }

    fn handle_toolbar_action(&mut self, action: ToolbarAction) {
        match action {
            ToolbarAction::ToggleRecording => self.toggle_recording(),
            ToolbarAction::ProfileSelected(true) => {
                self.visible = true;
                self.update_ui();
                self.maybe_trigger_prewarm();
            }
            ToolbarAction::ProfileSelected(false) => self.other_ime(),
            ToolbarAction::OpenSettings => {
                self.discard_prewarm();
                self.tools_launcher.open_tab(ToolsTabRequest::Settings);
            }
            ToolbarAction::OpenUsage => {
                self.tools_launcher.open_tab(ToolsTabRequest::Usage);
            }
            ToolbarAction::OpenLanguage => {
                self.discard_prewarm();
                self.tools_launcher.open_tab(ToolsTabRequest::Language);
            }
            ToolbarAction::SetHistoryConsent(enabled) => {
                if let Some(ref u) = self.usage {
                    let u_clone = Arc::clone(u);
                    tokio::spawn(async move {
                        let _ = u_clone.set_history_enabled(enabled).await;
                    });
                }
            }
        }
    }

    fn handle_peer_command(&mut self, peer_id: PeerId, command: ClientCommand) {
        let Some(peer_snapshot) = self.peers.get(&peer_id).map(|p| {
            (p.client_pid, p.thread_id, p.generation)
        }) else {
            return;
        };
        let (client_pid, thread_id, current_gen) = peer_snapshot;

        match command {
            ClientCommand::Hello { .. } => {}
            ClientCommand::UnbindTarget { generation } => {
                self.unbind(peer_id, Some(generation));
            }
            ClientCommand::Deselected { generation } => {
                if generation < current_gen {
                    return;
                }
                let matches_selection = self
                    .selection
                    .map(|s| s.peer_id == peer_id && generation >= s.generation)
                    .unwrap_or(false);
                if matches_selection
                    || (self.selection.is_none() && self.visible)
                    || self.verifier.is_foreground(client_pid, thread_id)
                {
                    self.other_ime();
                }
            }
            ClientCommand::Selected { generation } => {
                if !self.verifier.is_foreground(client_pid, thread_id) || generation < current_gen {
                    return;
                }
                if let Some(p) = self.peers.get_mut(&peer_id) {
                    p.generation = generation;
                }
                if let Some(bound) = self.target {
                    if bound.peer_id == peer_id && bound.generation != generation {
                        self.unbind(peer_id, Some(bound.generation));
                    }
                }
                self.selection = Some(TargetBinding {
                    peer_id,
                    generation,
                });
                self.visible = true;
                self.update_ui();
                self.maybe_trigger_prewarm();
            }
            ClientCommand::BindTarget { generation } => {
                if generation == 0
                    || !self.verifier.is_foreground(client_pid, thread_id)
                    || generation < current_gen
                {
                    return;
                }
                if let Some(p) = self.peers.get_mut(&peer_id) {
                    p.generation = generation;
                }
                let next_target = TargetBinding {
                    peer_id,
                    generation,
                };
                self.selection = Some(next_target);
                if self.target == Some(next_target) {
                    return;
                }
                self.boundary_utf16 = utf16_len(&self.sentence).max(self.delivered_utf16);
                self.target_revision += 1;
                self.target = Some(next_target);
                self.fence_view.set_target(self.target, self.target_revision);
                self.maybe_trigger_prewarm();
            }
            ClientCommand::Toggle { generation } => {
                if !self.verifier.is_foreground(client_pid, thread_id) || generation < current_gen {
                    return;
                }
                if let Some(p) = self.peers.get_mut(&peer_id) {
                    p.generation = generation;
                }
                if let Some(bound) = self.target {
                    if bound.peer_id == peer_id && bound.generation != generation {
                        self.unbind(peer_id, Some(bound.generation));
                    }
                }
                self.toggle_recording();
            }
        }
    }

    fn unbind(&mut self, peer_id: PeerId, generation: Option<u64>) {
        let Some(bound) = self.target else { return };
        if bound.peer_id != peer_id {
            return;
        }
        if let Some(g) = generation {
            if bound.generation != g {
                return;
            }
        }
        self.target = None;
        self.target_revision += 1;
        self.fence_view.set_target(None, self.target_revision);
        self.boundary_utf16 = utf16_len(&self.sentence).max(self.delivered_utf16);
        if let Some(round) = &self.audio {
            let round_epoch = round.epoch;
            let target_revision = self.target_revision;
            let tx = self.self_tx.clone();
            tokio::spawn(async move {
                tokio::time::sleep(TARGET_REBIND_GRACE).await;
                let _ = tx.send(ActorMessage::TargetRebindTimeout { round_epoch, target_revision }).await;
            });
        }
    }

    fn other_ime(&mut self) {
        self.stop_audio(StopReason::OtherIme);
        self.discard_prewarm();
        self.selection = None;
        self.target = None;
        self.target_revision += 1;
        self.fence_view.set_target(None, self.target_revision);
        self.visible = false;
        self.update_ui();
    }

    fn maybe_trigger_prewarm(&mut self) {
        if !self.visible || self.audio.is_some() {
            return;
        }
        let Ok(cfg) = self.config_cache.get() else {
            self.discard_prewarm();
            return;
        };
        if !cfg.enable_prewarm {
            self.discard_prewarm();
            return;
        }
        let Ok(runtime_cfg) = cfg.freeze_runtime_session() else {
            self.discard_prewarm();
            return;
        };
        if self.prewarm.as_ref().is_some_and(|p| p.config == runtime_cfg && p.started.elapsed() < PREWARM_REFRESH_AGE) {
            return;
        }
        self.discard_prewarm();
        self.prewarm = Some(PendingPrewarm {
            task: self.speech_factory.prewarm(runtime_cfg.clone()),
            config: runtime_cfg,
            started: Instant::now(),
        });
    }

    fn discard_prewarm(&mut self) {
        self.prewarm = None;
    }

    fn toggle_recording(&mut self) {
        if self.audio.is_some() {
            self.stop_audio(StopReason::ManualStop);
            return;
        }
        let Some(bound) = self.target else { return };
        let Some(peer) = self.peers.get(&bound.peer_id) else {
            return;
        };
        if !self.verifier.is_foreground(peer.client_pid, peer.thread_id)
            || self.maintenance.is_active()
        {
            return;
        }

        let mic_lock = if let Some(ref mic_path) = self.mic_lock_path {
            if let Some(ours) = self.releasing_mic.upgrade() {
                Some(ours)
            } else {
                match ExclusiveFileLock::try_acquire(mic_path) {
                    Ok(Some(lock)) => Some(Arc::new(lock)),
                    _ => {
                        self.fail_round_immediately("另一个语音输入法实例正在使用麦克风，请先停止它。");
                        return;
                    }
                }
            }
        } else {
            None
        };

        let cfg = match self.config_cache.get() {
            Ok(c) => c,
            Err(e) => {
                self.fail_round_immediately(&e.to_string());
                return;
            }
        };
        let runtime_cfg = match cfg.freeze_runtime_session() {
            Ok(r) => r,
            Err(msg) => {
                self.fail_round_immediately(&msg);
                return;
            }
        };
        let pending_warm = self.prewarm.take().filter(|p| p.config == runtime_cfg && p.started.elapsed() < PREWARM_REFRESH_AGE);

        self.epoch += 1;
        let round_epoch = self.epoch;
        self.fence_view.set_epoch(round_epoch);

        let usage_session = self.usage.as_ref().map(|u| u.begin_session());
        self.sentence.clear();
        self.boundary_utf16 = 0;
        self.delivered_utf16 = 0;
        self.status = ServerKind::Connecting;
        self.stopped_flag = false;
        self.error_flag = false;
        self.broadcast_status(ServerKind::Connecting, "");
        self.update_ui();

        let (ev_tx, mut ev_rx) = mpsc::unbounded_channel::<SpeechEvent>();
        let tx_actor = self.self_tx.clone();
        tokio::spawn(async move {
            while let Some(ev) = ev_rx.recv().await {
                if tx_actor
                    .send(ActorMessage::SpeechRoundEvent {
                        round_epoch,
                        event: ev,
                    })
                    .await
                    .is_err()
                {
                    break;
                }
            }
        });

        // 等待上一轮底层会话彻底释放后再启动新一轮识别器
        let prev_release = self.previous_release.take();
        let factory = Arc::clone(&self.speech_factory);
        let translating = runtime_cfg.enable_translation;
        let (stop_tx, mut stop_rx) = oneshot::channel();
        let (release_tx, release_rx) = oneshot::channel();
        tokio::spawn(async move {
            if let Some(prev) = prev_release {
                let _ = prev.await;
            }
            let warm = if let Some(mut pending) = pending_warm {
                tokio::select! {
                    biased;
                    _ = &mut stop_rx => return,
                    result = tokio::time::timeout(PREWARM_HANDOFF_BUDGET, &mut pending.task) => {
                        result.ok().and_then(Result::ok).flatten()
                    }
                }
            } else { None };
            if matches!(stop_rx.try_recv(), Err(oneshot::error::TryRecvError::Empty)) {
                let handle = factory.start_session(runtime_cfg, warm, ev_tx);
                let _ = stop_rx.await;
                handle.stop_and_wait_released().await;
            }
            let _ = release_tx.send(());
        });

        self.audio = Some(ActiveAudioRound {
            epoch: round_epoch,
            translating,
            ready: false,
            last_text_at: Instant::now(),
            usage_session,
            translation_targets: TranslationTargetQueue::new(),
            session_handle: Some(SpeechSessionHandle::from_channels(stop_tx, release_rx)),
            mic_lock,
        });
    }

    fn fail_round_immediately(&mut self, message: &str) {
        self.epoch += 1;
        self.fence_view.set_epoch(self.epoch);
        self.status = ServerKind::Error;
        self.error_flag = true;
        self.stopped_flag = false;
        self.broadcast_status(ServerKind::Error, message);
        self.update_ui();
    }

    fn stop_audio(&mut self, reason: StopReason) {
        let Some(mut round) = self.audio.take() else {
            return;
        };
        self.epoch += 1;
        self.fence_view.set_epoch(self.epoch);

        if let Some(usage_id) = round.usage_session {
            if let Some(ref u) = self.usage {
                let finalize_pending = reason == StopReason::ManualStop && !round.translating;
                u.end_session(usage_id, reason, finalize_pending);
            }
        }

        if let Some(handle) = round.session_handle.take() {
            let mic_lock = round.mic_lock.take();
            self.releasing_mic = mic_lock.as_ref().map(Arc::downgrade).unwrap_or_default();
            let prev = self.previous_release.take();
            self.previous_release = Some(tokio::spawn(async move {
                if let Some(p) = prev {
                    let _ = p.await;
                }
                handle.stop_and_wait_released().await;
                drop(mic_lock);
            }));
        }

        self.sentence.clear();
        self.boundary_utf16 = 0;
        self.delivered_utf16 = 0;
        self.status = ServerKind::Stopped;
        self.stopped_flag = true;
        self.broadcast_status(ServerKind::Stopped, "");
        self.update_ui();
    }

    fn handle_speech_event(&mut self, round_epoch: u64, event: SpeechEvent) {
        let is_current = self
            .audio
            .as_ref()
            .map(|r| r.epoch == round_epoch && self.epoch == round_epoch)
            .unwrap_or(false);
        if !is_current {
            return;
        }

        match event {
            SpeechEvent::Ready => {
                if let Some(ref mut round) = self.audio {
                    round.ready = true;
                    round.last_text_at = Instant::now();
                    if let Some(uid) = round.usage_session {
                        if let Some(ref u) = self.usage {
                            u.recording_started(uid);
                        }
                    }
                }
                self.status = ServerKind::Recording;
                self.broadcast_status(ServerKind::Recording, "");
                self.update_ui();
                self.schedule_inactivity_check(round_epoch, NO_TEXT_INACTIVITY_TIMEOUT);
                self.schedule_usage_checkpoint(round_epoch);
            }
            SpeechEvent::SourceRecognized { text, is_final } => {
                let active_target = self.target;
                let active_rev = self.target_revision;
                if let Some(ref mut round) = self.audio {
                    if let Some(uid) = round.usage_session.filter(|_| active_target.is_some()) {
                        if let Some(ref u) = self.usage {
                            u.recognized(uid, &text, is_final);
                        }
                    }
                    if !text.trim().is_empty() {
                        round.last_text_at = Instant::now();
                    }
                    if round.translating {
                        round
                            .translation_targets
                            .on_source(&text, is_final, active_target, active_rev);
                    }
                }
            }
            SpeechEvent::OutputText { text, is_final } => {
                let translating = self.audio.as_ref().map(|r| r.translating).unwrap_or(false);
                if translating {
                    if !is_final {
                        return;
                    }
                    let dest_opt = self.audio.as_mut().and_then(|r| {
                        r.translation_targets
                            .pop_valid_destination(self.target, self.target_revision)
                    });
                    if let Some(dest) = dest_opt {
                        if self.is_peer_foreground(dest.peer_id) && !self.send_text_to_target(dest, true, &text) {
                            self.stop_audio(StopReason::Failed);
                            self.fail_round_immediately("输入框未能及时接收译文，已停止录音，请重新按 F8。");
                        }
                    }
                    return;
                }
                self.deliver_recognized(&text, is_final);
            }
            SpeechEvent::Failed { message } => {
                self.stop_audio(StopReason::Failed);
                self.status = ServerKind::Error;
                self.error_flag = true;
                let safe_msg = if message.trim().is_empty() {
                    GENERIC_ERROR_TEXT
                } else {
                    &message
                };
                self.broadcast_status(ServerKind::Error, safe_msg);
                self.update_ui();
            }
        }
    }

    fn deliver_recognized(&mut self, text: &str, is_final: bool) {
        self.sentence = text.to_string();
        if let Some(target) = self.target {
            if !self.is_peer_foreground(target.peer_id) {
                self.unbind(target.peer_id, Some(target.generation));
            } else {
                let sliced = slice_from_utf16_boundary(text, self.boundary_utf16);
                if self.send_text_to_target(target, is_final, &sliced) {
                    self.delivered_utf16 = self.delivered_utf16.max(utf16_len(text));
                } else if is_final {
                    self.stop_audio(StopReason::Failed);
                    self.fail_round_immediately("输入框未能及时接收定稿，已停止录音，请重新按 F8。");
                }
            }
        }
        if is_final {
            self.sentence.clear();
            self.boundary_utf16 = 0;
            self.delivered_utf16 = 0;
        }
    }

    fn check_inactivity_timeout(&mut self, round_epoch: u64) {
        let Some(ref round) = self.audio else { return };
        if round.epoch != round_epoch || !round.ready {
            return;
        }
        let elapsed = round.last_text_at.elapsed();
        if elapsed >= NO_TEXT_INACTIVITY_TIMEOUT {
            self.stop_audio(StopReason::InactivityTimeout);
        } else {
            self.schedule_inactivity_check(round_epoch, NO_TEXT_INACTIVITY_TIMEOUT - elapsed);
        }
    }

    fn handle_usage_checkpoint(&mut self, round_epoch: u64) {
        let Some(ref round) = self.audio else { return };
        if round.epoch != round_epoch || !round.ready {
            return;
        }
        if let (Some(uid), Some(ref u)) = (round.usage_session, &self.usage) {
            u.checkpoint(uid);
        }
        self.schedule_usage_checkpoint(round_epoch);
    }

    fn schedule_inactivity_check(&self, round_epoch: u64, delay: Duration) {
        let tx = self.self_tx.clone();
        tokio::spawn(async move {
            tokio::time::sleep(delay).await;
            let _ = tx
                .send(ActorMessage::CheckInactivityTimeout { round_epoch })
                .await;
        });
    }

    fn schedule_usage_checkpoint(&self, round_epoch: u64) {
        let tx = self.self_tx.clone();
        tokio::spawn(async move {
            tokio::time::sleep(USAGE_CHECKPOINT_INTERVAL).await;
            let _ = tx
                .send(ActorMessage::UsageCheckpoint { round_epoch })
                .await;
        });
    }

    fn is_peer_foreground(&self, peer_id: PeerId) -> bool {
        self.peers
            .get(&peer_id)
            .map(|p| self.verifier.is_foreground(p.client_pid, p.thread_id))
            .unwrap_or(false)
    }

    fn broadcast_status(&self, kind: ServerKind, text: &str) {
        for peer in self.peers.values() {
            self.send_status_to_peer(peer, kind, text);
        }
    }

    fn send_status_to_peer(&self, peer: &PeerRecord, kind: ServerKind, text: &str) {
        let frame = ServerFrame::status(kind, self.epoch, text);
        let _ = peer.out_tx.try_send(OutboundItem {
            frame,
            text_fence: None,
        });
    }

    fn send_text_to_target(&self, target: TargetBinding, is_final: bool, text: &str) -> bool {
        let Some(peer) = self.peers.get(&target.peer_id) else {
            return false;
        };
        let frame = ServerFrame::text_result(is_final, self.epoch, target.generation, text);
        let fence = DeliveryFence {
            epoch: self.epoch,
            target,
            target_revision: self.target_revision,
        };
        peer.out_tx.try_send(OutboundItem {
            frame,
            text_fence: Some(fence),
        }).is_ok()
    }

    fn update_ui(&self) {
        let toolbar_state = match self.status {
            ServerKind::Connecting => 1,
            ServerKind::Recording => 2,
            _ => 0,
        };
        self.toolbar
            .update(self.visible, toolbar_state, self.stopped_flag, self.error_flag);
    }

    fn sync_history_indicator(&self) {
        if let Some(ref u) = self.usage {
            let st = u.status();
            if st.updating {
                return;
            }
            self.toolbar.update_history(st.history_enabled, st.available);
        } else {
            self.toolbar.update_history(false, false);
        }
    }
}

/// 服务任意双向异步字节流（Windows 生产环境传入 `NamedPipeServer` 拆分流，单测传入 `tokio::io::DuplexStream`）。
pub async fn serve_duplex_peer<R, W>(
    mut reader: R,
    mut writer: W,
    client_pid: u32,
    daemon: SharedDaemonHandle,
) where
    R: AsyncRead + Unpin + Send + 'static,
    W: AsyncWrite + Unpin + Send + 'static,
{
    // 1. 5 秒内完成 H 握手并验证客户端进程身份
    let hello_res = tokio::time::timeout(HANDSHAKE_TIMEOUT, read_raw_frame(&mut reader)).await;
    let Ok(Ok((kind, text))) = hello_res else {
        return;
    };
    let Ok(ClientFrame {
        command: ClientCommand::Hello { thread_id },
    }) = ClientFrame::parse(kind, &text)
    else {
        return;
    };
    if !daemon.verifier.authenticate_peer(client_pid, thread_id) {
        return;
    }

    let peer_id = daemon.next_peer_id.fetch_add(1, Ordering::Relaxed);
    let (out_tx, mut out_rx) = mpsc::channel::<OutboundItem>(64);

    let (reg_ack_tx, reg_ack_rx) = oneshot::channel();
    if daemon
        .tx
        .send(ActorMessage::RegisterPeer {
            peer_id,
            client_pid,
            thread_id,
            out_tx,
            ack: reg_ack_tx,
        })
        .await
        .is_err()
        || reg_ack_rx.await.is_err()
    {
        return;
    }

    // 2. 启动写管道协程（P/F 出队写盘前二次校验三重原子栅栏 + 前台窗口资格）
    let fence_view = Arc::clone(&daemon.fence_view);
    let verifier = Arc::clone(&daemon.verifier);
    let mut write_task = tokio::spawn(async move {
        while let Some(item) = out_rx.recv().await {
            if let Some(ref fence) = item.text_fence {
                if !fence_view.verify(fence) || !verifier.is_foreground(client_pid, thread_id) {
                    continue;
                }
            }
            let Ok(bytes) = item.frame.encode() else {
                break;
            };
            if tokio::time::timeout(Duration::from_secs(2), write_raw_frame(&mut writer, &bytes))
                .await
                .map_err(|_| ())
                .and_then(|r| r.map_err(|_| ()))
                .is_err()
            {
                break;
            }
        }
    });

    // 3. 循环读取 L / B / U / T / D 命令并串行提交给 Actor
    let mut write_finished = false;
    loop {
        let received = tokio::select! {
            biased;
            _ = &mut write_task => { write_finished = true; break; }
            frame = read_raw_frame(&mut reader) => frame,
        };
        let Ok((cmd_kind, cmd_text)) = received else {
            break;
        };
        let Ok(parsed) = ClientFrame::parse(cmd_kind, &cmd_text) else {
            break;
        };
        if matches!(parsed.command, ClientCommand::Hello { .. }) {
            break;
        }
        let (ack_tx, ack_rx) = oneshot::channel();
        if daemon
            .tx
            .send(ActorMessage::PeerCommand {
                peer_id,
                command: parsed.command,
                ack: ack_tx,
            })
            .await
            .is_err()
            || ack_rx.await.is_err()
        {
            break;
        }
    }

    // 4. 客户端断开：解绑并清理该 Peer（不影响正在其他目标上进行的录音）
    let (unreg_tx, unreg_rx) = oneshot::channel();
    let _ = daemon
        .tx
        .send(ActorMessage::UnregisterPeer {
            peer_id,
            ack: unreg_tx,
        })
        .await;
    let _ = unreg_rx.await;
    if !write_finished {
        // Reader EOF means the target is gone; do not keep its writer/safety slot alive.
        write_task.abort();
        let _ = write_task.await;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Mutex;
    use voice_ime_config::{ConfigStore, SpeechSubscription, StandaloneConfig};
    use voice_ime_win32::NoopToolbarDriver;

    use crate::launcher::NoopToolsLauncher;

    struct TestVerifier {
        foreground_pid: Mutex<u32>,
    }

    impl PipePeerVerifier for TestVerifier {
        fn authenticate_peer(&self, client_pid: u32, declared_thread_id: u32) -> bool {
            client_pid > 0 && declared_thread_id > 0
        }
        fn is_foreground(&self, client_pid: u32, _declared_thread_id: u32) -> bool {
            *self.foreground_pid.lock().unwrap() == client_pid
        }
    }

    struct FakeSpeechEngine {
        events_out: Mutex<Option<mpsc::UnboundedSender<SpeechEvent>>>,
    }

    impl SpeechEngineFactory for FakeSpeechEngine {
        fn prewarm(
            &self,
            _cfg: voice_ime_config::RuntimeSpeechSessionConfig,
        ) -> tokio::task::JoinHandle<Option<PrewarmedSpeechHandle>> {
            tokio::spawn(async { None })
        }

        fn start_session(
            &self,
            _cfg: voice_ime_config::RuntimeSpeechSessionConfig,
            _prewarmed: Option<PrewarmedSpeechHandle>,
            events_tx: mpsc::UnboundedSender<SpeechEvent>,
        ) -> SpeechSessionHandle {
            *self.events_out.lock().unwrap() = Some(events_tx.clone());
            tokio::spawn(async move {
                let _ = events_tx.send(SpeechEvent::Ready);
            });
            let (stop_tx, stop_rx) = oneshot::channel();
            let (rel_tx, rel_rx) = oneshot::channel();
            tokio::spawn(async move {
                let _ = stop_rx.await;
                let _ = rel_tx.send(());
            });
            SpeechSessionHandle::from_channels(stop_tx, rel_rx)
        }
    }

    async fn read_decoded(reader: &mut (impl AsyncRead + Unpin)) -> ServerFrame {
        let (k, payload) = read_raw_frame(reader).await.unwrap();
        ServerFrame::decode_payload(k, &payload).unwrap()
    }

    #[tokio::test]
    async fn actor_enforces_cross_window_utf16_boundary_and_epoch_fences() {
        let dir = std::env::temp_dir().join(format!("voice_ime_actor_{}", Uuid::new_v4()));
        std::fs::create_dir_all(&dir).unwrap();
        let cfg_path = dir.join("config.json");
        let store = ConfigStore::new(&cfg_path);
        let mut cfg = StandaloneConfig::default();
        cfg.enable_prewarm = false;
        let sub = SpeechSubscription::create(
            "21V 测试",
            "https://chinanorth3.api.cognitive.azure.cn/sts/v1.0/issuetoken",
            "0123456789abcdef0123456789abcdef",
        )
        .unwrap();
        cfg.active_subscription_id = sub.id.clone();
        cfg.subscriptions.push(sub);
        store.save(&cfg).unwrap();

        let verifier = Arc::new(TestVerifier {
            foreground_pid: Mutex::new(101),
        });
        let fake_engine = Arc::new(FakeSpeechEngine {
            events_out: Mutex::new(None),
        });
        let (_tb_tx, tb_rx) = mpsc::unbounded_channel();

        let (handle, actor_join) = SharedDaemonActor::spawn(
            Arc::new(RuntimeConfigCache::new(store)),
            Arc::clone(&fake_engine) as Arc<dyn SpeechEngineFactory>,
            Arc::clone(&verifier) as Arc<dyn PipePeerVerifier>,
            Arc::new(NoopToolbarDriver),
            Arc::new(NoopToolsLauncher),
            None,
            MaintenanceGate::new(dir.join("maintenance.lock")),
            None,
            tb_rx,
        );

        // 连接窗口 A (PID=101) 与窗口 B (PID=202)
        let (mut a_client, a_server) = tokio::io::duplex(16384);
        let (a_sr, a_sw) = tokio::io::split(a_server);
        let h_a = handle.clone();
        tokio::spawn(async move { serve_duplex_peer(a_sr, a_sw, 101, h_a).await });

        let (mut b_client, b_server) = tokio::io::duplex(16384);
        let (b_sr, b_sw) = tokio::io::split(b_server);
        let h_b = handle.clone();
        tokio::spawn(async move { serve_duplex_peer(b_sr, b_sw, 202, h_b).await });

        // 握手 H
        write_raw_frame(&mut a_client, &ClientFrame { command: ClientCommand::Hello { thread_id: 1 } }.encode().unwrap()).await.unwrap();
        write_raw_frame(&mut b_client, &ClientFrame { command: ClientCommand::Hello { thread_id: 2 } }.encode().unwrap()).await.unwrap();
        assert_eq!(read_decoded(&mut a_client).await.kind, ServerKind::Idle);
        assert_eq!(read_decoded(&mut b_client).await.kind, ServerKind::Idle);

        // 窗口 A 绑定目标 B(gen=5) 并按 F8 Toggle(gen=5)
        write_raw_frame(&mut a_client, &ClientFrame { command: ClientCommand::BindTarget { generation: 5 } }.encode().unwrap()).await.unwrap();
        write_raw_frame(&mut a_client, &ClientFrame { command: ClientCommand::Toggle { generation: 5 } }.encode().unwrap()).await.unwrap();

        assert_eq!(read_decoded(&mut a_client).await.kind, ServerKind::Connecting);
        assert_eq!(read_decoded(&mut a_client).await.kind, ServerKind::Recording);
        // 窗口 B 也同步收到 C 与 R 状态，但不会收到文字
        assert_eq!(read_decoded(&mut b_client).await.kind, ServerKind::Connecting);
        assert_eq!(read_decoded(&mut b_client).await.kind, ServerKind::Recording);

        // 发送第一段临时识别结果 "你好😀" 到窗口 A
        let ev_tx = fake_engine.events_out.lock().unwrap().clone().unwrap();
        ev_tx.send(SpeechEvent::OutputText { text: "你好😀".into(), is_final: false }).unwrap();
        let p_a = read_decoded(&mut a_client).await;
        assert_eq!(p_a.kind, ServerKind::Partial);
        assert_eq!(p_a.target_generation, 5);
        assert_eq!(p_a.text, "你好😀");

        // 用户中途点击切换到窗口 B (PID=202)，窗口 B 发送 BindTarget(gen=9)
        *verifier.foreground_pid.lock().unwrap() = 202;
        write_raw_frame(&mut b_client, &ClientFrame { command: ClientCommand::BindTarget { generation: 9 } }.encode().unwrap()).await.unwrap();
        tokio::time::sleep(Duration::from_millis(30)).await;

        // 同一句话定稿为 "你好😀世界"，窗口 B 必须只收到冻结边界后的后缀 "世界"！
        ev_tx.send(SpeechEvent::OutputText { text: "你好😀世界".into(), is_final: true }).unwrap();
        let f_b = read_decoded(&mut b_client).await;
        assert_eq!(f_b.kind, ServerKind::Final);
        assert_eq!(f_b.target_generation, 9);
        assert_eq!(f_b.text, "世界");

        // A becomes the foreground selection but never binds a new editor. Its EOF
        // must not leave the old background target B recording or the toolbar visible.
        *verifier.foreground_pid.lock().unwrap() = 101;
        write_raw_frame(&mut a_client, &ClientFrame { command: ClientCommand::Selected { generation: 6 } }.encode().unwrap()).await.unwrap();
        drop(a_client);
        let stopped = tokio::time::timeout(Duration::from_secs(2), read_decoded(&mut b_client))
            .await.unwrap();
        assert_eq!(stopped.kind, ServerKind::Stopped);

        handle.shutdown().await;
        actor_join.await.unwrap();
        let _ = std::fs::remove_dir_all(&dir);
    }
}
