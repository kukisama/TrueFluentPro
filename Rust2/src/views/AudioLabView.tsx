import { useState, useEffect, useCallback } from "react";
import { useTranslation } from "react-i18next";
import {
  FileAudio, FolderOpen, RefreshCw, Play, Pause,
  SkipForward, SkipBack, ChevronRight, Loader2,
  Brain, Sparkles, FileText, Podcast, Globe,
  Network, Music, Lightbulb, Trash2,
  CheckCircle2, Clock, AlertCircle, Timer,
  Volume2,
} from "lucide-react";
import { cn } from "../lib/utils";
import {
  Button, GlassCard, Select, Badge, FadeIn,
  EmptyState, ScrollArea, Separator,
} from "../components/ui";
import { api } from "../lib/tauri-api";
import type { AudioLibraryItem, AudioLifecycle, LifecycleStage, StageStatus } from "../lib/tauri-api";

/* ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
   听析中心 — 8 阶段生命周期音频分析
   对标 C# AudioLabView / AudioProcessingSnapshot
   ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━ */

const STAGES: { key: LifecycleStage; icon: React.ReactNode; label: string }[] = [
  { key: "Transcribed",    icon: <FileText size={16} />,   label: "转录" },
  { key: "Summarized",     icon: <Brain size={16} />,      label: "总结" },
  { key: "MindMap",        icon: <Network size={16} />,    label: "思维导图" },
  { key: "Insight",        icon: <Lightbulb size={16} />,  label: "顿悟" },
  { key: "Research",       icon: <Sparkles size={16} />,   label: "深度研究" },
  { key: "PodcastScript",  icon: <Podcast size={16} />,    label: "播客台本" },
  { key: "PodcastAudio",   icon: <Music size={16} />,      label: "播客音频" },
  { key: "Translated",     icon: <Globe size={16} />,      label: "翻译" },
];

const STATUS_ICONS: Record<StageStatus, React.ReactNode> = {
  Pending:   <Clock size={14} className="text-[var(--text-muted)]" />,
  Running:   <Loader2 size={14} className="text-blue-400 animate-spin" />,
  Completed: <CheckCircle2 size={14} className="text-emerald-400" />,
  Failed:    <AlertCircle size={14} className="text-red-400" />,
  Stale:     <Timer size={14} className="text-amber-400" />,
};

