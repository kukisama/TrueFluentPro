import { useState, useEffect, useCallback } from "react";
import { useTranslation } from "react-i18next";
import {
  ListChecks, RefreshCw, CheckCircle2, AlertCircle,
  Clock, Play, Loader2, XCircle, Ban, RotateCcw,
  Zap, Eye, Trash2, ArrowUpDown, Settings2,
} from "lucide-react";
import { cn } from "../lib/utils";
import {
  Button, GlassCard, Badge, Progress, FadeIn, EmptyState,
  ScrollArea, Separator,
} from "../components/ui";
import { api } from "../lib/tauri-api";
import type { AudioTask, TaskExecution, TaskEngineStats, TaskStatus, TaskEvent } from "../lib/tauri-api";

/* ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
   任务监控 — 2 栏布局 + 5 桶 + 执行历史
   对标 C# TaskMonitorViewModel
   ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━ */

const STATUS_FILTERS: { key: TaskStatus | "all"; label: string; icon: React.ReactNode; color: string }[] = [
  { key: "all",        label: "全部",   icon: <ListChecks size={14} />, color: "text-[var(--text-secondary)]" },
  { key: "Queued",     label: "排队中", icon: <Clock size={14} />,      color: "text-[var(--text-muted)]" },
  { key: "Executing",  label: "执行中", icon: <Play size={14} />,       color: "text-blue-500" },
  { key: "Completed",  label: "已完成", icon: <CheckCircle2 size={14} />, color: "text-emerald-500" },
  { key: "Failed",     label: "失败",   icon: <AlertCircle size={14} />,  color: "text-red-500" },
  { key: "Cancelled",  label: "已取消", icon: <Ban size={14} />,        color: "text-[var(--text-muted)]" },
];

