import { useState } from "react";
import { useTranslation } from "react-i18next";
import {
  CheckCircle, AlertCircle, Clock, Play, Trash2,
  RefreshCw, XCircle,
} from "lucide-react";
import { cn } from "../lib/utils";
import {
  Button, GlassCard, Badge, Progress, FadeIn,
} from "../components/ui";

interface TaskItem {
  id: string;
  name: string;
  type: string;
  status: "pending" | "running" | "completed" | "failed" | "cancelled";
  progress: number;
  startedAt: string;
  duration: string;
  error?: string;
}

const MOCK_TASKS: TaskItem[] = [
  { id: "t1", name: "字幕翻译 - 产品演示.srt", type: "翻译", status: "completed", progress: 1.0, startedAt: "14:20:01", duration: "2m 34s" },
  { id: "t2", name: "图片生成 - 赛博朋克上海", type: "图片", status: "running", progress: 0.45, startedAt: "14:32:10", duration: "0m 48s" },
  { id: "t3", name: "音频转写 - 季度会议录音", type: "转写", status: "running", progress: 0.72, startedAt: "14:25:00", duration: "4m 12s" },
  { id: "t4", name: "批量翻译 - 文档包 A", type: "翻译", status: "pending", progress: 0, startedAt: "-", duration: "-" },
  { id: "t5", name: "TTS - 培训语音合成", type: "TTS", status: "failed", progress: 0.15, startedAt: "14:10:00", duration: "0m 22s", error: "TTS Provider 未配置" },
  { id: "t6", name: "视频生成 - 产品广告", type: "视频", status: "cancelled", progress: 0.05, startedAt: "13:50:00", duration: "1m 05s" },
];

const STATUS_CONFIG: Record<string, { label: string; variant: "gray" | "blue" | "green" | "red"; icon: React.ReactNode }> = {
  pending: { label: "等待", variant: "gray", icon: <Clock size={12} /> },
  running: { label: "运行", variant: "blue", icon: <Play size={12} /> },
  completed: { label: "完成", variant: "green", icon: <CheckCircle size={12} /> },
  failed: { label: "失败", variant: "red", icon: <AlertCircle size={12} /> },
  cancelled: { label: "取消", variant: "gray", icon: <XCircle size={12} /> },
};

export function TaskMonitorView() {
  const { t } = useTranslation();
  const [tasks] = useState(MOCK_TASKS);

  const running = tasks.filter((t) => t.status === "running").length;
  const completed = tasks.filter((t) => t.status === "completed").length;
  const failed = tasks.filter((t) => t.status === "failed").length;
  const pending = tasks.filter((t) => t.status === "pending").length;

  return (
    <div className="flex flex-col h-full">
      {/* 顶部 */}
      <div className="flex items-center gap-3 px-6 py-3 border-b border-white/[0.06] bg-white/[0.02]">
        <h1 className="text-base font-semibold text-slate-100 mr-4">{t("tasks.title")}</h1>
        <div className="flex-1" />
        <Button variant="ghost" size="sm"><RefreshCw size={14} /> {t("tasks.refresh")}</Button>
      </div>

      {/* 统计卡片 */}
      <div className="grid grid-cols-4 gap-4 px-6 py-4">
        <StatCard label={t("tasks.runningCount")} value={running} accent="brand" />
        <StatCard label={t("tasks.pendingCount")} value={pending} accent="slate" />
        <StatCard label={t("tasks.completedCount")} value={completed} accent="emerald" />
        <StatCard label={t("tasks.failedCount")} value={failed} accent="red" />
      </div>

      {/* 任务表格 */}
      <div className="flex-1 overflow-y-auto px-6 pb-6">
        <GlassCard className="p-0 overflow-hidden">
          <table className="w-full text-sm">
            <thead>
              <tr className="bg-white/[0.03] text-slate-500 text-left">
                <th className="px-4 py-2.5 font-medium">{t("tasks.task")}</th>
                <th className="px-4 py-2.5 font-medium w-24">{t("tasks.type")}</th>
                <th className="px-4 py-2.5 font-medium w-24">{t("tasks.status")}</th>
                <th className="px-4 py-2.5 font-medium w-40">{t("tasks.progress")}</th>
                <th className="px-4 py-2.5 font-medium w-24">{t("tasks.startTime")}</th>
                <th className="px-4 py-2.5 font-medium w-24">{t("tasks.duration")}</th>
                <th className="px-4 py-2.5 font-medium w-20">{t("tasks.actions")}</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-white/[0.04]">
              {tasks.map((task) => {
                const cfg = STATUS_CONFIG[task.status];
                return (
                  <tr key={task.id} className="hover:bg-white/[0.02] transition-colors">
                    <td className="px-4 py-3">
                      <span className="text-slate-200">{task.name}</span>
                      {task.error && <p className="text-xs text-red-400 mt-0.5">{task.error}</p>}
                    </td>
                    <td className="px-4 py-3"><Badge variant="gray">{task.type}</Badge></td>
                    <td className="px-4 py-3">
                      <Badge variant={cfg.variant}>
                        {cfg.icon}<span className="ml-0.5">{cfg.label}</span>
                      </Badge>
                    </td>
                    <td className="px-4 py-3">
                      {task.status === "running" || task.status === "failed" ? (
                        <div className="space-y-1">
                          <Progress
                            value={task.progress * 100}
                            indicatorClassName={task.status === "failed" ? "bg-red-500" : undefined}
                          />
                          <span className="text-xs text-slate-500">{Math.round(task.progress * 100)}%</span>
                        </div>
                      ) : task.status === "completed" ? (
                        <span className="text-xs text-emerald-400">100%</span>
                      ) : (
                        <span className="text-xs text-slate-600">-</span>
                      )}
                    </td>
                    <td className="px-4 py-3 text-xs text-slate-500 font-mono">{task.startedAt}</td>
                    <td className="px-4 py-3 text-xs text-slate-500">{task.duration}</td>
                    <td className="px-4 py-3">
                      <Button variant="ghost" size="icon" className="h-6 w-6 text-red-400 hover:text-red-300">
                        <Trash2 size={12} />
                      </Button>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </GlassCard>
      </div>
    </div>
  );
}

function StatCard({ label, value, accent }: { label: string; value: number; accent: string }) {
  return (
    <FadeIn>
      <GlassCard className="py-3 px-4">
        <p className="text-xs text-slate-500 mb-0.5">{label}</p>
        <p className={cn("text-2xl font-bold tabular-nums", `text-${accent}-400`)}>{value}</p>
      </GlassCard>
    </FadeIn>
  );
}