export function AudioLabView() {
  const { t: _t } = useTranslation();

  const [audioItems, setAudioItems] = useState<AudioLibraryItem[]>([]);
  const [selectedItemId, setSelectedItemId] = useState<string | null>(null);
  const [lifecycle, setLifecycle] = useState<AudioLifecycle[]>([]);
  const [activeStage, setActiveStage] = useState<LifecycleStage>("Transcribed");
  const [, setLoading] = useState(false);
  const [isPlaying, setIsPlaying] = useState(false);
  const [playbackSpeed, setPlaybackSpeed] = useState(1.0);

  const selectedItem = audioItems.find((a) => a.id === selectedItemId);
  const activeLifecycle = lifecycle.find((lc) => lc.stage === activeStage);

  useEffect(() => { loadAudioItems(); }, []);

  const loadAudioItems = useCallback(async () => {
    try {
      const items = await api.listAudioItems();
      setAudioItems(items);
    } catch (err) {
      console.error("Failed to load audio items:", err);
    }
  }, []);

  const loadLifecycle = useCallback(async (audioItemId: string) => {
    try {
      setLoading(true);
      const lc = await api.getAudioLifecycle(audioItemId);
      setLifecycle(lc);
    } catch (err) {
      console.error("Failed to load lifecycle:", err);
    } finally {
      setLoading(false);
    }
  }, []);

  const handleSelectItem = useCallback(async (itemId: string) => {
    setSelectedItemId(itemId);
    setActiveStage("Transcribed");
    await loadLifecycle(itemId);
  }, [loadLifecycle]);

  const handleOpenFile = useCallback(async () => {
    try {
      const item = await api.addAudioItem({
        file_name: "recording.wav",
        file_path: "",
        duration_ms: 0,
        sample_rate: 16000,
        channels: 1,
        source_lang: "zh-CN",
      } as any);
      setAudioItems((prev) => [item, ...prev]);
      handleSelectItem(item.id);
    } catch (err) {
      console.error("Failed to add audio item:", err);
    }
  }, [handleSelectItem]);

  const handleDeleteItem = useCallback(async (itemId: string) => {
    try {
      await api.deleteAudioItem(itemId);
      setAudioItems((prev) => prev.filter((a) => a.id !== itemId));
      if (selectedItemId === itemId) {
        setSelectedItemId(null);
        setLifecycle([]);
      }
    } catch (err) {
      console.error("Failed to delete:", err);
    }
  }, [selectedItemId]);

  const handleSubmitStage = useCallback(async (stage: LifecycleStage) => {
    if (!selectedItemId) return;
    try {
      await api.submitTask({
        audio_item_id: selectedItemId,
        stage,
        task_type: stage === "Transcribed" ? "Transcription" : stage === "PodcastAudio" ? "TTS" : "AiCompletion",
        status: "Queued",
        priority: 0,
        retry_count: 0,
        max_retries: 3,
        progress: 0,
      } as any);
      await loadLifecycle(selectedItemId);
    } catch (err) {
      console.error("Failed to submit task:", err);
    }
  }, [selectedItemId, loadLifecycle]);

  const handleSubmitAll = useCallback(async () => {
    if (!selectedItemId) return;
    for (const stage of STAGES) {
      const lc = lifecycle.find((l) => l.stage === stage.key);
      if (!lc || lc.status === "Pending" || lc.status === "Failed" || lc.status === "Stale") {
        await handleSubmitStage(stage.key);
      }
    }
  }, [selectedItemId, lifecycle, handleSubmitStage]);

  return (
    <div className="flex h-full">
      {/* 左侧: 音频文件列表 */}
      <div className="w-[240px] border-r border-[var(--border-subtle)] flex flex-col shrink-0"
        style={{ backgroundColor: "var(--sidebar-bg)" }}>
        <div className="p-3 border-b border-[var(--border-subtle)] flex items-center gap-2">
          <h2 className="text-sm font-semibold text-[var(--text-primary)] flex-1">音频库</h2>
          <Button variant="ghost" size="icon" className="h-7 w-7" onClick={handleOpenFile} title="打开文件">
            <FolderOpen size={14} />
          </Button>
          <Button variant="ghost" size="icon" className="h-7 w-7" onClick={loadAudioItems} title="刷新">
            <RefreshCw size={14} />
          </Button>
        </div>
        <ScrollArea className="flex-1">
          <div className="p-2 space-y-1">
            {audioItems.length === 0 ? (
              <div className="p-4 text-center">
                <FileAudio size={32} className="text-[var(--text-muted)] mx-auto mb-2" />
                <p className="text-xs text-[var(--text-muted)]">暂无文件</p>
                <p className="text-[10px] text-[var(--text-placeholder)] mt-1">打开或拖入音频文件</p>
              </div>
            ) : audioItems.map((item) => (
              <button key={item.id} onClick={() => handleSelectItem(item.id)}
                className={cn(
                  "w-full text-left px-3 py-2 rounded-lg transition-all group text-xs",
                  selectedItemId === item.id
                    ? "bg-brand-600/15 text-[var(--active-text)]"
                    : "text-[var(--text-secondary)] hover:bg-[var(--hover-bg)]"
                )}>
                <div className="flex items-center gap-2">
                  <FileAudio size={14} className="shrink-0" />
                  <span className="truncate flex-1">{item.file_name}</span>
                  <button onClick={(e) => { e.stopPropagation(); handleDeleteItem(item.id); }}
                    className="opacity-0 group-hover:opacity-100 transition-opacity">
                    <Trash2 size={12} className="text-red-400" />
                  </button>
                </div>
                <div className="flex items-center gap-2 mt-0.5 ml-5 text-[10px] text-[var(--text-muted)]">
                  <span>{formatDuration(item.duration_ms)}</span>
                  <span>{item.source_lang}</span>
                </div>
              </button>
            ))}
          </div>
        </ScrollArea>
      </div>

      {/* 中央: 主内容区 */}
      <div className="flex-1 flex flex-col min-w-0">
        {selectedItem ? (
          <>
            {/* 播放控件 */}
            <div className="px-5 py-3 border-b border-[var(--border-subtle)] flex items-center gap-4"
              style={{ backgroundColor: "var(--toolbar-bg)" }}>
              <div className="flex items-center gap-2">
                <Button variant="ghost" size="icon" className="h-8 w-8"><SkipBack size={14} /></Button>
                <Button variant="secondary" size="icon" className="h-9 w-9" onClick={() => setIsPlaying(!isPlaying)}>
                  {isPlaying ? <Pause size={16} /> : <Play size={16} />}
                </Button>
                <Button variant="ghost" size="icon" className="h-8 w-8"><SkipForward size={14} /></Button>
              </div>
              <div className="flex-1 h-1.5 bg-[var(--surface-2)] rounded-full">
                <div className="h-full w-0 bg-brand-500 rounded-full" />
              </div>
              <Volume2 size={14} className="text-[var(--text-muted)]" />
              <Select className="w-16 h-7 text-xs" value={playbackSpeed.toString()} onChange={(e) => setPlaybackSpeed(Number(e.target.value))}>
                {[0.5, 0.75, 1.0, 1.25, 1.5, 2.0].map((s) => <option key={s} value={s}>{s}x</option>)}
              </Select>
              <span className="text-xs text-[var(--text-muted)]">{selectedItem.file_name} · {formatDuration(selectedItem.duration_ms)}</span>
            </div>

            {/* 8阶段管道 */}
            <div className="px-5 py-3 border-b border-[var(--border-subtle)] flex items-center gap-1 overflow-x-auto"
              style={{ backgroundColor: "var(--toolbar-bg)" }}>
              {STAGES.map((stage, i) => {
                const lc = lifecycle.find((l) => l.stage === stage.key);
                const status: StageStatus = (lc?.status as StageStatus) || "Pending";
                return (
                  <div key={stage.key} className="flex items-center">
                    <button onClick={() => setActiveStage(stage.key)}
                      className={cn(
                        "flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-xs font-medium transition-all whitespace-nowrap",
                        activeStage === stage.key
                          ? "bg-brand-600/15 text-[var(--active-text)] shadow-sm"
                          : "text-[var(--text-muted)] hover:bg-[var(--hover-bg)]"
                      )}>
                      {STATUS_ICONS[status]}
                      {stage.icon}
                      <span>{stage.label}</span>
                    </button>
                    {i < STAGES.length - 1 && <ChevronRight size={12} className="text-[var(--text-placeholder)] mx-0.5" />}
                  </div>
                );
              })}
              <div className="flex-1" />
              <Button size="sm" variant="secondary" onClick={handleSubmitAll}>
                <Sparkles size={12} /> 全部生成
              </Button>
            </div>

            {/* 阶段内容 */}
            <ScrollArea className="flex-1">
              <div className="p-6">
                <FadeIn key={activeStage}>
                  <StageContent stage={activeStage} lifecycle={activeLifecycle} onSubmit={() => handleSubmitStage(activeStage)} />
                </FadeIn>
              </div>
            </ScrollArea>
          </>
        ) : (
          <div className="flex-1 flex items-center justify-center">
            <EmptyState
              icon={<FileAudio size={48} />}
              title="未选择文件"
              description="打开或拖入音频文件开始分析"
              action={<Button onClick={handleOpenFile}><FolderOpen size={14} /> 打开文件</Button>}
            />
          </div>
        )}
      </div>
    </div>
  );
}