export function TaskMonitorView() {
  const { t } = useTranslation();
  const [tasks, setTasks] = useState<AudioTask[]>([]);
  const [stats, setStats] = useState<TaskEngineStats>({ queued: 0, executing: 0, completed: 0, failed: 0, cancelled: 0, total_tokens: 0 });
  const [selectedTaskId, setSelectedTaskId] = useState<string | null>(null);
  const [executions, setExecutions] = useState<TaskExecution[]>([]);
  const [statusFilter, setStatusFilter] = useState<TaskStatus | "all">("all");
  const [loading, setLoading] = useState(false);
  // O-46: 排序
  const [sortField, setSortField] = useState<"created_at" | "status" | "task_type">("created_at");
  const [sortAsc, setSortAsc] = useState(false);
  // O-08: 并发/超时配置
  const [showConfig, setShowConfig] = useState(false);
  const [concurrency, setConcurrency] = useState(3);
  const [timeoutSecs, setTimeoutSecs] = useState(300);
  // O-47: 调试对话框
  const [debugTask, setDebugTask] = useState<AudioTask | null>(null);

  const selectedTask = tasks.find((t) => t.id === selectedTaskId);

  // O-46: 排序后的任务列表
  const sortedTasks = [...tasks].sort((a, b) => {
    const va = (a as any)[sortField] ?? "";
    const vb = (b as any)[sortField] ?? "";
    const cmp = va < vb ? -1 : va > vb ? 1 : 0;
    return sortAsc ? cmp : -cmp;
  });

  const loadData = useCallback(async () => {
    setLoading(true);
    try {
      const [taskList, engineStats] = await Promise.all([
        api.listTasks(statusFilter === "all" ? undefined : statusFilter, 200),
        api.getTaskEngineStats(),
      ]);
      setTasks(taskList);
      setStats(engineStats);
    } catch (err) {
      console.error("Failed to load tasks:", err);
    } finally {
      setLoading(false);
    }
  }, [statusFilter]);

  // O-48: 清理已完成/过期任务
  const handleCleanup = useCallback(async () => {
    try {
      await api.cleanupExpiredTasks(7);
      await loadData();
    } catch (err) { console.error("Cleanup failed:", err); }
  }, [loadData]);

  useEffect(() => { loadData(); }, [loadData]);

  // Listen for task events — O-02: 修复 listen() Promise 竞态条件
  useEffect(() => {
    let cancelled = false;
    let unlisten: (() => void) | null = null;
    api.onTaskEvent((_event: TaskEvent) => {
      loadData();
    }).then((fn) => {
      if (cancelled) { fn(); } // 组件已卸载，立即取消
      else { unlisten = fn; }
    });
    return () => {
      cancelled = true;
      unlisten?.();
    };
  }, [loadData]);

  const handleSelectTask = useCallback(async (taskId: string) => {
    setSelectedTaskId(taskId);
    try {
      const execs = await api.getTaskExecutions(taskId);
      setExecutions(execs);
    } catch (err) {
      console.error("Failed to load executions:", err);
    }
  }, []);

  const handleCancel = useCallback(async (taskId: string) => {
    try {
      await api.cancelTask(taskId);
      await loadData();
    } catch (err) {
      console.error("Failed to cancel:", err);
    }
  }, [loadData]);

  const handleRetry = useCallback(async (taskId: string) => {
    try {
      await api.retryTask(taskId);
      await loadData();
    } catch (err) {
      console.error("Failed to retry:", err);
    }
  }, [loadData]);

  return (
    <div className="flex h-full">
      {/* ── 左侧: 任务列表 ── */}
      <div className="flex-1 flex flex-col min-w-0">
        {/* 顶部工具栏 */}
        <div className="flex items-center gap-3 px-6 py-3 border-b border-[var(--border-subtle)]"
          style={{ backgroundColor: "var(--toolbar-bg)" }}>
          <h1 className="text-base font-semibold text-[var(--text-primary)] mr-4">{t("tasks.title")}</h1>
          <div className="flex-1" />
          {/* O-08: 并发/超时配置 */}
          <Button variant="ghost" size="sm" onClick={() => setShowConfig(!showConfig)}>
            <Settings2 size={14} /> 引擎配置
          </Button>
          {/* O-48: 清理过期任务 */}
          <Button variant="ghost" size="sm" onClick={handleCleanup}>
            <Trash2 size={14} /> 清理
          </Button>
          <Button variant="ghost" size="sm" onClick={loadData} disabled={loading}>
            {loading ? <Loader2 size={14} className="animate-spin" /> : <RefreshCw size={14} />}
            刷新
          </Button>
        </div>

        {/* O-08: 引擎配置面板 */}
        {showConfig && (
          <div className="px-6 py-3 border-b border-[var(--border-subtle)] bg-[var(--surface-1)] flex items-center gap-4">
            <label className="text-xs text-[var(--text-secondary)]">
              并发数
              <input type="number" min={1} max={16} value={concurrency}
                onChange={(e) => setConcurrency(Number(e.target.value))}
                className="ml-2 w-16 px-2 py-1 text-xs rounded border border-[var(--border-medium)] bg-[var(--input-bg)]" />
            </label>
            <label className="text-xs text-[var(--text-secondary)]">
              超时(秒)
              <input type="number" min={30} max={3600} step={30} value={timeoutSecs}
                onChange={(e) => setTimeoutSecs(Number(e.target.value))}
                className="ml-2 w-20 px-2 py-1 text-xs rounded border border-[var(--border-medium)] bg-[var(--input-bg)]" />
            </label>
            <Button variant="secondary" size="sm"
              onClick={async () => {
                try { await api.updateTaskEngineConfig(concurrency, timeoutSecs); } catch (err) { console.error(err); }
              }}>
              应用
            </Button>
          </div>
        )}

        {/* 统计卡片 */}
        <div className="px-6 pt-4 grid grid-cols-5 gap-2">
          {[
            { label: "排队", count: stats.queued, color: "text-[var(--text-muted)]", icon: <Clock size={14} /> },
            { label: "执行", count: stats.executing, color: "text-blue-500", icon: <Play size={14} /> },
            { label: "完成", count: stats.completed, color: "text-emerald-500", icon: <CheckCircle2 size={14} /> },
            { label: "失败", count: stats.failed, color: "text-red-500", icon: <AlertCircle size={14} /> },
            { label: "Token", count: stats.total_tokens, color: "text-amber-500", icon: <Zap size={14} /> },
          ].map((s) => (
            <GlassCard key={s.label} className="flex items-center gap-2 py-2 px-3">
              <div className={cn("shrink-0", s.color)}>{s.icon}</div>
              <div>
                <p className="text-sm font-bold text-[var(--text-primary)]">{s.count.toLocaleString()}</p>
                <p className="text-[10px] text-[var(--text-muted)]">{s.label}</p>
              </div>
            </GlassCard>
          ))}
        </div>

        {/* 状态过滤 */}
        <div className="px-6 pt-3 flex items-center gap-1">
          {STATUS_FILTERS.map((f) => (
            <button key={f.key} onClick={() => setStatusFilter(f.key)}
              className={cn(
                "flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-xs font-medium transition-all",
                statusFilter === f.key
                  ? "bg-brand-600/15 text-[var(--active-text)]"
                  : "text-[var(--text-muted)] hover:bg-[var(--hover-bg)]"
              )}>
              {f.icon} {f.label}
              {f.key !== "all" && (
                <span className="text-[10px] ml-0.5">
                  {f.key === "Queued" ? stats.queued
                  : f.key === "Executing" ? stats.executing
                  : f.key === "Completed" ? stats.completed
                  : f.key === "Failed" ? stats.failed
                  : stats.cancelled}
                </span>
              )}
            </button>
          ))}
        </div>

        {/* 任务列表 */}
        <ScrollArea className="flex-1 px-6 pt-3 pb-4">
          {/* O-46: 排序表头 */}
          <div className="flex items-center gap-2 pb-2 text-[10px] text-[var(--text-muted)]">
            {([["created_at", "时间"], ["status", "状态"], ["task_type", "类型"]] as const).map(([field, label]) => (
              <button key={field} className="flex items-center gap-0.5 hover:text-[var(--text-secondary)]"
                onClick={() => { if (sortField === field) setSortAsc(!sortAsc); else { setSortField(field); setSortAsc(false); } }}>
                <ArrowUpDown size={10} /> {label} {sortField === field && (sortAsc ? "↑" : "↓")}
              </button>
            ))}
          </div>
          {sortedTasks.length === 0 ? (
            <EmptyState icon={<ListChecks size={48} />} title="暂无任务" description="提交音频分析任务后在此监控" />
          ) : (
            <div className="space-y-1.5">
              {sortedTasks.map((task, i) => (
                <FadeIn key={task.id} delay={i * 0.02}>
                  <button
                    onClick={() => handleSelectTask(task.id)}
                    className={cn(
                      "w-full text-left rounded-lg border transition-all p-3",
                      selectedTaskId === task.id
                        ? "border-brand-500/50 bg-brand-600/5"
                        : "border-[var(--border-subtle)] hover:border-[var(--border-medium)] bg-[var(--card-bg)]"
                    )}
                  >
                    <div className="flex items-center gap-3">
                      <TaskStatusIcon status={task.status as TaskStatus} />
                      <div className="flex-1 min-w-0">
                        <div className="flex items-center gap-2">
                          <span className="text-xs font-medium text-[var(--text-primary)]">{task.stage}</span>
                          <Badge variant={task.task_type === "Transcription" ? "blue" : task.task_type === "TTS" ? "amber" : "gray"} className="text-[10px]">
                            {task.task_type}
                          </Badge>
                        </div>
                        <p className="text-[10px] text-[var(--text-muted)] mt-0.5 truncate">
                          {task.audio_item_id.slice(0, 8)} · 优先级 {task.priority} · 重试 {task.retry_count}/{task.max_retries}
                        </p>
                      </div>
                      {task.progress > 0 && task.progress < 1 && (
                        <div className="w-16">
                          <Progress value={task.progress * 100} className="h-1.5" />
                        </div>
                      )}
                      <div className="flex items-center gap-1">
                        {(task.status === "Queued" || task.status === "Executing") && (
                          <Button variant="ghost" size="icon" className="h-6 w-6" onClick={(e) => { e.stopPropagation(); handleCancel(task.id); }}>
                            <XCircle size={12} className="text-red-400" />
                          </Button>
                        )}
                        {task.status === "Failed" && (
                          <Button variant="ghost" size="icon" className="h-6 w-6" onClick={(e) => { e.stopPropagation(); handleRetry(task.id); }}>
                            <RotateCcw size={12} className="text-amber-400" />
                          </Button>
                        )}
                        {/* O-47: 调试查看 prompt/result */}
                        <Button variant="ghost" size="icon" className="h-6 w-6" title="调试详情"
                          onClick={(e) => { e.stopPropagation(); setDebugTask(task); }}>
                          <Eye size={12} />
                        </Button>
                      </div>
                    </div>
                  </button>
                </FadeIn>
              ))}
            </div>
          )}
        </ScrollArea>
      </div>

      {/* ── 右侧: 任务详情 ── */}
      <div className="w-[360px] border-l border-[var(--border-subtle)] flex flex-col shrink-0"
        style={{ backgroundColor: "var(--sidebar-bg)" }}>
        {selectedTask ? (
          <ScrollArea className="flex-1">
            <div className="p-5 space-y-4">
              <div className="flex items-center gap-3">
                <TaskStatusIcon status={selectedTask.status as TaskStatus} size={20} />
                <div className="flex-1">
                  <h3 className="text-sm font-semibold text-[var(--text-primary)]">{selectedTask.stage}</h3>
                  <Badge variant={selectedTask.status === "Completed" ? "green" : selectedTask.status === "Failed" ? "red" : selectedTask.status === "Executing" ? "blue" : "default"}>
                    {selectedTask.status}
                  </Badge>
                </div>
              </div>

              <Separator />

              {/* Detail fields */}
              <div className="space-y-2 text-xs">
                <DetailRow label="任务 ID" value={selectedTask.id.slice(0, 12) + "..."} />
                <DetailRow label="音频 ID" value={selectedTask.audio_item_id.slice(0, 12) + "..."} />
                <DetailRow label="类型" value={selectedTask.task_type} />
                <DetailRow label="阶段" value={selectedTask.stage} />
                <DetailRow label="优先级" value={selectedTask.priority.toString()} />
                <DetailRow label="重试" value={`${selectedTask.retry_count} / ${selectedTask.max_retries}`} />
                <DetailRow label="提交时间" value={formatTime(selectedTask.submitted_at)} />
                {selectedTask.started_at && <DetailRow label="开始时间" value={formatTime(selectedTask.started_at)} />}
                {selectedTask.completed_at && <DetailRow label="完成时间" value={formatTime(selectedTask.completed_at)} />}
              </div>

              {selectedTask.error && (
                <>
                  <Separator />
                  <div>
                    <p className="text-xs font-medium text-red-400 mb-1">错误信息</p>
                    <GlassCard className="p-3 border-red-500/20">
                      <p className="text-xs text-red-300 break-all">{selectedTask.error}</p>
                    </GlassCard>
                  </div>
                </>
              )}

              <Separator />

              {/* Execution history */}
              <div>
                <p className="text-xs font-medium text-[var(--text-secondary)] mb-2">执行历史</p>
                {executions.length === 0 ? (
                  <p className="text-xs text-[var(--text-muted)]">暂无执行记录</p>
                ) : (
                  <div className="space-y-2">
                    {executions.map((exec) => (
                      <GlassCard key={exec.id} className="p-3 space-y-1">
                        <div className="flex items-center gap-2">
                          <Badge variant={exec.status === "Completed" ? "green" : exec.status === "Failed" ? "red" : "default"} className="text-[10px]">
                            #{exec.attempt} {exec.status}
                          </Badge>
                          {exec.duration_ms && (
                            <span className="text-[10px] text-[var(--text-muted)]">{(exec.duration_ms / 1000).toFixed(1)}s</span>
                          )}
                        </div>
                        {(exec.prompt_tokens || exec.completion_tokens) && (
                          <p className="text-[10px] text-[var(--text-muted)]">
                            {exec.prompt_tokens && `↑${exec.prompt_tokens}`} {exec.completion_tokens && `↓${exec.completion_tokens}`} tokens
                          </p>
                        )}
                        {exec.error && <p className="text-[10px] text-red-400 break-all">{exec.error}</p>}
                      </GlassCard>
                    ))}
                  </div>
                )}
              </div>

              {/* Actions */}
              <div className="flex gap-2 pt-2">
                {selectedTask.status === "Failed" && (
                  <Button size="sm" onClick={() => handleRetry(selectedTask.id)}>
                    <RotateCcw size={12} /> 重试
                  </Button>
                )}
                {(selectedTask.status === "Queued" || selectedTask.status === "Executing") && (
                  <Button variant="danger" size="sm" onClick={() => handleCancel(selectedTask.id)}>
                    <XCircle size={12} /> 取消
                  </Button>
                )}
              </div>
            </div>
          </ScrollArea>
        ) : (
          <div className="flex-1 flex items-center justify-center">
            <div className="text-center">
              <Eye size={32} className="text-[var(--text-muted)] mx-auto mb-2" />
              <p className="text-xs text-[var(--text-muted)]">选择任务查看详情</p>
            </div>
          </div>
        )}
      </div>

      {/* O-47: 调试对话框 — 显示 prompt_text / result_text */}
      {debugTask && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50" onClick={() => setDebugTask(null)}>
          <div className="bg-[var(--card-bg)] border border-[var(--border-medium)] rounded-xl shadow-2xl w-[640px] max-h-[80vh] overflow-auto p-5"
            onClick={(e) => e.stopPropagation()}>
            <div className="flex items-center justify-between mb-3">
              <h3 className="text-sm font-semibold text-[var(--text-primary)]">任务调试 — {debugTask.id.slice(0, 8)}</h3>
              <Button variant="ghost" size="icon" className="h-6 w-6" onClick={() => setDebugTask(null)}>
                <XCircle size={14} />
              </Button>
            </div>
            <div className="space-y-3">
              <div>
                <p className="text-[10px] text-[var(--text-muted)] mb-1">Prompt Text</p>
                <pre className="text-xs bg-[var(--surface-1)] rounded p-3 whitespace-pre-wrap max-h-48 overflow-auto font-mono">
                  {debugTask.prompt_text || "(空)"}
                </pre>
              </div>
              <div>
                <p className="text-[10px] text-[var(--text-muted)] mb-1">Result Text</p>
                <pre className="text-xs bg-[var(--surface-1)] rounded p-3 whitespace-pre-wrap max-h-48 overflow-auto font-mono">
                  {debugTask.result_text || "(空)"}
                </pre>
              </div>
              <div>
                <p className="text-[10px] text-[var(--text-muted)] mb-1">Error Details</p>
                <pre className="text-xs bg-[var(--surface-1)] rounded p-3 whitespace-pre-wrap max-h-32 overflow-auto font-mono text-red-400">
                  {debugTask.error || "(无错误)"}
                </pre>
              </div>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

// ── 子组件 ──

function TaskStatusIcon({ status, size = 16 }: { status: TaskStatus; size?: number }) {
  switch (status) {
    case "Queued": return <Clock size={size} className="text-[var(--text-muted)]" />;
    case "Executing": return <Loader2 size={size} className="text-blue-400 animate-spin" />;
    case "Completed": return <CheckCircle2 size={size} className="text-emerald-400" />;
    case "Failed": return <AlertCircle size={size} className="text-red-400" />;
    case "Cancelled": return <Ban size={size} className="text-[var(--text-muted)]" />;
    default: return <Clock size={size} className="text-[var(--text-muted)]" />;
  }
}

function DetailRow({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex justify-between">
      <span className="text-[var(--text-muted)]">{label}</span>
      <span className="text-[var(--text-secondary)] font-mono">{value}</span>
    </div>
  );
}

function formatTime(iso: string): string {
  try {
    return new Date(iso).toLocaleString("zh-CN", { month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit", second: "2-digit" });
  } catch { return iso; }
}
