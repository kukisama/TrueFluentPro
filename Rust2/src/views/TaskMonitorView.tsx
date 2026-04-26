import { useState, useEffect } from "react";
import { useTranslation } from "react-i18next";
import {
  ListChecks, RefreshCw, CheckCircle, AlertCircle,
  Clock, Play, Loader2,
} from "lucide-react";
import { cn } from "../lib/utils";
import {
  Button, GlassCard, Badge, Progress, FadeIn, EmptyState,
} from "../components/ui";
import { useAppStore } from "../stores/app-store";
import { api } from "../lib/tauri-api";

export function TaskMonitorView() {
  const { t } = useTranslation();
  const batchTasks = useAppStore((s) => s.batchTasks);
  const setBatchTasks = useAppStore((s) => s.setBatchTasks);
  const [loading, setLoading] = useState(false);

  const loadTasks = async () => {
    setLoading(true);
    try {
      const tasks = await api.getBatchTasks(100);
      setBatchTasks(tasks);
    } catch { /* ignore */ }
    finally { setLoading(false); }
  };

  useEffect(() => { loadTasks(); }, []);

  const statusCounts = {
    running: batchTasks.filter((t) => t.status === "running").length,
    pending: batchTasks.filter((t) => t.status === "pending").length,
    completed: batchTasks.filter((t) => t.status === "completed").length,
    failed: batchTasks.filter((t) => t.status === "failed").length,
  };

  return (
    <div className="flex flex-col h-full">
      {/* 顶部 */}
      <div className="flex items-center gap-3 px-6 py-3 border-b border-[var(--border-subtle)]"
        style={{ backgroundColor: "var(--toolbar-bg)" }}>
        <h1 className="text-base font-semibold text-[var(--text-primary)] mr-4">{t("tasks.title")}</h1>
        <div className="flex-1" />
        <Button variant="ghost" size="sm" onClick={loadTasks} disabled={loading}>
          {loading ? <Loader2 size={14} className="animate-spin" /> : <RefreshCw size={14} />}
          {t("tasks.refresh")}
        </Button>
      </div>

      {/* 统计 */}
      <div className="px-6 pt-4 grid grid-cols-4 gap-3">
        {[
          { label: t("tasks.runningCount"), count: statusCounts.running, color: "text-blue-500", icon: <Play size={16} /> },
          { label: t("tasks.pendingCount"), count: statusCounts.pending, color: "text-[var(--text-muted)]", icon: <Clock size={16} /> },
          { label: t("tasks.completedCount"), count: statusCounts.completed, color: "text-emerald-500", icon: <CheckCircle size={16} /> },
          { label: t("tasks.failedCount"), count: statusCounts.failed, color: "text-red-500", icon: <AlertCircle size={16} /> },
        ].map((s) => (
          <GlassCard key={s.label} className="flex items-center gap-3 py-3">
            <div className={cn("shrink-0", s.color)}>{s.icon}</div>
            <div>
              <p className="text-xl font-bold text-[var(--text-primary)]">{s.count}</p>
              <p className="text-xs text-[var(--text-muted)]">{s.label}</p>
            </div>
          </GlassCard>
        ))}
      </div>

      {/* 任务列表 */}
      <div className="flex-1 overflow-y-auto p-6 pt-4">
        {batchTasks.length === 0 ? (
          <EmptyState
            icon={<ListChecks size={48} />}
            title="暂无任务"
            description="创建批量任务后在此监控进度"
          />
        ) : (
          <div className="space-y-2">
            {batchTasks.map((task, i) => (
              <FadeIn key={task.id} delay={i * 0.03}>
                <GlassCard className="py-3">
                  <div className="flex items-center gap-4">
                    <div className="flex-1 min-w-0">
                      <div className="flex items-center gap-2">
                        <span className="text-sm font-medium text-[var(--text-primary)] truncate">{task.name}</span>
                        <Badge variant={
                          task.status === "completed" ? "green"
                          : task.status === "running" ? "blue"
                          : task.status === "failed" ? "red"
                          : "gray"
                        }>{task.status}</Badge>
                        <Badge variant="gray">{task.task_type}</Badge>
                      </div>
                      <p className="text-xs text-[var(--text-muted)] mt-0.5">{task.created_at}</p>
                    </div>
                    {task.progress > 0 && task.progress < 1 && (
                      <div className="w-32">
                        <Progress value={task.progress * 100} />
                      </div>
                    )}
                  </div>
                  {task.error && <p className="text-xs text-red-400 mt-1">{task.error}</p>}
                </GlassCard>
              </FadeIn>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
