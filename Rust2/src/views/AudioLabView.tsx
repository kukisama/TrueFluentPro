import { useState, useEffect, useCallback, useRef, useMemo } from "react";
import { convertFileSrc } from "@tauri-apps/api/core";
import { open as dialogOpen } from "@tauri-apps/plugin-dialog";
import {
  FileAudio, FolderOpen, Plus, RefreshCw,
  Play, Pause, SkipBack, SkipForward,
  ChevronLeft, ChevronRight,
  Sparkles, FileText, Trash2, X,
  Loader2, Edit3, Save, Copy,
  Tag, Search, ArrowUpDown, Mic,
  Settings2, GripVertical,
} from "lucide-react";
import { MindMapCanvas } from "../components/MindMapCanvas";
import { cn } from "../lib/utils";
import {
  Button, GlassCard, Badge, FadeIn,
  EmptyState, ScrollArea, Separator,
} from "../components/ui";
import { api } from "../lib/tauri-api";
import type {
  AudioFile, AudioSegment, AudioStagePreset,
  AudioLabBundle, AudioLabTabKind,
  StudioTask,
} from "../lib/tauri-api";
import { useAudioLabStore, speakerColor } from "../stores/audiolab-store";

/* ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
   听析中心 — 完全复刻 C# AudioLabView
   PR-2: 文件库 + 转录 Tab + 播放器
   PR-3: 6 阶段 Tab + AutoTags
   PR-4: Custom Tab + StagePresets 编辑器 + 段落编辑 + 导出
   ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━ */

const TABS: { key: AudioLabTabKind; label: string; emoji: string }[] = [
  { key: "Summary",     label: "总结",     emoji: "📋" },
  { key: "Transcript",  label: "转录",     emoji: "📝" },
  { key: "MindMap",     label: "导图",     emoji: "🧠" },
  { key: "Insight",     label: "顿悟",     emoji: "💡" },
  { key: "Research",    label: "研究",     emoji: "🔬" },
  { key: "Podcast",     label: "播客",     emoji: "🎙" },
  { key: "Translation", label: "翻译",     emoji: "🌐" },
  { key: "Custom",      label: "自定义",   emoji: "⚡" },
];

