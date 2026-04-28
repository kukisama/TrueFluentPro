import { useState, useEffect, useCallback, useRef, useMemo } from "react";
import { useTranslation } from "react-i18next";
import {
  ListChecks, RefreshCw, CheckCircle2, AlertCircle,
  Clock, Play, XCircle, Ban, RotateCcw,
  Trash2, Copy, Download, Search,
  ChevronDown, ChevronRight, Filter,
} from "lucide-react";
import { cn } from "../lib/utils";
import {
  Button, GlassCard, ScrollArea, Separator,
} from "../components/ui";
import { api } from "../lib/tauri-api";
import type {
  MonitorSnapshot,
  MonitorExecutionRecord, MonitorSettings,
} from "../lib/tauri-api";

/* ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
   任务监控 — 完全复刻 C# TaskQueueMonitorView
   左 216px 导航 + 右内容（6 列任务表 + 详情卡 + 执行历史）
   ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━ */

const SORT_COLUMNS = ["TaskId", "AudioFileName", "Stage", "Status", "SubmittedAt"] as const;
type SortColumn = typeof SORT_COLUMNS[number];

export function TaskMonitorView() {
  useTranslation();

  const [snapshot, setSnapshot] = useState<MonitorSnapshot | null>(null);
  const [settings, setSettings] = useState<MonitorSettings>({ max_transcription_concurrency: 2, max_ai_concurrency: 4, transcription_timeout_minutes: 10 });
  const [activeBucket, setActiveBucket] = useState("pending");
  const [sortColumn, setSortColumn] = useState<SortColumn>("SubmittedAt");
  const [sortAscending, setSortAscending] = useState(false);
  const [selectedTaskId, setSelectedTaskId] = useState<string | null>(null);
  const [executions, setExecutions] = useState<MonitorExecutionRecord[]>([]);
  const [selectedExecId, setSelectedExecId] = useState<string | null>(null);
  const [showExecutions, setShowExecutions] = useState(true);
  const [loading, setLoading] = useState(false);
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());
  const [searchText, setSearchText] = useState("");
  const [stageFilter, setStageFilter] = useState<string[]>([]);
  const [showFilterBar, setShowFilterBar] = useState(false);
  const [toast, setToast] = useState<{ id: string; message: string; taskId: string } | null>(null);
  // PR-3.3: 右键菜单
  const [contextMenu, setContextMenu] = useState<{ x: number; y: number; taskId: string; taskStatus: string } | null>(null);
  // PR-3.8: 执行历史选中锁定（选中某 execution 时不被 push 覆盖）
  const selectionLockedRef = useRef(false);

  const settingsDebounceRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const uiStateDebounceRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const uiStateInitialized = useRef(false);

  const selectedTask = useMemo(
    () => snapshot?.current_bucket_tasks.find(t => t.id === selectedTaskId) ?? null,
    [snapshot, selectedTaskId]
  );

  const loadSnapshot = useCallback(async (bucket?: string) => {
    // PR-3.8: 选中锁定时不刷新（避免覆盖正在查看的 execution 详情）
    if (selectionLockedRef.current) return;
    setLoading(true);
    try {
      const snap = await api.monitorGetSnapshot(bucket ?? activeBucket, sortColumn, sortAscending);
      setSnapshot(snap);
    } catch (err) {
      console.error("Monitor snapshot failed:", err);
    } finally {
      setLoading(false);
    }
  }, [activeBucket, sortColumn, sortAscending]);

  const loadSettings = useCallback(async () => {
    try {
      const s = await api.monitorGetSettings();
      setSettings(s);
    } catch (err) {
      console.error("Monitor settings failed:", err);
    }
  }, []);

  const loadExecutions = useCallback(async (taskId: string) => {
    try {
      const execs = await api.monitorListExecutions(taskId);
      setExecutions(execs);
    } catch (err) {
      console.error("Load executions failed:", err);
    }
  }, []);

  useEffect(() => {
    // PR-2.14: 先从 KV 恢复 UI 状态，再加载数据
    (async () => {
      try {
        const saved = await api.monitorLoadUiState();
        if (saved) {
          setActiveBucket(saved.active_bucket);
          setSortColumn(saved.sort_column as SortColumn);
          setSortAscending(saved.sort_ascending);
          if (saved.selected_task_id) setSelectedTaskId(saved.selected_task_id);
        }
      } catch { /* ignore */ }
      uiStateInitialized.current = true;
      loadSnapshot();
      loadSettings();
    })();
  }, []);

  // PR-2.14: 当 UI 状态变化时 debounce 500ms 保存到 KV
  useEffect(() => {
    if (!uiStateInitialized.current) return;
    if (uiStateDebounceRef.current) clearTimeout(uiStateDebounceRef.current);
    uiStateDebounceRef.current = setTimeout(() => {
      api.monitorSaveUiState({ active_bucket: activeBucket, sort_column: sortColumn, sort_ascending: sortAscending, selected_task_id: selectedTaskId }).catch(() => {});
    }, 500);
  }, [activeBucket, sortColumn, sortAscending, selectedTaskId]);

  useEffect(() => {
    let cancelled = false;
    let unlisten: (() => void) | null = null;
    // monitor-snapshot-update 是信号事件（null payload），触发 refetch
    api.onMonitorSnapshotUpdate(() => {
      if (!cancelled) loadSnapshot();
    }).then((fn) => { if (cancelled) fn(); else unlisten = fn; });
    let unlistenTask: (() => void) | null = null;
    api.onTaskEvent((event) => {
      if (event.type === "TaskFailed" && event.error) {
        setToast({ id: event.task_id, message: event.error, taskId: event.task_id });
        setTimeout(() => setToast(null), 8000);
      }
      loadSnapshot();
    }).then((fn) => { if (cancelled) fn(); else unlistenTask = fn; });
    return () => { cancelled = true; unlisten?.(); unlistenTask?.(); };
  }, [loadSnapshot]);

  useEffect(() => {
    const timer = setInterval(() => {
      if (activeBucket === "running") loadSnapshot();
    }, 5000);
    return () => clearInterval(timer);
  }, [activeBucket, loadSnapshot]);

  const handleBucketClick = useCallback(async (key: string) => {
    setActiveBucket(key);
    setSelectedTaskId(null);
    setSelectedIds(new Set());
    setExecutions([]);
    try {
      const snap = await api.monitorGetSnapshot(key, sortColumn, sortAscending);
      setSnapshot(snap);
    } catch (err) { console.error(err); }
  }, [sortColumn, sortAscending]);

  const handleSort = useCallback((col: SortColumn) => {
    if (sortColumn === col) setSortAscending(!sortAscending);
    else { setSortColumn(col); setSortAscending(true); }
  }, [sortColumn, sortAscending]);

  useEffect(() => { loadSnapshot(); }, [sortColumn, sortAscending]);

  const handleSelectTask = useCallback(async (taskId: string) => {
    setSelectedTaskId(taskId);
    setSelectedExecId(null);
    selectionLockedRef.current = false; // PR-3.8: 切换任务解除锁定
    await loadExecutions(taskId);
  }, [loadExecutions]);

  const handleCancelTask = useCallback(async (taskId: string) => {
    if (!confirm("确认取消此任务？")) return;
    try { await api.monitorCancelTask(taskId, "user_cancel"); await loadSnapshot(); } catch (err) { console.error(err); }
  }, [loadSnapshot]);

  const handleRetryTask = useCallback(async (taskId: string) => {
    try { await api.monitorRetryTask(taskId); await loadSnapshot(); } catch (err) { console.error(err); }
  }, [loadSnapshot]);

  const handleCleanup = useCallback(async () => {
    if (!confirm("确认清理7天前已完成/取消任务？")) return;
    try { await api.monitorCleanupCompleted(7); await loadSnapshot(); } catch (err) { console.error(err); }
  }, [loadSnapshot]);

  const handleSettingsChange = useCallback((field: keyof MonitorSettings, value: number) => {
    setSettings(prev => ({ ...prev, [field]: value }));
    if (settingsDebounceRef.current) clearTimeout(settingsDebounceRef.current);
    settingsDebounceRef.current = setTimeout(async () => {
      try {
        const updated = { ...settings, [field]: value };
        await api.monitorUpdateSettings(updated.max_transcription_concurrency, updated.max_ai_concurrency, updated.transcription_timeout_minutes);
      } catch (err) { console.error(err); }
    }, 500);
  }, [settings]);

  const handleCopyText = useCallback((text: string) => { navigator.clipboard.writeText(text); }, []);

  const handleBatchCancel = useCallback(async () => {
    if (selectedIds.size === 0) return;
    if (!confirm(`确认批量取消 ${selectedIds.size} 个任务？`)) return;
    try { await api.monitorBatchCancel([...selectedIds]); setSelectedIds(new Set()); await loadSnapshot(); } catch (err) { console.error(err); }
  }, [selectedIds, loadSnapshot]);

  const handleBatchDelete = useCallback(async () => {
    if (selectedIds.size === 0) return;
    if (!confirm(`确认批量删除 ${selectedIds.size} 个任务？`)) return;
    try { await api.monitorBatchDelete([...selectedIds]); setSelectedIds(new Set()); await loadSnapshot(); } catch (err) { console.error(err); }
  }, [selectedIds, loadSnapshot]);

  const handleExportCsv = useCallback(async () => {
    try {
      const path = `task_monitor_export_${Date.now()}.csv`;
      await api.monitorExportCsv(path, activeBucket === "all" ? undefined : activeBucket);
    } catch (err) { console.error(err); }
  }, [activeBucket]);

  const handleSelectAll = useCallback(() => {
    if (!snapshot) return;
    setSelectedIds(new Set(snapshot.current_bucket_tasks.map(t => t.id)));
  }, [snapshot]);

  const toggleSelection = useCallback((taskId: string) => {
    setSelectedIds(prev => {
      const next = new Set(prev);
      if (next.has(taskId)) next.delete(taskId); else next.add(taskId);
      return next;
    });
  }, []);

  const filteredTasks = useMemo(() => {
    if (!snapshot) return [];
    let tasks = snapshot.current_bucket_tasks;
    if (searchText.trim()) {
      const q = searchText.toLowerCase();
      tasks = tasks.filter(t => t.id.toLowerCase().includes(q) || t.audio_file_name.toLowerCase().includes(q) || t.stage_display_name.toLowerCase().includes(q));
    }
    if (stageFilter.length > 0) {
      tasks = tasks.filter(t => stageFilter.includes(t.stage));
    }
    return tasks;
  }, [snapshot, searchText, stageFilter]);

  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if (e.key === "Delete" && selectedIds.size > 0) { e.preventDefault(); handleBatchCancel(); }
      if (e.key === "a" && e.ctrlKey) { e.preventDefault(); handleSelectAll(); }
      if (e.key === "Escape") { setSelectedIds(new Set()); setSelectedTaskId(null); setContextMenu(null); selectionLockedRef.current = false; }
      if (e.key === "F5" && !e.ctrlKey) { e.preventDefault(); selectionLockedRef.current = false; loadSnapshot(); }
    };
    const clickHandler = () => setContextMenu(null);
    window.addEventListener("keydown", handler);
    window.addEventListener("click", clickHandler);
    return () => { window.removeEventListener("keydown", handler); window.removeEventListener("click", clickHandler); };
  }, [handleBatchCancel, handleSelectAll, loadSnapshot, selectedIds]);

  const buckets = snapshot?.buckets ?? [];
  const globalStats = snapshot?.global_stats ?? { total_executions: 0, billable_executions: 0, billable_tokens_in: 0, billable_tokens_out: 0 };

  return (
    <div className="flex h-full">
      {/* ━━━ 左侧导航 216px ━━━ */}
      <div className="w-[216px] shrink-0 border-r border-[var(--border-subtle)] flex flex-col" style={{ backgroundColor: "var(--sidebar-bg)" }}>
        <div className="p-3 flex flex-col gap-3 flex-1 overflow-auto">
          <div className="flex items-center gap-1">
            <span className="text-sm font-semibold text-[var(--text-primary)] flex-1">任务分类</span>
            <button className="p-1 rounded hover:bg-[var(--hover-bg)]" title="刷新" onClick={() => loadSnapshot()}>
              <RefreshCw size={13} className={cn(loading && "animate-spin")} />
            </button>
            <button className="p-1 rounded hover:bg-[var(--hover-bg)]" title="清理7天前已完成/取消任务" onClick={handleCleanup}>
              <Trash2 size={13} />
            </button>
          </div>
          <div className="space-y-0.5">
            {buckets.map(b => (
              <button key={b.key} onClick={() => handleBucketClick(b.key)}
                className={cn("w-full flex items-center gap-2.5 px-2.5 py-1.5 rounded-md text-xs transition-colors",
                  activeBucket === b.key ? "bg-brand-600/15 text-[var(--active-text)]" : "text-[var(--text-secondary)] hover:bg-[var(--hover-bg)]")}>
                <BucketIcon icon={b.icon} />
                <span className="flex-1 text-left">{b.title}</span>
                <span className={cn("min-w-[20px] text-center text-[10px] px-1.5 py-0.5 rounded-full font-medium",
                  b.is_danger ? "bg-red-500/20 text-red-400" : "bg-[var(--surface-2)] text-[var(--text-muted)]")}>{b.count}</span>
              </button>
            ))}
          </div>
          <Separator />
          <div className="space-y-1.5">
            <span className="text-[11px] font-semibold text-[var(--text-muted)]">并发设置</span>
            <div className="grid grid-cols-[auto_1fr] gap-x-2 gap-y-1.5 items-center">
              <span className="text-[11px] text-[var(--text-muted)]">转录</span>
              <input type="number" min={1} max={20} value={settings.max_transcription_concurrency}
                onChange={e => handleSettingsChange("max_transcription_concurrency", Number(e.target.value))}
                className="w-full h-6 px-1.5 text-[11px] rounded border border-[var(--border-medium)] bg-[var(--input-bg)] text-[var(--text-primary)]" />
              <span className="text-[11px] text-[var(--text-muted)]">AI</span>
              <input type="number" min={1} max={20} value={settings.max_ai_concurrency}
                onChange={e => handleSettingsChange("max_ai_concurrency", Number(e.target.value))}
                className="w-full h-6 px-1.5 text-[11px] rounded border border-[var(--border-medium)] bg-[var(--input-bg)] text-[var(--text-primary)]" />
            </div>
          </div>
          <Separator />
          <div className="space-y-1.5">
            <span className="text-[11px] font-semibold text-[var(--text-muted)]">超时设置</span>
            <div className="grid grid-cols-[auto_1fr] gap-x-2 items-center">
              <span className="text-[11px] text-[var(--text-muted)]">转录</span>
              <input type="number" min={1} max={60} value={settings.transcription_timeout_minutes}
                onChange={e => handleSettingsChange("transcription_timeout_minutes", Number(e.target.value))}
                className="w-full h-6 px-1.5 text-[11px] rounded border border-[var(--border-medium)] bg-[var(--input-bg)] text-[var(--text-primary)]" />
            </div>
          </div>
          <Separator />
          <div className="space-y-1">
            <span className="text-[11px] font-semibold text-[var(--text-muted)]">Token 用量</span>
            <p className="text-[10px] text-[var(--text-muted)]">执行 {globalStats.total_executions} 次 · 计费 {globalStats.billable_executions} 次</p>
            <p className="text-[10px] text-[var(--text-muted)]">
              {(globalStats.billable_tokens_in + globalStats.billable_tokens_out) > 0
                ? `入 ${globalStats.billable_tokens_in.toLocaleString()} / 出 ${globalStats.billable_tokens_out.toLocaleString()}`
                : "暂无数据"}
            </p>
          </div>
        </div>
      </div>

      {/* ━━━ 右侧内容 ━━━ */}
      <div className="flex-1 flex flex-col min-w-0">
        {selectedIds.size > 0 && (
          <div className="flex items-center gap-2 px-4 py-2 border-b border-[var(--border-subtle)] bg-brand-600/5 text-xs">
            <span className="text-[var(--text-secondary)] font-medium">已选 {selectedIds.size} 个</span>
            <Button variant="ghost" size="sm" onClick={handleBatchCancel}><XCircle size={12} /> 批量取消</Button>
            <Button variant="ghost" size="sm" onClick={handleBatchDelete}><Trash2 size={12} /> 批量删除</Button>
            <Button variant="ghost" size="sm" onClick={() => setSelectedIds(new Set())}>取消选择</Button>
          </div>
        )}
        <div className="flex items-center gap-2 px-4 py-2 border-b border-[var(--border-subtle)]">
          <div className="relative flex-1 max-w-xs">
            <Search size={12} className="absolute left-2 top-1/2 -translate-y-1/2 text-[var(--text-muted)]" />
            <input type="text" placeholder="搜索任务ID / 文件名..." value={searchText}
              onChange={e => setSearchText(e.target.value)}
              className="w-full h-7 pl-7 pr-2 text-xs rounded border border-[var(--border-medium)] bg-[var(--input-bg)] text-[var(--text-primary)]" />
          </div>
          <button onClick={() => setShowFilterBar(!showFilterBar)}
            className={cn("p-1.5 rounded hover:bg-[var(--hover-bg)]", showFilterBar && "bg-brand-600/15")}><Filter size={13} /></button>
          <button className="p-1.5 rounded hover:bg-[var(--hover-bg)]" title="导出 CSV" onClick={handleExportCsv}><Download size={13} /></button>
        </div>
        {showFilterBar && (
          <div className="flex items-center gap-1.5 px-4 py-1.5 border-b border-[var(--border-subtle)] bg-[var(--surface-1)] flex-wrap">
            {["Transcribed","Summarized","MindMap","Insight","PodcastScript","Research","Translated","ImageGen","VideoGen"].map(s => (
              <button key={s} onClick={() => setStageFilter(prev => prev.includes(s) ? prev.filter(x => x !== s) : [...prev, s])}
                className={cn("px-2 py-0.5 rounded-full text-[10px] border transition-colors",
                  stageFilter.includes(s) ? "bg-brand-600/20 border-brand-500/50 text-[var(--active-text)]" : "border-[var(--border-subtle)] text-[var(--text-muted)]")}>{s}</button>
            ))}
            {stageFilter.length > 0 && <button onClick={() => setStageFilter([])} className="text-[10px] text-red-400 ml-2">清除</button>}
          </div>
        )}
        {/* 表头 */}
        <div className="grid grid-cols-[110px_1fr_90px_170px_120px_70px] gap-0 px-4 py-2 border-b border-[var(--border-subtle)]">
          <SortableHeader label="任务ID" column="TaskId" current={sortColumn} ascending={sortAscending} onSort={handleSort} />
          <SortableHeader label="音频" column="AudioFileName" current={sortColumn} ascending={sortAscending} onSort={handleSort} />
          <SortableHeader label="阶段" column="Stage" current={sortColumn} ascending={sortAscending} onSort={handleSort} />
          <span className="text-[11px] font-semibold text-[var(--text-muted)] flex items-center">状态</span>
          <SortableHeader label="发起时间" column="SubmittedAt" current={sortColumn} ascending={sortAscending} onSort={handleSort} />
          <span className="text-[11px] font-semibold text-[var(--text-muted)] flex items-center justify-end">耗时</span>
        </div>
        {/* 任务列表 */}
        <ScrollArea className="flex-1">
          {filteredTasks.length === 0 ? (
            <div className="flex items-center justify-center h-40">
              <div className="text-center">
                <ListChecks size={32} className="text-[var(--text-muted)] mx-auto mb-2" />
                <p className="text-xs text-[var(--text-muted)]">暂无任务</p>
              </div>
            </div>
          ) : (
            <div className="divide-y divide-[var(--border-subtle)]">
              {filteredTasks.map(task => (
                <div key={task.id} onClick={() => handleSelectTask(task.id)}
                  onContextMenu={(e) => { e.preventDefault(); setContextMenu({ x: e.clientX, y: e.clientY, taskId: task.id, taskStatus: task.status }); }}
                  className={cn("grid grid-cols-[110px_1fr_90px_170px_120px_70px] gap-0 px-4 h-8 items-center cursor-pointer transition-colors",
                    selectedTaskId === task.id ? "bg-brand-600/10" : "hover:bg-[var(--hover-bg)]")}>
                  <div className="flex items-center gap-1.5 min-w-0">
                    <input type="checkbox" checked={selectedIds.has(task.id)}
                      onChange={() => toggleSelection(task.id)} onClick={e => e.stopPropagation()} className="w-3 h-3 shrink-0" />
                    <span className="text-[11px] font-mono text-[var(--text-muted)] truncate">{task.short_task_id}</span>
                  </div>
                  <span className="text-[12px] text-[var(--text-primary)] truncate">{task.audio_file_name}</span>
                  <div className="flex items-center gap-1.5">
                    <div className="w-1 h-3.5 rounded-sm" style={{ backgroundColor: task.stage_color }} />
                    <span className="text-[11px]">{task.stage_display_name}</span>
                  </div>
                  <span className="text-[11px] font-semibold truncate" title={task.status_display_name}>{task.status_display_name}</span>
                  <span className="text-[11px] text-[var(--text-muted)]">{formatDateTime(task.submitted_at)}</span>
                  <span className="text-[11px] text-[var(--text-muted)] text-right">{task.elapsed_time}</span>
                </div>
              ))}
            </div>
          )}
        </ScrollArea>
        {/* 选中任务详情卡 */}
        {selectedTask && (
          <div className="border-t border-[var(--border-subtle)] max-h-[45%] overflow-auto p-4 space-y-3" style={{ backgroundColor: "var(--card-bg)" }}>
            <div className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1.5 text-xs">
              <span className="font-semibold text-[var(--text-muted)]">任务ID：</span>
              <div className="flex items-center gap-1">
                <span className="font-mono text-[11px]">{selectedTask.id}</span>
                <button onClick={() => handleCopyText(selectedTask.id)} className="p-0.5 rounded hover:bg-[var(--hover-bg)]"><Copy size={10} /></button>
              </div>
              <span className="font-semibold text-[var(--text-muted)]">音频：</span>
              <span>{selectedTask.audio_file_name}</span>
              <span className="font-semibold text-[var(--text-muted)]">阶段 / 状态：</span>
              <div className="flex items-center gap-3">
                <span>{selectedTask.stage_display_name}</span>
                <span className="font-semibold">{selectedTask.status_display_name}</span>
                {selectedTask.retry_count > 0 && <span className="text-[var(--text-muted)]">重试: {selectedTask.retry_count}</span>}
              </div>
              <span className="font-semibold text-[var(--text-muted)]">时间：</span>
              <div className="flex items-center gap-4">
                <span>提交: {formatDateTime(selectedTask.submitted_at)}</span>
                {selectedTask.started_at && <span>开始: {formatDateTime(selectedTask.started_at)}</span>}
                <span>耗时: {selectedTask.elapsed_time}</span>
              </div>
              {selectedTask.error_message && (
                <>
                  <span className="font-semibold text-[var(--text-muted)]">错误信息：</span>
                  <span className="text-red-400 break-all">{selectedTask.error_message}</span>
                </>
              )}
            </div>
            {executions.length > 0 && (
              <div className="space-y-2">
                <Separator />
                <button className="flex items-center gap-1 text-[11px] font-semibold text-[var(--text-muted)]"
                  onClick={() => setShowExecutions(!showExecutions)}>
                  {showExecutions ? <ChevronDown size={12} /> : <ChevronRight size={12} />}
                  执行历史 ({executions.length})
                </button>
                {showExecutions && (
                  <div className="space-y-1">
                    <div className="grid grid-cols-[70px_70px_120px_80px_80px_1fr] text-[10px] font-semibold text-[var(--text-muted)]">
                      <span>状态</span><span>计费</span><span>Token</span><span>耗时</span><span>时间</span><span>模型</span>
                    </div>
                    {executions.map(exec => (
                      <div key={exec.id}>
                        <div onClick={() => {
                          const newId = selectedExecId === exec.id ? null : exec.id;
                          setSelectedExecId(newId);
                          selectionLockedRef.current = newId !== null; // PR-3.8
                        }}
                          className={cn("grid grid-cols-[70px_70px_120px_80px_80px_1fr] h-5 items-center text-[11px] cursor-pointer rounded hover:bg-[var(--hover-bg)]",
                            selectedExecId === exec.id && "bg-brand-600/10")}>
                          <span>{exec.status_display_name}</span>
                          <span className="text-[var(--text-muted)]">{exec.billable_display}</span>
                          <span className="font-mono">{exec.tokens_display}</span>
                          <span className="text-[var(--text-muted)]">{exec.duration_display}</span>
                          <span className="text-[var(--text-muted)]">{exec.time_display}</span>
                          <span className="text-[var(--text-muted)] truncate">{exec.model_name ?? "--"}</span>
                        </div>
                        {selectedExecId === exec.id && exec.has_debug_data && (
                          <div className="ml-2 mt-1 mb-2 p-2 rounded border border-[var(--border-subtle)] bg-[var(--surface-1)] space-y-2">
                            <div className="flex items-center gap-2 text-[10px] font-semibold text-[var(--text-muted)]">
                              调试数据
                              {exec.debug_prompt && <button onClick={() => handleCopyText(exec.debug_prompt!)} className="p-0.5 rounded hover:bg-[var(--hover-bg)]" title="复制提示词"><Copy size={10} /></button>}
                            </div>
                            {exec.debug_prompt && (
                              <div>
                                <p className="text-[9px] text-[var(--text-muted)] mb-0.5">提示词</p>
                                <pre className="text-[10px] font-mono bg-[var(--surface-2)] rounded p-2 max-h-32 overflow-auto whitespace-pre-wrap">{exec.debug_prompt}</pre>
                              </div>
                            )}
                            {exec.debug_response && (
                              <div>
                                <div className="flex items-center gap-1">
                                  <p className="text-[9px] text-[var(--text-muted)] mb-0.5">响应</p>
                                  <button onClick={() => handleCopyText(exec.debug_response!)} className="p-0.5 rounded hover:bg-[var(--hover-bg)]" title="复制响应"><Copy size={10} /></button>
                                </div>
                                <pre className="text-[10px] font-mono bg-[var(--surface-2)] rounded p-2 max-h-32 overflow-auto whitespace-pre-wrap">{exec.debug_response}</pre>
                              </div>
                            )}
                          </div>
                        )}
                      </div>
                    ))}
                  </div>
                )}
              </div>
            )}
            <div className="flex items-center gap-2 pt-1">
              {(selectedTask.status === "Failed" || selectedTask.status === "Cancelled") && (
                <Button size="sm" onClick={() => handleRetryTask(selectedTask.id)}><RotateCcw size={12} /> 重试</Button>
              )}
              {(selectedTask.status === "Queued" || selectedTask.status === "Executing") && (
                <Button variant="danger" size="sm" onClick={() => handleCancelTask(selectedTask.id)}><XCircle size={12} /> 取消</Button>
              )}
            </div>
          </div>
        )}
      </div>

      {/* PR-3.3: 右键菜单 */}
      {contextMenu && (
        <div className="fixed z-50" style={{ left: contextMenu.x, top: contextMenu.y }}
          onClick={() => setContextMenu(null)} onMouseLeave={() => setContextMenu(null)}>
          <div className="bg-[var(--card-bg)] border border-[var(--border-medium)] rounded-lg shadow-xl py-1 min-w-[140px]"
            onClick={e => e.stopPropagation()}>
            <button className="w-full px-3 py-1.5 text-xs text-left hover:bg-[var(--hover-bg)]"
              onClick={() => { handleSelectTask(contextMenu.taskId); setContextMenu(null); }}>
              查看详情
            </button>
            <button className="w-full px-3 py-1.5 text-xs text-left hover:bg-[var(--hover-bg)]"
              onClick={() => { handleCopyText(contextMenu.taskId); setContextMenu(null); }}>
              复制任务ID
            </button>
            <Separator />
            {["Failed", "Cancelled", "Interrupted", "Timeout"].includes(contextMenu.taskStatus) && (
            <button className="w-full px-3 py-1.5 text-xs text-left hover:bg-[var(--hover-bg)]"
              onClick={() => { handleRetryTask(contextMenu.taskId); setContextMenu(null); }}>
              <RotateCcw size={11} className="inline mr-1" /> 重试
            </button>
            )}
            {["Queued", "Executing"].includes(contextMenu.taskStatus) && (
            <button className="w-full px-3 py-1.5 text-xs text-left text-red-400 hover:bg-[var(--hover-bg)]"
              onClick={() => { handleCancelTask(contextMenu.taskId); setContextMenu(null); }}>
              <XCircle size={11} className="inline mr-1" /> 取消任务
            </button>
            )}
          </div>
        </div>
      )}

      {/* Toast */}
      {toast && (
        <div className="fixed bottom-4 right-4 z-50 animate-in slide-in-from-bottom-4">
          <GlassCard className="p-3 border-red-500/30 max-w-sm">
            <div className="flex items-start gap-2">
              <AlertCircle size={14} className="text-red-400 shrink-0 mt-0.5" />
              <div className="flex-1 min-w-0">
                <p className="text-xs font-medium text-red-400">任务失败</p>
                <p className="text-[10px] text-[var(--text-muted)] truncate mt-0.5">{toast.message}</p>
              </div>
              <button onClick={() => { setToast(null); handleBucketClick("failed"); }} className="text-[10px] text-brand-400 shrink-0">查看</button>
            </div>
          </GlassCard>
        </div>
      )}
    </div>
  );
}

