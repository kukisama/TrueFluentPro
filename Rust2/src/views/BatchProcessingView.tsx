import { useState } from "react";
import { useTranslation } from "react-i18next";
import {
  FileText, CheckCircle, AlertCircle, Clock, Play,
  Plus, ChevronDown, Upload,
} from "lucide-react";
import {
  Button, GlassCard, Select, Badge, Progress,
  FadeIn, EmptyState,
} from "../components/ui";

type TaskStatus = "pending" | "running" | "completed" | "failed";

interface MockTask {
  id: string;
  name: string;
  status: TaskStatus;
  type: string;
  progress: number;
  fileCount: number;
}

const MOCK_TASKS: MockTask[] = [
  { id: "1", name: "产品演示字幕翻译", status: "completed", type: "字幕翻译", progress: 1.0, fileCount: 12 },
  { id: "2", name: "季度会议录音转写", status: "running", type: "音频转写", progress: 0.65, fileCount: 3 },
  { id: "3", name: "技术文档批量翻译", status: "pending", type: "文本翻译", progress: 0, fileCount: 28 },
  { id: "4", name: "培训视频字幕", status: "failed", type: "字幕翻译", progress: 0.32, fileCount: 5 },
];

const STATUS_MAP: Record<TaskStatus, { label: string; variant: "gray" | "blue" | "green" | "red"; icon: React.ReactNode }> = {
  pending: { label: "等待中", variant: "gray", icon: <Clock size={12} /> },
  running: { label: "运行中", variant: "blue", icon: <Play size={12} /> },
  completed: { label: "已完成", variant: "green", icon: <CheckCircle size={12} /> },
  failed: { label: "失败", variant: "red", icon: <AlertCircle size={12} /> },
};

export function BatchProcessingView() {
  const { t } = useTranslation();
  const [tasks] = useState(MOCK_TASKS);
  const [filterStatus, setFilterStatus] = useState<TaskStatus | "all">("all");
  const filtered = filterStatus === "all" ? tasks : tasks.filter((t) => t.status === filterStatus);

  return (
    <div className="flex flex-col h-full">
      <div className="flex items-center gap-3 px-6 py-3 border-b border-[var(--border-subtle)]"
        style={{ backgroundColor: "var(--toolbar-bg)" }}>
        <h1 className="text-base font-semibold text-[var(--text-primary)] mr-4">{t("batch.title")}</h1>
        <div className="relative">
          <Select value={filterStatus} onChange={(e) => setFilterStatus(e.target.value as TaskStatus | "all")} className="w-28">
            <option value="all">{t("batch.all")}</option>
            <option value="running">{t("batch.running")}</option>
            <option value="pending">{t("batch.pending")}</option>
            <option value="completed">{t("batch.completed")}</option>
            <option value="failed">{t("batch.failed")}</option>
          </Select>
          <ChevronDown size={14} className="absolute right-2 top-1/2 -translate-y-1/2 text-[var(--text-muted)] pointer-events-none" />
        </div>
        <div className="flex-1" />
        <Button size="sm"><Plus size={14} /> {t("batch.newTask")}</Button>
      </div>

      <div className="px-6 pt-4">
        <GlassCard className="border-dashed flex flex-col items-center py-6 cursor-pointer hover:border-brand-500/30 transition-colors">
          <Upload size={24} className="text-[var(--text-muted)] mb-2" />
          <p className="text-sm text-[var(--text-secondary)]">{t("batch.dragHint")}</p>
          <p className="text-xs text-[var(--text-muted)] mt-1">{t("batch.dragSubHint")}</p>
        </GlassCard>
      </div>

      <div className="flex-1 overflow-y-auto p-6 pt-4">
        {filtered.length === 0 ? (
          <EmptyState icon={<FileText size={48} />} title={t("batch.noTasks")} description={t("batch.noTasksHint")} />
        ) : (
          <div className="space-y-3">
            {filtered.map((task, i) => {
              const status = STATUS_MAP[task.status];
              return (
                <FadeIn key={task.id} delay={i * 0.05}>
                  <GlassCard className="hover:border-[var(--border-medium)] transition-colors">
                    <div className="flex items-center gap-4">
                      <div className="flex-1 min-w-0">
                        <div className="flex items-center gap-2 mb-1.5">
                          <span className="font-medium text-[var(--text-primary)] truncate">{task.name}</span>
                          <Badge variant={status.variant}>{status.icon}<span className="ml-0.5">{status.label}</span></Badge>
                          <Badge variant="gray">{task.type}</Badge>
                        </div>
                        <p className="text-xs text-[var(--text-muted)]">{task.fileCount} {t("batch.files")}</p>
                      </div>
                      {task.status === "running" && (
                        <div className="w-36 space-y-1">
                          <Progress value={task.progress * 100} />
                          <span className="text-xs text-[var(--text-muted)]">{Math.round(task.progress * 100)}%</span>
                        </div>
                      )}
                    </div>
                  </GlassCard>
                </FadeIn>
              );
            })}
          </div>
        )}
      </div>
    </div>
  );
}
