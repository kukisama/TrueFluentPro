import { useState, useEffect, useCallback } from "react";
import { useTranslation } from "react-i18next";
import {
  Play, Square, RefreshCw, Pause, RotateCcw, Trash2, Undo2,
  ChevronDown, ChevronRight, Package, AlertCircle, CheckCircle2,
  Clock, Loader2,
} from "lucide-react";
import { cn } from "../lib/utils";
import { Button } from "../components/ui";
import { api } from "../lib/api";
import type { BatchPackage, BatchSubtaskView, BatchBucketNav, BatchPackageState } from "../lib/types";
import type { UnlistenFn } from "@tauri-apps/api/event";

const STATE_COLORS: Record<BatchPackageState, string> = {
  pending: "bg-yellow-500/20 text-yellow-400",
  running: "bg-blue-500/20 text-blue-400",
  partial: "bg-orange-500/20 text-orange-400",
  completed: "bg-green-500/20 text-green-400",
  failed: "bg-red-500/20 text-red-400",
  removed: "bg-gray-500/20 text-gray-400",
};

const STATE_ICONS: Record<BatchPackageState, React.ReactNode> = {
  pending: <Clock size={14} />,
  running: <Loader2 size={14} className="animate-spin" />,
  partial: <AlertCircle size={14} />,
  completed: <CheckCircle2 size={14} />,
  failed: <AlertCircle size={14} />,
  removed: <Trash2 size={14} />,
};

function StateBadge({ state }: { state: BatchPackageState }) {
  return (
    <span className={cn("inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium", STATE_COLORS[state])}>
      {STATE_ICONS[state]}
      {state}
    </span>
  );
}

function ProgressBar({ progress }: { progress: number }) {
  const pct = Math.round(progress * 100);
  return (
    <div className="w-full h-1.5 bg-[var(--surface-2)] rounded-full overflow-hidden">
      <div
        className="h-full bg-[var(--accent)] transition-all duration-300 rounded-full"
        style={{ width: `${pct}%` }}
      />
    </div>
  );
}

function SubtaskRow({ subtask }: { subtask: BatchSubtaskView }) {
  return (
    <div className="flex items-center gap-3 px-4 py-2 text-sm border-t border-[var(--border-subtle)]">
      <span className="w-24 text-[var(--text-muted)] truncate">{subtask.tag}</span>
      <span className="flex-1 truncate">{subtask.title}</span>
      <StateBadge state={subtask.state} />
      <span className="w-20 text-right text-[var(--text-muted)]">{subtask.status_text}</span>
      <div className="w-24">
        <ProgressBar progress={subtask.progress} />
      </div>
    </div>
  );
}

function PackageCard({
  pkg,
  isExpanded,
  onToggle,
  subtasks,
  onAction,
}: {
  pkg: BatchPackage;
  isExpanded: boolean;
  onToggle: () => void;
  subtasks: BatchSubtaskView[];
  onAction: (action: string, id: string) => void;
}) {
  const pct = Math.round(pkg.progress * 100);

  return (
    <div className="rounded-xl border border-[var(--border-subtle)] bg-[var(--surface-1)] overflow-hidden">
      {/* Header */}
      <div className="flex items-center gap-3 px-4 py-3 cursor-pointer hover:bg-[var(--hover-bg)] transition-colors" onClick={onToggle}>
        <button className="shrink-0 text-[var(--text-muted)]">
          {isExpanded ? <ChevronDown size={16} /> : <ChevronRight size={16} />}
        </button>
        <Package size={16} className="shrink-0 text-[var(--text-muted)]" />
        <span className="font-medium truncate flex-1">{pkg.display_name}</span>
        <StateBadge state={pkg.state} />
        <span className="text-xs text-[var(--text-muted)] w-16 text-right">
          {pkg.completed_count}/{pkg.total_count}
        </span>
        <span className="text-xs text-[var(--text-muted)] w-12 text-right">{pct}%</span>
      </div>

      {/* Progress bar */}
      <div className="px-4 pb-2">
        <ProgressBar progress={pkg.progress} />
      </div>

      {/* Actions */}
      <div className="flex items-center gap-1 px-4 pb-3">
        {pkg.state === "running" && !pkg.is_paused && (
          <Button variant="ghost" size="sm" onClick={() => onAction("pause", pkg.id)}>
            <Pause size={14} className="mr-1" /> Pause
          </Button>
        )}
        {pkg.is_paused && (
          <Button variant="ghost" size="sm" onClick={() => onAction("resume", pkg.id)}>
            <Play size={14} className="mr-1" /> Resume
          </Button>
        )}
        {!pkg.is_removed && (
          <Button variant="ghost" size="sm" onClick={() => onAction("remove", pkg.id)}>
            <Trash2 size={14} className="mr-1" /> Remove
          </Button>
        )}
        {pkg.is_removed && (
          <Button variant="ghost" size="sm" onClick={() => onAction("restore", pkg.id)}>
            <Undo2 size={14} className="mr-1" /> Restore
          </Button>
        )}
        {(pkg.state === "failed" || pkg.state === "partial") && (
          <Button variant="ghost" size="sm" onClick={() => onAction("regenerate", pkg.id)}>
            <RotateCcw size={14} className="mr-1" /> Retry
          </Button>
        )}
      </div>

      {/* Subtasks */}
      {isExpanded && subtasks.length > 0 && (
        <div className="border-t border-[var(--border-subtle)] bg-[var(--surface-0)]">
          {subtasks.map((st, i) => (
            <SubtaskRow key={i} subtask={st} />
          ))}
        </div>
      )}
      {isExpanded && subtasks.length === 0 && (
        <div className="px-4 py-3 text-sm text-[var(--text-muted)] border-t border-[var(--border-subtle)] bg-[var(--surface-0)]">
          No subtasks
        </div>
      )}
    </div>
  );
}