function SortableHeader({ label, column, current, ascending, onSort }: {
  label: string; column: SortColumn; current: SortColumn; ascending: boolean; onSort: (col: SortColumn) => void;
}) {
  const isActive = current === column;
  return (
    <button onClick={() => onSort(column)}
      className="flex items-center gap-0.5 text-[11px] font-semibold text-[var(--text-muted)] hover:text-[var(--text-secondary)]">
      {label} {isActive && <span className="text-[9px]">{ascending ? "↑" : "↓"}</span>}
    </button>
  );
}

function BucketIcon({ icon }: { icon: string }) {
  if (icon.includes("clock")) return <Clock size={13} />;
  if (icon.includes("rotate")) return <Play size={13} />;
  if (icon.includes("check")) return <CheckCircle2 size={13} />;
  if (icon.includes("exclamation") || icon.includes("triangle")) return <AlertCircle size={13} />;
  if (icon.includes("xmark")) return <Ban size={13} />;
  return <ListChecks size={13} />;
}

function formatDateTime(iso: string): string {
  if (!iso) return "";
  try {
    const d = new Date(iso);
    const mm = String(d.getMonth() + 1).padStart(2, "0");
    const dd = String(d.getDate()).padStart(2, "0");
    const hh = String(d.getHours()).padStart(2, "0");
    const mi = String(d.getMinutes()).padStart(2, "0");
    return `${mm}-${dd} ${hh}:${mi}`;
  } catch { return iso; }
}