function StageContent({ stage, lifecycle, onSubmit }: { stage: LifecycleStage; lifecycle?: AudioLifecycle; onSubmit: () => void }) {
  const status: StageStatus = (lifecycle?.status as StageStatus) || "Pending";
  const stageInfo = STAGES.find((s) => s.key === stage);

  return (
    <div className="max-w-4xl space-y-4">
      <div className="flex items-center gap-3">
        <div className={cn(
          "w-10 h-10 rounded-xl flex items-center justify-center",
          status === "Completed" ? "bg-emerald-500/15 text-emerald-400"
          : status === "Running" ? "bg-blue-500/15 text-blue-400"
          : status === "Failed" ? "bg-red-500/15 text-red-400"
          : "bg-[var(--surface-2)] text-[var(--text-muted)]"
        )}>{stageInfo?.icon}</div>
        <div className="flex-1">
          <h2 className="text-lg font-semibold text-[var(--text-primary)]">{stageInfo?.label}</h2>
          <div className="flex items-center gap-2 mt-0.5">
            <Badge variant={status === "Completed" ? "green" : status === "Running" ? "blue" : status === "Failed" ? "red" : "default"}>
              {status === "Completed" ? "已完成" : status === "Running" ? "处理中" : status === "Failed" ? "失败" : status === "Stale" ? "已过期" : "待处理"}
            </Badge>
            {lifecycle?.model_id && <span className="text-[10px] text-[var(--text-muted)]">模型: {lifecycle.model_id}</span>}
            {lifecycle?.token_used && <span className="text-[10px] text-[var(--text-muted)]">{lifecycle.token_used} tokens</span>}
          </div>
        </div>
        {(status === "Pending" || status === "Failed" || status === "Stale") && (
          <Button onClick={onSubmit}><Sparkles size={14} /> 生成</Button>
        )}
        {status === "Running" && (
          <Button variant="secondary" disabled><Loader2 size={14} className="animate-spin" /> 处理中</Button>
        )}
      </div>

      <Separator />

      {status === "Completed" && lifecycle?.result_text ? (
        <GlassCard className="p-5">
          {stage === "PodcastAudio" ? (
            <div className="flex items-center gap-4 p-4">
              <Button variant="secondary" size="icon" className="h-12 w-12"><Play size={20} /></Button>
              <div className="flex-1 h-2 bg-[var(--surface-2)] rounded-full"><div className="h-full w-0 bg-brand-500 rounded-full" /></div>
              <span className="text-xs text-[var(--text-muted)]">0:00</span>
            </div>
          ) : (
            <pre className="text-sm text-[var(--text-primary)] whitespace-pre-wrap leading-relaxed">{lifecycle.result_text}</pre>
          )}
        </GlassCard>
      ) : status === "Running" ? (
        <GlassCard className="p-8 flex flex-col items-center gap-3">
          <Loader2 size={32} className="text-brand-400 animate-spin" />
          <p className="text-sm text-[var(--text-muted)]">正在处理 {stageInfo?.label}...</p>
        </GlassCard>
      ) : status === "Failed" ? (
        <GlassCard className="p-5 border-red-500/30">
          <div className="flex items-start gap-3">
            <AlertCircle size={20} className="text-red-400 shrink-0 mt-0.5" />
            <div>
              <p className="text-sm text-red-400 font-medium">处理失败</p>
              <p className="text-xs text-[var(--text-muted)] mt-1">{lifecycle?.error || "未知错误"}</p>
            </div>
          </div>
        </GlassCard>
      ) : (
        <GlassCard className="p-8 flex flex-col items-center gap-3">
          {stageInfo?.icon}
          <p className="text-sm text-[var(--text-muted)]">点击「生成」按钮开始 {stageInfo?.label}</p>
        </GlassCard>
      )}
    </div>
  );
}

function formatDuration(ms: number): string {
  if (ms <= 0) return "0:00";
  const totalSec = Math.floor(ms / 1000);
  const min = Math.floor(totalSec / 60);
  const sec = totalSec % 60;
  return `${min}:${sec.toString().padStart(2, "0")}`;
}