export function AudioLabView() {
  const store = useAudioLabStore();
  const audioRef = useRef<HTMLAudioElement>(null);
  const [dragOver, setDragOver] = useState(false);
  const [searchQuery, setSearchQuery] = useState("");
  const [sortMode, setSortMode] = useState<string>("time");
  const [showPresetsEditor, setShowPresetsEditor] = useState(false);

  // mount：加载文件库 + 阶段预设 + 订阅事件
  useEffect(() => {
    store.loadFiles();
    store.loadStagePresets();

    let unsubTask: (() => void) | null = null;
    let unsubDelta: (() => void) | null = null;

    api.onAudiolabTaskUpdate(() => {
      const sid = useAudioLabStore.getState().activeSessionId;
      if (sid) {
        store.loadRunningTasks(sid);
        store.refreshBundle(sid);
      }
    }).then((fn) => { unsubTask = fn; });

    api.onAudiolabStageDelta(() => {
      const sid = useAudioLabStore.getState().activeSessionId;
      if (sid) store.refreshBundle(sid);
    }).then((fn) => { unsubDelta = fn; });

    return () => { unsubTask?.(); unsubDelta?.(); };
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  // mount 时恢复活跃 session
  useEffect(() => {
    const sid = store.activeSessionId;
    if (sid) { store.refreshBundle(sid); store.loadRunningTasks(sid); }
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  // 播放位置同步
  useEffect(() => {
    const audio = audioRef.current;
    if (!audio) return;
    const onTime = () => store.updatePlayback({ positionMs: Math.floor(audio.currentTime * 1000) });
    const onEnd = () => store.updatePlayback({ isPlaying: false });
    audio.addEventListener("timeupdate", onTime);
    audio.addEventListener("ended", onEnd);
    return () => { audio.removeEventListener("timeupdate", onTime); audio.removeEventListener("ended", onEnd); };
  }, [store.playback.playbackPath]); // eslint-disable-line react-hooks/exhaustive-deps

  // 切换音频源
  useEffect(() => {
    const audio = audioRef.current;
    if (!audio || !store.playback.playbackPath) return;
    audio.src = convertFileSrc(store.playback.playbackPath);
    audio.playbackRate = store.playback.speed;
  }, [store.playback.playbackPath]); // eslint-disable-line react-hooks/exhaustive-deps

  // ── 文件操作 ──
  const handleImport = useCallback(async () => {
    const selected = await dialogOpen({
      multiple: true,
      filters: [{ name: "音频文件", extensions: ["wav", "mp3", "flac", "ogg", "m4a", "aac", "wma"] }],
    });
    if (!selected) return;
    const paths = Array.isArray(selected) ? selected : [selected];
    try { await api.audiolabImportFiles(paths as string[]); store.loadFiles(); }
    catch (err) { console.error("导入失败:", err); }
  }, [store]);

  const handleDrop = useCallback(async (e: React.DragEvent) => {
    e.preventDefault(); setDragOver(false);
    const paths = Array.from(e.dataTransfer.files)
      .filter((f) => /\.(wav|mp3|flac|ogg|m4a|aac|wma)$/i.test(f.name))
      .map((f) => (f as any).path as string).filter(Boolean);
    if (!paths.length) return;
    try { await api.audiolabImportFiles(paths); store.loadFiles(); }
    catch (err) { console.error("拖放导入失败:", err); }
  }, [store]);

  const handleDeleteFile = useCallback(async (fileId: string, deleteSource: boolean) => {
    try { await api.audiolabRemoveFile(fileId, deleteSource); store.loadFiles(); }
    catch (err) { console.error("删除失败:", err); }
  }, [store]);

  // ── 播放控制 ──
  const togglePlay = useCallback(() => {
    const audio = audioRef.current; if (!audio) return;
    if (store.playback.isPlaying) { audio.pause(); store.updatePlayback({ isPlaying: false }); }
    else { audio.play(); store.updatePlayback({ isPlaying: true }); }
  }, [store]);

  const setSpeed = useCallback((speed: number) => {
    const audio = audioRef.current; if (audio) audio.playbackRate = speed;
    store.updatePlayback({ speed });
  }, [store]);

  const seekToMs = useCallback((ms: number) => {
    const audio = audioRef.current;
    if (audio) { audio.currentTime = ms / 1000; store.updatePlayback({ positionMs: ms }); }
  }, [store]);

  // ── 当前段 ──
  const bundle = store.currentBundle();
  const currentSegmentId = useMemo(() => {
    if (!bundle?.segments?.length) return null;
    const pos = store.playback.positionMs;
    return bundle.segments.find((s) => s.start_ms <= pos && s.end_ms > pos)?.id ?? null;
  }, [bundle?.segments, store.playback.positionMs]);

  // ── 文件过滤/排序 ──
  const filteredFiles = useMemo(() => {
    let list = store.files;
    if (searchQuery) { const q = searchQuery.toLowerCase(); list = list.filter((f) => f.display_name.toLowerCase().includes(q)); }
    if (sortMode === "duration") list = [...list].sort((a, b) => b.duration_ms - a.duration_ms);
    else if (sortMode === "name") list = [...list].sort((a, b) => a.display_name.localeCompare(b.display_name));
    return list;
  }, [store.files, searchQuery, sortMode]);

  const customVisibleStages = useMemo(() =>
    store.stagePresets.filter((p) => p.is_enabled && p.show_in_tab && !TABS.some((t) => t.key === p.stage)),
    [store.stagePresets]);

  return (
    <div className="flex h-full">
      <audio ref={audioRef} style={{ display: "none" }} />

      {/* ═══ 左侧文件面板（对齐 C# FilePanelRail） ═══ */}
      <div
        className={cn(
          "border-r border-[var(--border-subtle)] flex flex-col shrink-0 transition-all",
          store.isFilePanelOpen ? "w-[280px]" : "w-[52px]",
          dragOver && "ring-2 ring-brand-500/50 ring-inset",
        )}
        style={{ backgroundColor: "var(--sidebar-bg)" }}
        onDragOver={(e) => { e.preventDefault(); setDragOver(true); }}
        onDragLeave={() => setDragOver(false)}
        onDrop={handleDrop}
      >
        <div className="p-2 border-b border-[var(--border-subtle)]">
          <Button variant="ghost" size="icon" className="h-8 w-8"
            onClick={() => store.setFilePanelOpen(!store.isFilePanelOpen)}
            title={store.isFilePanelOpen ? "收起" : "展开"}>
            {store.isFilePanelOpen ? <ChevronLeft size={14} /> : <ChevronRight size={14} />}
          </Button>
        </div>

        {store.isFilePanelOpen && (
          <>
            <div className="p-3 border-b border-[var(--border-subtle)] flex items-center gap-2">
              <FileAudio size={14} className="text-[var(--text-muted)] shrink-0" />
              <h2 className="text-sm font-semibold text-[var(--text-primary)] flex-1">音频列表</h2>
              <Button variant="ghost" size="icon" className="h-7 w-7" onClick={() => store.loadFiles()} title="刷新">
                <RefreshCw size={12} />
              </Button>
            </div>

            <div className="p-2">
              <Button variant="secondary" className="w-full justify-center text-xs" onClick={handleImport}>
                <FolderOpen size={12} /> 从文件加载
              </Button>
            </div>

            <div className="px-2 pb-2 flex gap-1">
              <div className="flex-1 relative">
                <Search size={12} className="absolute left-2 top-1/2 -translate-y-1/2 text-[var(--text-muted)]" />
                <input className="w-full h-7 pl-7 pr-2 text-xs rounded bg-[var(--surface-1)] border border-[var(--border-subtle)] text-[var(--text-primary)] placeholder:text-[var(--text-placeholder)]"
                  placeholder="搜索..." value={searchQuery} onChange={(e) => setSearchQuery(e.target.value)} />
              </div>
              <Button variant="ghost" size="icon" className="h-7 w-7 shrink-0"
                onClick={() => setSortMode(sortMode === "time" ? "duration" : sortMode === "duration" ? "name" : "time")}
                title={`排序: ${sortMode === "time" ? "时间" : sortMode === "duration" ? "时长" : "名称"}`}>
                <ArrowUpDown size={12} />
              </Button>
            </div>

            <ScrollArea className="flex-1">
              <div className="p-2 space-y-1">
                {filteredFiles.length === 0 ? (
                  <div className="p-4 text-center">
                    <FileAudio size={32} className="text-[var(--text-muted)] mx-auto mb-2" />
                    <p className="text-xs text-[var(--text-muted)]">暂无文件</p>
                    <p className="text-[10px] text-[var(--text-placeholder)] mt-1">拖拽音频文件到此处</p>
                  </div>
                ) : filteredFiles.map((file) => (
                  <FileItem key={file.id} file={file}
                    isActive={file.id === store.activeFileId}
                    onSelect={() => {
                      if (file.session_id) store.selectFile(file.id, file.session_id);
                    }}
                    onDelete={(ds) => handleDeleteFile(file.id, ds)}
                    hasRunningTask={store.runningTasks.some((t) => t.session_id === file.id)} />
                ))}
              </div>
            </ScrollArea>
          </>
        )}
      </div>

      {/* ═══ 主内容区 ═══ */}
      <div className="flex-1 flex flex-col min-w-0">
        {store.activeFileId && bundle ? (
          <>
            {/* 播放器 */}
            <PlaybackBar playback={store.playback} onTogglePlay={togglePlay} onSetSpeed={setSpeed} onSeek={seekToMs} />

            {/* AutoTags */}
            {bundle.auto_tags.length > 0 && (
              <div className="px-4 py-2 border-b border-[var(--border-subtle)] flex flex-wrap gap-1.5 items-center">
                <Tag size={12} className="text-[var(--text-muted)] mr-1" />
                {bundle.auto_tags.map((t) => (
                  <span key={t.id} className="inline-flex items-center gap-1 px-2.5 py-0.5 rounded-full text-[11px] bg-brand-600/10 text-brand-400">
                    {t.tag}
                    <button onClick={() => api.audiolabRemoveAutoTag(t.id).then(() => store.refreshBundle(store.activeSessionId!))} className="hover:text-red-400"><X size={10} /></button>
                  </span>
                ))}
              </div>
            )}

            {/* Tab 栏 */}
            <div className="px-4 py-2 border-b border-[var(--border-subtle)] flex items-center gap-1 overflow-x-auto" style={{ backgroundColor: "var(--toolbar-bg)" }}>
              {TABS.map((tab) => (
                <button key={tab.key} onClick={() => store.setSelectedTab(tab.key)}
                  className={cn("flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-xs font-medium transition-all whitespace-nowrap",
                    store.selectedTab === tab.key ? "bg-brand-600/15 text-[var(--active-text)] shadow-sm" : "text-[var(--text-muted)] hover:bg-[var(--hover-bg)]")}>
                  <span>{tab.emoji}</span><span>{tab.label}</span>
                </button>
              ))}
              {customVisibleStages.map((preset) => (
                <button key={preset.stage}
                  onClick={() => { store.setCustomStageKey(preset.stage); store.setSelectedTab("Custom"); }}
                  className={cn("flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-xs font-medium transition-all whitespace-nowrap",
                    store.selectedTab === "Custom" && store.customStageKey === preset.stage
                      ? "bg-brand-600/15 text-[var(--active-text)] shadow-sm" : "text-[var(--text-muted)] hover:bg-[var(--hover-bg)]")}>
                  <span>⚡</span><span>{preset.display_name}</span>
                </button>
              ))}
              <Separator orientation="vertical" className="h-5 mx-2" />
              <Button variant="ghost" size="sm" className="text-xs" onClick={handleImport}><FolderOpen size={12} /> 打开文件</Button>
              <Button variant="ghost" size="sm" className="text-xs" onClick={() => setShowPresetsEditor(true)}><Settings2 size={12} /></Button>
            </div>

            {/* Tab 内容 */}
            <ScrollArea className="flex-1">
              <div className="p-5">
                <FadeIn key={store.selectedTab + (store.customStageKey ?? "")}>
                  <TabContent tab={store.selectedTab} bundle={bundle} sessionId={store.activeSessionId!}
                    currentSegmentId={currentSegmentId} playbackPositionMs={store.playback.positionMs}
                    onSeekToSegment={seekToMs} runningTasks={store.runningTasks}
                    editingStageKey={store.editingStageKey} editText={store.editText}
                    onSetEditing={store.setEditingStage} customStageKey={store.customStageKey}
                    stagePresets={store.stagePresets} onRefresh={() => store.refreshBundle(store.activeSessionId!)} />
                </FadeIn>
              </div>
            </ScrollArea>

            {/* 底部状态栏 */}
            {store.runningTasks.length > 0 && (
              <div className="px-4 py-2 border-t border-[var(--border-subtle)] flex items-center gap-3" style={{ backgroundColor: "var(--toolbar-bg)" }}>
                <Loader2 size={14} className="text-brand-400 animate-spin" />
                <span className="text-xs text-[var(--text-muted)] flex-1">{store.runningTasks.length} 个任务处理中...</span>
              </div>
            )}
          </>
        ) : (
          <div className="flex-1 flex items-center justify-center">
            <EmptyState icon={<FileAudio size={48} />} title="拖拽音频文件到此处"
              description="或点击「从文件加载」开始分析"
              action={<Button onClick={handleImport}><Plus size={14} /> 导入音频</Button>} />
          </div>
        )}
      </div>

      {showPresetsEditor && <StagePresetsDrawer presets={store.stagePresets} onClose={() => { setShowPresetsEditor(false); store.loadStagePresets(); }} />}
    </div>
  );
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  子组件
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

function FileItem({ file, isActive, onSelect, onDelete, hasRunningTask }: {
  file: AudioFile; isActive: boolean; onSelect: () => void; onDelete: (ds: boolean) => void; hasRunningTask: boolean;
}) {
  const [showMenu, setShowMenu] = useState(false);
  return (
    <div className="relative">
      <button onClick={onSelect} onContextMenu={(e) => { e.preventDefault(); setShowMenu(true); }}
        className={cn("w-full text-left px-3 py-2 rounded-lg transition-all group text-xs",
          isActive ? "bg-brand-600/15 text-[var(--active-text)]" : "text-[var(--text-secondary)] hover:bg-[var(--hover-bg)]")}>
        <div className="flex items-center gap-2">
          <FileAudio size={14} className="shrink-0" />
          <span className="truncate flex-1">{file.display_name}</span>
          {hasRunningTask && <div className="w-2 h-2 rounded-full bg-red-400 animate-pulse" />}
          <button onClick={(e) => { e.stopPropagation(); onDelete(false); }} className="opacity-0 group-hover:opacity-100 transition-opacity">
            <Trash2 size={12} className="text-red-400" />
          </button>
        </div>
        <div className="flex items-center gap-2 mt-0.5 ml-5 text-[10px] text-[var(--text-muted)]">
          <span>{fmtDur(file.duration_ms)}</span>
          <span>{file.imported_at?.slice(0, 10)}</span>
        </div>
      </button>
      {showMenu && (
        <div className="absolute right-0 top-full z-50 mt-1 py-1 min-w-[140px] rounded-lg border border-[var(--border-subtle)] bg-[var(--surface-2)] shadow-lg"
          onMouseLeave={() => setShowMenu(false)}>
          <CtxBtn onClick={() => { navigator.clipboard.writeText(file.source_path); setShowMenu(false); }}>复制路径</CtxBtn>
          <CtxBtn onClick={() => { onDelete(false); setShowMenu(false); }}>移除</CtxBtn>
          <CtxBtn onClick={() => { onDelete(true); setShowMenu(false); }} className="text-red-400">删除源文件</CtxBtn>
        </div>
      )}
    </div>
  );
}

function CtxBtn({ children, onClick, className }: { children: React.ReactNode; onClick: () => void; className?: string }) {
  return <button onClick={onClick} className={cn("w-full text-left px-3 py-1.5 text-xs hover:bg-[var(--hover-bg)] transition-colors", className)}>{children}</button>;
}

function PlaybackBar({ playback, onTogglePlay, onSetSpeed, onSeek }: {
  playback: { positionMs: number; isPlaying: boolean; speed: number; durationMs: number }; onTogglePlay: () => void; onSetSpeed: (s: number) => void; onSeek: (ms: number) => void;
}) {
  const [showSpeedMenu, setShowSpeedMenu] = useState(false);
  const SPEEDS = [0.5, 0.75, 1, 1.25, 1.5, 2];
  return (
    <div className="px-4 py-2 border-b border-[var(--border-subtle)] flex items-center gap-3" style={{ backgroundColor: "var(--toolbar-bg)" }}>
      <Mic size={14} className="text-[var(--text-muted)] shrink-0" />
      <Button variant="ghost" size="icon" className="h-7 w-7" onClick={() => onSeek(Math.max(0, playback.positionMs - 5000))}><SkipBack size={14} /></Button>
      <Button variant="ghost" size="icon" className="h-8 w-8" onClick={onTogglePlay}>{playback.isPlaying ? <Pause size={16} /> : <Play size={16} />}</Button>
      <Button variant="ghost" size="icon" className="h-7 w-7" onClick={() => onSeek(Math.min(playback.durationMs, playback.positionMs + 5000))}><SkipForward size={14} /></Button>
      <input type="range" min={0} max={playback.durationMs || 1} value={playback.positionMs} onChange={(e) => onSeek(Number(e.target.value))} className="flex-1 h-1 accent-brand-500" />
      <span className="text-xs text-[var(--text-muted)] min-w-[80px] text-center">{fmtDur(playback.positionMs)} / {fmtDur(playback.durationMs)}</span>
      <div className="relative">
        <Button variant="ghost" size="sm" className="text-xs px-2" onClick={() => setShowSpeedMenu(!showSpeedMenu)}>{playback.speed}×</Button>
        {showSpeedMenu && (
          <div className="absolute right-0 bottom-full mb-1 py-1 rounded-lg border border-[var(--border-subtle)] bg-[var(--surface-2)] shadow-lg z-50" onMouseLeave={() => setShowSpeedMenu(false)}>
            {SPEEDS.map((s) => (
              <button key={s} onClick={() => { onSetSpeed(s); setShowSpeedMenu(false); }}
                className={cn("block w-full text-left px-4 py-1.5 text-xs hover:bg-[var(--hover-bg)]", s === playback.speed && "text-brand-400 font-semibold")}>{s}×</button>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  Tab 内容路由
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

interface TabProps {
  tab: AudioLabTabKind; bundle: AudioLabBundle; sessionId: string;
  currentSegmentId: string | null; playbackPositionMs: number; onSeekToSegment: (ms: number) => void;
  runningTasks: StudioTask[]; editingStageKey: string | null; editText: string;
  onSetEditing: (k: string | null, t?: string) => void; customStageKey: string | null;
  stagePresets: AudioStagePreset[]; onRefresh: () => void;
}

function TabContent(p: TabProps) {
  switch (p.tab) {
    case "Transcript":  return <TranscriptTab {...p} />;
    case "Summary":     return <StageTab {...p} stageKey="Summarized" />;
    case "MindMap":     return <MindMapTab {...p} />;
    case "Insight":     return <StageTab {...p} stageKey="Insight" />;
    case "Research":    return <ResearchTab {...p} />;
    case "Podcast":     return <PodcastTab {...p} />;
    case "Translation": return <TranslationTab {...p} />;
    case "Custom":      return <CustomTab {...p} />;
    default:            return null;
  }
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  PR-2: 转录 Tab
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

function TranscriptTab({ bundle, sessionId, currentSegmentId, onSeekToSegment, runningTasks, onRefresh }: TabProps) {
  const [parserKind, setParserKind] = useState("fast");
  const isTranscribing = runningTasks.some((t) => t.task_type === "audio_transcribe");
  const [ctxSegId, setCtxSegId] = useState<string | null>(null);
  const [ctxPos, setCtxPos] = useState<{ x: number; y: number } | null>(null);

  useEffect(() => {
    if (!currentSegmentId) return;
    document.getElementById(`seg-${currentSegmentId}`)?.scrollIntoView({ block: "nearest", behavior: "smooth" });
  }, [currentSegmentId]);

  const handleStart = async () => {
    try { await api.audiolabStartTranscription(sessionId, bundle.file.id, parserKind); onRefresh(); }
    catch (err) { console.error("转录失败:", err); }
  };

  const handleRenameSpeaker = async (segId: string) => {
    const seg = bundle.segments.find((s) => s.id === segId);
    if (!seg || !bundle.transcript) return;
    const name = prompt("新说话人名称", seg.speaker);
    if (!name || name === seg.speaker) return;
    await api.audiolabRenameSpeaker(bundle.transcript.id, seg.speaker_index, name);
    onRefresh();
  };

  return (
    <div className="max-w-4xl space-y-4">
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-3">
          <h2 className="text-lg font-semibold text-[var(--text-primary)]">转录</h2>
          {bundle.transcript && <Badge variant="green">已完成 · {bundle.segments.length} 段</Badge>}
        </div>
        <div className="flex items-center gap-2">
          <select value={parserKind} onChange={(e) => setParserKind(e.target.value)}
            className="h-8 px-2 text-xs rounded border border-[var(--border-subtle)] bg-[var(--surface-1)] text-[var(--text-primary)]">
            <option value="fast">快速解析</option><option value="batch">批量解析</option>
          </select>
          <Button size="sm" onClick={handleStart} disabled={isTranscribing}>
            {isTranscribing ? <><Loader2 size={12} className="animate-spin" /> 转录中...</> : <><RefreshCw size={12} /> {bundle.transcript ? "重新转录" : "开始转录"}</>}
          </Button>
        </div>
      </div>
      <Separator />

      {bundle.segments.length > 0 ? (
        <div className="space-y-1">
          {bundle.segments.map((seg) => (
            <div key={seg.id} id={`seg-${seg.id}`}
              onClick={() => onSeekToSegment(seg.start_ms)}
              onContextMenu={(e) => { e.preventDefault(); setCtxSegId(seg.id); setCtxPos({ x: e.clientX, y: e.clientY }); }}
              className={cn("px-4 py-2 rounded-lg cursor-pointer transition-all",
                currentSegmentId === seg.id ? "bg-brand-600/10 border border-brand-500/30" : "hover:bg-[var(--hover-bg)]")}>
              <div className="flex items-center gap-2 mb-0.5">
                <div className="w-4 h-4 rounded-full shrink-0" style={{ backgroundColor: speakerColor(seg.speaker_index) }} title={seg.speaker} />
                <span className="text-[10px] text-[var(--text-muted)]">{fmtDur(seg.start_ms)}</span>
                <span className="text-[10px] text-[var(--text-muted)] font-semibold">{seg.speaker}</span>
                {currentSegmentId === seg.id && <span className="text-[10px]">🔊</span>}
              </div>
              <p className="text-sm text-[var(--text-primary)] leading-relaxed ml-6">{seg.text}</p>
            </div>
          ))}
        </div>
      ) : isTranscribing ? (
        <GlassCard className="p-8 flex flex-col items-center gap-3">
          <Loader2 size={32} className="text-brand-400 animate-spin" />
          <p className="text-sm text-[var(--text-muted)]">正在转录中，请稍后回来查看...</p>
        </GlassCard>
      ) : (
        <GlassCard className="p-8 flex flex-col items-center gap-3">
          <FileText size={32} className="text-[var(--text-muted)]" />
          <p className="text-sm text-[var(--text-muted)]">{bundle.transcript ? "转录完成但无段落" : "点击「开始转录」"}</p>
        </GlassCard>
      )}

      {ctxSegId && ctxPos && (
        <SegCtxMenu segId={ctxSegId} pos={ctxPos} segments={bundle.segments}
          onClose={() => { setCtxSegId(null); setCtxPos(null); }}
          onRenameSpeaker={() => { handleRenameSpeaker(ctxSegId); setCtxSegId(null); setCtxPos(null); }}
          onSeek={(ms) => { onSeekToSegment(ms); setCtxSegId(null); setCtxPos(null); }}
          onRefresh={onRefresh} />
      )}
    </div>
  );
}

function SegCtxMenu({ segId, pos, segments, onClose, onRenameSpeaker, onSeek, onRefresh }: {
  segId: string; pos: { x: number; y: number }; segments: AudioSegment[];
  onClose: () => void; onRenameSpeaker: () => void; onSeek: (ms: number) => void; onRefresh: () => void;
}) {
  const seg = segments.find((s) => s.id === segId);
  if (!seg) return null;
  const handleEditTime = async () => {
    const s = prompt("起始(ms)", String(seg.start_ms)); if (!s) return;
    const e = prompt("结束(ms)", String(seg.end_ms)); if (!e) return;
    await api.audiolabUpdateSegment(segId, undefined, undefined, Number(s), Number(e));
    onRefresh();
  };
  return (
    <div className="fixed z-[100] py-1 min-w-[160px] rounded-lg border border-[var(--border-subtle)] bg-[var(--surface-2)] shadow-lg"
      style={{ left: pos.x, top: pos.y }} onMouseLeave={onClose}>
      <CtxBtn onClick={() => { navigator.clipboard.writeText(seg.text); onClose(); }}>复制文本</CtxBtn>
      <CtxBtn onClick={() => onSeek(seg.start_ms)}>跳到此段</CtxBtn>
      <CtxBtn onClick={onRenameSpeaker}>修改说话人</CtxBtn>
      <CtxBtn onClick={handleEditTime}>修改时间戳</CtxBtn>
    </div>
  );
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  PR-3: 通用阶段 Tab（Summary / Insight）
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

function StageTab({ bundle, sessionId, stageKey, editingStageKey, editText, onSetEditing, onRefresh }: TabProps & { stageKey: string }) {
  const output = bundle.stage_outputs.find((o) => o.stage_key === stageKey);
  const isEditing = editingStageKey === stageKey;
  const isProcessing = output?.status === "Processing";
  const isReady = output?.status === "Ready";
  const hasContent = isReady && output?.content_markdown;
  const label = TABS.find((t) => t.key === stageKey)?.label ?? stageKey;

  const handleGen = async () => { await api.audiolabStartStage(sessionId, stageKey); onRefresh(); };
  const handleSave = async () => { await api.audiolabUpdateStageContent(sessionId, stageKey, editText); onSetEditing(null); onRefresh(); };

  return (
    <div className="max-w-4xl space-y-4">
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-3">
          <h2 className="text-lg font-semibold text-[var(--text-primary)]">{label}</h2>
          {output && <Badge variant={isReady ? "green" : isProcessing ? "blue" : output.status === "Error" ? "red" : "default"}>
            {isReady ? "已完成" : isProcessing ? "处理中" : output.status === "Error" ? "失败" : "空"}
          </Badge>}
        </div>
        <div className="flex items-center gap-2">
          {hasContent && !isEditing && <>
            <Button variant="ghost" size="sm" className="text-xs" onClick={() => onSetEditing(stageKey, output!.content_markdown)}><Edit3 size={12} /> 编辑</Button>
            <Button variant="ghost" size="sm" className="text-xs" onClick={() => navigator.clipboard.writeText(output!.content_markdown)}><Copy size={12} /> 复制</Button>
          </>}
          {isEditing && <Button size="sm" onClick={handleSave}><Save size={12} /> 保存</Button>}
          <Button size="sm" variant="secondary" onClick={handleGen} disabled={isProcessing}><RefreshCw size={12} /> {hasContent ? "重新生成" : "生成"}</Button>
        </div>
      </div>
      <Separator />
      {isEditing ? (
        <textarea className="w-full min-h-[300px] p-4 text-sm rounded-lg border border-[var(--border-subtle)] bg-[var(--surface-1)] text-[var(--text-primary)] resize-y"
          value={editText} onChange={(e) => onSetEditing(stageKey, e.target.value)} />
      ) : hasContent ? (
        <GlassCard className="p-5"><pre className="text-sm text-[var(--text-primary)] whitespace-pre-wrap leading-relaxed">{output!.content_markdown}</pre></GlassCard>
      ) : isProcessing ? (
        <GlassCard className="p-8 flex flex-col items-center gap-3"><Loader2 size={32} className="text-brand-400 animate-spin" /><p className="text-sm text-[var(--text-muted)]">正在生成{label}...</p></GlassCard>
      ) : output?.status === "Error" ? (
        <GlassCard className="p-5 border-red-500/30"><p className="text-sm text-red-400">{output.error_message || "失败"}</p></GlassCard>
      ) : (
        <EmptyStage label={label} hasTranscript={!!bundle.transcript} onGen={handleGen} />
      )}
    </div>
  );
}

function EmptyStage({ label, hasTranscript, onGen }: { label: string; hasTranscript: boolean; onGen: () => void }) {
  return (
    <GlassCard className="p-8 flex flex-col items-center gap-3">
      <p className="text-sm text-[var(--text-muted)]">{hasTranscript ? `转录已完成，可点击「生成」生成${label}。` : `请先完成音频转录。`}</p>
      {hasTranscript && <Button size="sm" onClick={onGen}><Sparkles size={12} /> 生成</Button>}
    </GlassCard>
  );
}

// ── PR-3: MindMap ──
function MindMapTab({ bundle, sessionId, onRefresh }: TabProps) {
  const output = bundle.stage_outputs.find((o) => o.stage_key === "MindMap");
  const isProc = output?.status === "Processing";
  const has = output?.status === "Ready" && output?.content_markdown;
  const gen = async () => { await api.audiolabStartStage(sessionId, "MindMap"); onRefresh(); };
  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <h2 className="text-lg font-semibold text-[var(--text-primary)]">思维导图</h2>
        <div className="flex items-center gap-2">
          <span className="text-[10px] text-[var(--text-muted)]">滚轮缩放 · 拖拽平移 · 双击复位</span>
          <Button size="sm" variant="secondary" onClick={gen} disabled={isProc}><RefreshCw size={12} /> {has ? "重新生成" : "生成"}</Button>
        </div>
      </div>
      <Separator />
      {has ? <div className="min-h-[400px]"><MindMapCanvas data={output!.content_markdown} className="min-h-[400px]" /></div>
        : isProc ? <GlassCard className="p-8 flex flex-col items-center gap-3"><Loader2 size={32} className="text-brand-400 animate-spin" /><p className="text-sm text-[var(--text-muted)]">生成中...</p></GlassCard>
        : <EmptyStage label="思维导图" hasTranscript={!!bundle.transcript} onGen={gen} />}
    </div>
  );
}

// ── PR-3: Research ──
function ResearchTab({ bundle, sessionId, onRefresh }: TabProps) {
  const [selId, setSelId] = useState<string | null>(null);
  const sel = bundle.research_topics.find((t) => t.id === selId);
  const addTopic = async () => { const t = prompt("标题"); if (!t) return; await api.audiolabAddResearchTopic(sessionId, t, ""); onRefresh(); };
  const startRes = async (id: string) => { await api.audiolabStartResearch(id); onRefresh(); };
  const delTopic = async (id: string) => { await api.audiolabRemoveResearchTopic(id); if (selId === id) setSelId(null); onRefresh(); };
  return (
    <div className="max-w-5xl space-y-4">
      <div className="flex items-center justify-between">
        <h2 className="text-lg font-semibold text-[var(--text-primary)]">深度研究</h2>
        <Button size="sm" onClick={addTopic}><Plus size={12} /> 添加主题</Button>
      </div>
      <Separator />
      {bundle.research_topics.length > 0 ? (
        <div className="flex gap-4 min-h-[300px]">
          <div className="w-[200px] space-y-1 shrink-0">
            {bundle.research_topics.map((t) => (
              <button key={t.id} onClick={() => setSelId(t.id)}
                className={cn("w-full text-left px-3 py-2 rounded-lg text-xs transition-all",
                  selId === t.id ? "bg-brand-600/15 text-[var(--active-text)]" : "text-[var(--text-secondary)] hover:bg-[var(--hover-bg)]")}>
                <div className="font-medium truncate">{t.title}</div>
                <Badge variant={t.status === "completed" ? "green" : "default"}>{t.status}</Badge>
              </button>
            ))}
          </div>
          <div className="flex-1 min-w-0">
            {sel ? (
              <div className="space-y-3">
                <div className="flex items-center justify-between">
                  <h3 className="text-sm font-semibold text-[var(--text-primary)]">{sel.title}</h3>
                  <div className="flex gap-2">
                    <Button size="sm" variant="secondary" onClick={() => startRes(sel.id)}><Sparkles size={12} /> 生成</Button>
                    <Button size="sm" variant="ghost" onClick={() => delTopic(sel.id)}><Trash2 size={12} /></Button>
                  </div>
                </div>
                {sel.report_markdown ? <GlassCard className="p-4"><pre className="text-sm whitespace-pre-wrap leading-relaxed text-[var(--text-primary)]">{sel.report_markdown}</pre></GlassCard>
                  : <p className="text-xs text-[var(--text-muted)] italic">尚未生成报告</p>}
              </div>
            ) : <div className="flex items-center justify-center h-full text-sm text-[var(--text-muted)]">选择主题查看</div>}
          </div>
        </div>
      ) : <EmptyStage label="研究" hasTranscript={!!bundle.transcript} onGen={addTopic} />}
    </div>
  );
}

// ── PR-3: Podcast ──
function PodcastTab({ bundle, sessionId, onRefresh }: TabProps) {
  const out = bundle.stage_outputs.find((o) => o.stage_key === "PodcastScript");
  const isProc = out?.status === "Processing";
  const has = out?.status === "Ready" && out?.content_markdown;
  const genScript = async () => { await api.audiolabStartStage(sessionId, "PodcastScript"); onRefresh(); };
  const synTts = async () => { await api.audiolabStartPodcastTts(sessionId); onRefresh(); };
  return (
    <div className="max-w-4xl space-y-4">
      <div className="flex items-center justify-between">
        <h2 className="text-lg font-semibold text-[var(--text-primary)]">播客</h2>
        <div className="flex items-center gap-2">
          {has && <Button size="sm" variant="secondary" onClick={synTts}><Mic size={12} /> 合成音频</Button>}
          <Button size="sm" variant="secondary" onClick={genScript} disabled={isProc}><RefreshCw size={12} /> {has ? "重新生成" : "生成台本"}</Button>
        </div>
      </div>
      <Separator />
      {has ? <GlassCard className="p-5"><pre className="text-sm whitespace-pre-wrap leading-relaxed text-[var(--text-primary)]">{out!.content_markdown}</pre></GlassCard>
        : isProc ? <GlassCard className="p-8 flex flex-col items-center gap-3"><Loader2 size={32} className="text-brand-400 animate-spin" /><p className="text-sm text-[var(--text-muted)]">生成台本中...</p></GlassCard>
        : <EmptyStage label="播客台本" hasTranscript={!!bundle.transcript} onGen={genScript} />}
    </div>
  );
}

// ── PR-3: Translation ──
function TranslationTab({ bundle, sessionId, onRefresh }: TabProps) {
  const out = bundle.stage_outputs.find((o) => o.stage_key === "Translated");
  const isProc = out?.status === "Processing";
  const has = out?.status === "Ready" && out?.content_markdown;
  const gen = async () => { await api.audiolabStartStage(sessionId, "Translated"); onRefresh(); };
  return (
    <div className="max-w-5xl space-y-4">
      <div className="flex items-center justify-between">
        <h2 className="text-lg font-semibold text-[var(--text-primary)]">翻译</h2>
        <Button size="sm" variant="secondary" onClick={gen} disabled={isProc}><RefreshCw size={12} /> {has ? "重新生成" : "翻译"}</Button>
      </div>
      <Separator />
      {has ? (
        <GlassCard className="p-5"><div className="space-y-3">
          {bundle.segments.map((seg, i) => {
            const lines = out!.content_markdown.split("\n");
            return (
              <div key={seg.id} className="grid grid-cols-2 gap-4 text-sm">
                <div className="text-[var(--text-primary)]"><span className="text-[10px] text-[var(--text-muted)] mr-2">{fmtDur(seg.start_ms)}</span>{seg.text}</div>
                <div className="text-[var(--text-secondary)] italic">{lines[i] ?? ""}</div>
              </div>
            );
          })}
        </div></GlassCard>
      ) : isProc ? <GlassCard className="p-8 flex flex-col items-center gap-3"><Loader2 size={32} className="text-brand-400 animate-spin" /><p className="text-sm text-[var(--text-muted)]">翻译中...</p></GlassCard>
        : <EmptyStage label="翻译" hasTranscript={!!bundle.transcript} onGen={gen} />}
    </div>
  );
}

// ── PR-4: Custom Tab ──
function CustomTab({ bundle, sessionId, customStageKey, stagePresets, onRefresh }: TabProps) {
  const store = useAudioLabStore();
  const preset = stagePresets.find((p) => p.stage === customStageKey);
  const fullKey = customStageKey ? `Custom:${customStageKey}` : null;
  const out = fullKey ? bundle.stage_outputs.find((o) => o.stage_key === fullKey) : null;
  const isProc = out?.status === "Processing";
  const has = out?.status === "Ready" && out?.content_markdown;
  const isMM = preset?.display_mode === "MindMap";
  const gen = async () => { if (fullKey) { await api.audiolabStartStage(sessionId, fullKey); onRefresh(); } };
  const customs = stagePresets.filter((p) => p.is_enabled);
  return (
    <div className="max-w-4xl space-y-4">
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-3">
          <h2 className="text-lg font-semibold text-[var(--text-primary)]">自定义阶段</h2>
          <select value={customStageKey ?? ""} onChange={(e) => store.setCustomStageKey(e.target.value || null)}
            className="h-8 px-2 text-xs rounded border border-[var(--border-subtle)] bg-[var(--surface-1)] text-[var(--text-primary)]">
            <option value="">选择...</option>
            {customs.map((p) => <option key={p.stage} value={p.stage}>{p.display_name}</option>)}
          </select>
        </div>
        {customStageKey && <Button size="sm" variant="secondary" onClick={gen} disabled={isProc}><RefreshCw size={12} /> {has ? "重新生成" : "生成"}</Button>}
      </div>
      <Separator />
      {!customStageKey ? <GlassCard className="p-8 flex flex-col items-center gap-3"><p className="text-sm text-[var(--text-muted)]">请选择自定义阶段</p></GlassCard>
        : has ? (isMM ? <div className="min-h-[400px]"><MindMapCanvas data={out!.content_markdown} className="min-h-[400px]" /></div>
          : <GlassCard className="p-5"><pre className="text-sm whitespace-pre-wrap leading-relaxed text-[var(--text-primary)]">{out!.content_markdown}</pre></GlassCard>)
        : isProc ? <GlassCard className="p-8 flex flex-col items-center gap-3"><Loader2 size={32} className="text-brand-400 animate-spin" /><p className="text-sm text-[var(--text-muted)]">处理中...</p></GlassCard>
        : <GlassCard className="p-8 flex flex-col items-center gap-3"><p className="text-sm text-[var(--text-muted)]">暂无内容</p>
          {bundle.transcript && <Button size="sm" onClick={gen}><Sparkles size={12} /> 生成</Button>}
        </GlassCard>}
    </div>
  );
}

// ── PR-4: StagePresets 编辑器抽屉 ──
function StagePresetsDrawer({ presets, onClose }: { presets: AudioStagePreset[]; onClose: () => void }) {
  const [local, setLocal] = useState<AudioStagePreset[]>([...presets]);
  const add = () => {
    const np: AudioStagePreset = { id: "", stage: `custom_${Date.now()}`, display_name: "新阶段", system_prompt: "", show_in_tab: true, include_in_batch: false, is_enabled: true, display_mode: "Markdown", sort_order: local.length };
    setLocal([...local, np]);
  };
  const save = async (p: AudioStagePreset) => { await api.audiolabUpsertStagePreset(p); };
  const del = async (stage: string) => { await api.audiolabDeleteStagePreset(stage); setLocal(local.filter((p) => p.stage !== stage)); };
  const upd = (stage: string, patch: Partial<AudioStagePreset>) => setLocal(local.map((p) => p.stage === stage ? { ...p, ...patch } : p));

  return (
    <div className="fixed inset-0 z-50 flex">
      <div className="flex-1 bg-black/30" onClick={onClose} />
      <div className="w-[400px] bg-[var(--surface-1)] border-l border-[var(--border-subtle)] flex flex-col">
        <div className="p-4 border-b border-[var(--border-subtle)] flex items-center justify-between">
          <h2 className="text-sm font-semibold text-[var(--text-primary)]">阶段预设编辑器</h2>
          <div className="flex gap-2">
            <Button size="sm" onClick={add}><Plus size={12} /> 新增</Button>
            <Button variant="ghost" size="icon" className="h-7 w-7" onClick={onClose}><X size={14} /></Button>
          </div>
        </div>
        <ScrollArea className="flex-1">
          <div className="p-4 space-y-2">
            {local.map((p) => (
              <div key={p.stage} className="p-3 rounded-lg border border-[var(--border-subtle)] bg-[var(--surface-2)]">
                <div className="flex items-center gap-2 mb-2">
                  <GripVertical size={12} className="text-[var(--text-muted)]" />
                  <input className="flex-1 text-xs font-medium bg-transparent text-[var(--text-primary)] outline-none" value={p.display_name}
                    onChange={(e) => upd(p.stage, { display_name: e.target.value })} />
                  <label className="flex items-center gap-1 text-[10px] text-[var(--text-muted)]">
                    <input type="checkbox" checked={p.is_enabled} onChange={(e) => upd(p.stage, { is_enabled: e.target.checked })} />启用
                  </label>
                  <label className="flex items-center gap-1 text-[10px] text-[var(--text-muted)]">
                    <input type="checkbox" checked={p.show_in_tab} onChange={(e) => upd(p.stage, { show_in_tab: e.target.checked })} />Tab
                  </label>
                  <label className="flex items-center gap-1 text-[10px] text-[var(--text-muted)]">
                    <input type="checkbox" checked={p.display_mode === "MindMap"} onChange={(e) => upd(p.stage, { display_mode: e.target.checked ? "MindMap" : "Markdown" })} />导图
                  </label>
                </div>
                <textarea className="w-full h-16 text-xs p-2 rounded border border-[var(--border-subtle)] bg-[var(--surface-1)] text-[var(--text-primary)] resize-y"
                  placeholder="系统提示词..." value={p.system_prompt} onChange={(e) => upd(p.stage, { system_prompt: e.target.value })} />
                <div className="flex justify-end gap-2 mt-2">
                  <Button variant="ghost" size="sm" className="text-xs text-red-400" onClick={() => del(p.stage)}><Trash2 size={10} /> 删除</Button>
                  <Button size="sm" className="text-xs" onClick={() => save(p)}><Save size={10} /> 保存</Button>
                </div>
              </div>
            ))}
          </div>
        </ScrollArea>
      </div>
    </div>
  );
}

// ── 工具函数 ──
function fmtDur(ms: number): string {
  if (ms <= 0) return "0:00";
  const s = Math.floor(ms / 1000);
  return `${Math.floor(s / 60)}:${(s % 60).toString().padStart(2, "0")}`;
}