export function BatchProcessingView() {
  const { t } = useTranslation();
  const [buckets, setBuckets] = useState<BatchBucketNav[]>([]);
  const [activeBucket, setActiveBucket] = useState("pending");
  const [packages, setPackages] = useState<BatchPackage[]>([]);
  const [expandedPkgs, setExpandedPkgs] = useState<Set<string>>(new Set());
  const [subtasksMap, setSubtasksMap] = useState<Record<string, BatchSubtaskView[]>>({});
  const [loading, setLoading] = useState(false);

  const loadBuckets = useCallback(async () => {
    try {
      const nav = await api.batchGetBucketNav();
      setBuckets(nav);
    } catch (e) {
      console.error("Failed to load bucket nav:", e);
    }
  }, []);

  const loadPackages = useCallback(async (bucket: string) => {
    setLoading(true);
    try {
      const pkgs = await api.batchGetPackages(bucket);
      setPackages(pkgs);
    } catch (e) {
      console.error("Failed to load packages:", e);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    loadBuckets();
    loadPackages(activeBucket);
  }, [loadBuckets, loadPackages, activeBucket]);

  // Listen for batch updates
  useEffect(() => {
    let unlisten: UnlistenFn | null = null;
    api.onBatchPackageUpdate(() => {
      loadBuckets();
      loadPackages(activeBucket);
    }).then((fn) => { unlisten = fn; });
    return () => { unlisten?.(); };
  }, [activeBucket, loadBuckets, loadPackages]);

  const toggleExpand = async (pkgId: string) => {
    const next = new Set(expandedPkgs);
    if (next.has(pkgId)) {
      next.delete(pkgId);
    } else {
      next.add(pkgId);
      if (!subtasksMap[pkgId]) {
        try {
          const st = await api.batchGetSubtasks(pkgId);
          setSubtasksMap((prev) => ({ ...prev, [pkgId]: st }));
        } catch (e) {
          console.error("Failed to load subtasks:", e);
        }
      }
    }
    setExpandedPkgs(next);
  };

  const handleAction = async (action: string, id: string) => {
    try {
      switch (action) {
        case "pause": await api.batchPausePackage(id); break;
        case "resume": await api.batchResumePackage(id); break;
        case "remove": await api.batchRemovePackage(id); break;
        case "restore": await api.batchRestorePackage(id); break;
        case "regenerate": await api.batchRegeneratePackage(id); break;
      }
      loadBuckets();
      loadPackages(activeBucket);
    } catch (e) {
      console.error(`Batch action ${action} failed:`, e);
    }
  };

  const handleStartBatch = async () => {
    const pendingIds = packages.filter((p) => p.state === "pending").map((p) => p.id);
    if (pendingIds.length === 0) return;
    try {
      await api.batchStart(pendingIds, true);
      loadBuckets();
      loadPackages(activeBucket);
    } catch (e) {
      console.error("Start batch failed:", e);
    }
  };

  const handleStopBatch = async () => {
    const runningIds = packages.filter((p) => p.state === "running").map((p) => p.id);
    if (runningIds.length === 0) return;
    try {
      await api.batchStop(runningIds);
      loadBuckets();
      loadPackages(activeBucket);
    } catch (e) {
      console.error("Stop batch failed:", e);
    }
  };

  const handleRefresh = () => {
    loadBuckets();
    loadPackages(activeBucket);
  };

  const selectBucket = (key: string) => {
    setActiveBucket(key);
    setExpandedPkgs(new Set());
    setSubtasksMap({});
  };

  // Default bucket labels
  const BUCKET_LABELS: Record<string, string> = {
    pending: t("batch.pending", "Pending"),
    running: t("batch.running", "In Progress"),
    completed: t("batch.completed", "Completed"),
    failed: t("batch.failed", "Failed"),
    removed: t("batch.removed", "Removed"),
  };

  return (
    <div className="flex h-full">
      {/* Sidebar: 5 bucket navigation */}
      <aside className="w-48 shrink-0 border-r border-[var(--border-subtle)] bg-[var(--surface-0)] flex flex-col">
        <div className="px-3 py-3 text-sm font-semibold text-[var(--text-primary)]">
          {t("nav.batchProcessing", "Batch Processing")}
        </div>
        <nav className="flex-1 px-2 space-y-0.5">
          {buckets.map((b) => (
            <button
              key={b.key}
              onClick={() => selectBucket(b.key)}
              className={cn(
                "flex items-center justify-between w-full px-3 py-2 rounded-lg text-sm transition-colors",
                activeBucket === b.key
                  ? "bg-[var(--active-bg)] text-[var(--active-text)]"
                  : "text-[var(--text-muted)] hover:bg-[var(--hover-bg)]",
              )}
            >
              <span>{BUCKET_LABELS[b.key] || b.title}</span>
              {b.count > 0 && (
                <span className="ml-1 px-1.5 py-0.5 text-xs rounded-full bg-[var(--surface-2)]">
                  {b.count}
                </span>
              )}
            </button>
          ))}
          {buckets.length === 0 && (
            <>
              {["pending", "running", "completed", "failed", "removed"].map((key) => (
                <button
                  key={key}
                  onClick={() => selectBucket(key)}
                  className={cn(
                    "flex items-center justify-between w-full px-3 py-2 rounded-lg text-sm transition-colors",
                    activeBucket === key
                      ? "bg-[var(--active-bg)] text-[var(--active-text)]"
                      : "text-[var(--text-muted)] hover:bg-[var(--hover-bg)]",
                  )}
                >
                  <span>{BUCKET_LABELS[key]}</span>
                  <span className="ml-1 px-1.5 py-0.5 text-xs rounded-full bg-[var(--surface-2)]">0</span>
                </button>
              ))}
            </>
          )}
        </nav>
      </aside>

      {/* Main content */}
      <div className="flex-1 flex flex-col min-w-0">
        {/* Toolbar */}
        <div className="flex items-center gap-2 px-4 py-3 border-b border-[var(--border-subtle)]">
          <Button variant="primary" size="sm" onClick={handleStartBatch}>
            <Play size={14} className="mr-1" /> {t("batch.start", "Start")}
          </Button>
          <Button variant="ghost" size="sm" onClick={handleStopBatch}>
            <Square size={14} className="mr-1" /> {t("batch.stop", "Stop")}
          </Button>
          <div className="flex-1" />
          <Button variant="ghost" size="icon" onClick={handleRefresh}>
            <RefreshCw size={16} />
          </Button>
        </div>

        {/* Package list */}
        <div className="flex-1 overflow-y-auto p-4 space-y-3">
          {loading && packages.length === 0 && (
            <div className="flex items-center justify-center py-12 text-[var(--text-muted)]">
              <Loader2 size={20} className="animate-spin mr-2" /> Loading…
            </div>
          )}
          {!loading && packages.length === 0 && (
            <div className="flex flex-col items-center justify-center py-12 text-[var(--text-muted)]">
              <Package size={40} className="mb-3 opacity-40" />
              <p className="text-sm">{t("batch.empty", "No packages in this bucket")}</p>
            </div>
          )}
          {packages.map((pkg) => (
            <PackageCard
              key={pkg.id}
              pkg={pkg}
              isExpanded={expandedPkgs.has(pkg.id)}
              onToggle={() => toggleExpand(pkg.id)}
              subtasks={subtasksMap[pkg.id] || []}
              onAction={handleAction}
            />
          ))}
        </div>
      </div>
    </div>
  );
}
