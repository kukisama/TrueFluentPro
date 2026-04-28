import { useState, useEffect, useRef } from "react";
import { useTranslation } from "react-i18next";
import {
  Plus, Trash2, Edit2, Globe, TestTube, Loader2,
  ChevronRight, CheckCircle2, XCircle, SkipForward,
} from "lucide-react";
import { cn } from "../../lib/utils";
import {
  Button, GlassCard, Badge, FadeIn, EmptyState, SectionHeader,
} from "../../components/ui";
import { useAppStore } from "../../stores/app-store";
import { api } from "../../lib/api";
import type {
  EndpointTestReport, EndpointTestProgress, EndpointTestItem,
} from "../../lib/types";
import {
  VENDOR_BADGE, CAPABILITY_ICONS, CAPABILITY_COLORS,
} from "../SettingsView";
import EndpointForm from "./EndpointForm";
import EndpointEditPanel from "./EndpointEditPanel";

// ── Test report helpers ──

function TestReportSummaryBadges({ items }: { items: EndpointTestItem[] }) {
  const success = items.filter((i) => i.status === "success").length;
  const failed = items.filter((i) => i.status === "failed").length;
  const skipped = items.filter((i) => i.status === "skipped").length;
  return (
    <span className="flex items-center gap-1.5 ml-2">
      {success > 0 && <span className="text-emerald-500 text-[10px] font-medium">✓{success}</span>}
      {failed > 0 && <span className="text-red-400 text-[10px] font-medium">✗{failed}</span>}
      {skipped > 0 && <span className="text-[var(--text-muted)] text-[10px] font-medium">⏭{skipped}</span>}
    </span>
  );
}

const STATUS_CONFIG: Record<string, { icon: React.ReactNode; bg: string; text: string; labelKey: string }> = {
  pending: { icon: <div className="w-3 h-3 rounded-full border border-[var(--text-muted)] shrink-0" />, bg: "bg-[var(--surface-2)]", text: "text-[var(--text-muted)]", labelKey: "endpointForm.pending" },
  running: { icon: <Loader2 size={13} className="text-blue-400 animate-spin shrink-0" />, bg: "bg-blue-500/10", text: "text-blue-400", labelKey: "endpointForm.running" },
  success: { icon: <CheckCircle2 size={13} className="text-emerald-500 shrink-0" />, bg: "bg-emerald-500/15", text: "text-emerald-500", labelKey: "endpointForm.passed" },
  failed: { icon: <XCircle size={13} className="text-red-400 shrink-0" />, bg: "bg-red-400/15", text: "text-red-400", labelKey: "endpointForm.failed" },
  skipped: { icon: <SkipForward size={13} className="text-[var(--text-muted)] shrink-0" />, bg: "bg-[var(--surface-2)]", text: "text-[var(--text-muted)]", labelKey: "endpointForm.skipped" },
};

function TestResultRow({ item }: { item: EndpointTestItem }) {
  const { t } = useTranslation();
  const [expanded, setExpanded] = useState(false);
  const sc = STATUS_CONFIG[item.status] ?? STATUS_CONFIG.pending;

  return (
    <div className={cn("rounded-md px-2.5 py-1.5 transition-colors",
      item.status === "running" ? "bg-blue-500/5 border border-blue-500/20" : "bg-[var(--surface-1)]")}>
      <button className="flex items-center gap-2 w-full text-left" onClick={() => setExpanded(!expanded)}>
        {sc.icon}
        <span className="text-xs text-[var(--text-primary)] flex-1">{item.summary}</span>
        <span className={cn("text-[10px] px-1.5 py-0.5 rounded shrink-0", sc.bg, sc.text)}>
          {item.capability} · {t(sc.labelKey)}
        </span>
        {item.duration_ms > 0 && (
          <span className="text-[10px] text-[var(--text-muted)] shrink-0 ml-1">
            {t("endpointForm.elapsed")} {item.duration_ms < 1000 ? `${item.duration_ms}ms` : `${(item.duration_ms / 1000).toFixed(1)}s`}
          </span>
        )}
        <span className="text-[10px] text-[var(--text-muted)] shrink-0 ml-0.5 max-w-24 truncate">{item.model_id}</span>
        {(item.detail || item.request_url || item.request_summary) && (
          <ChevronRight size={10} className={cn("text-[var(--text-muted)] transition-transform shrink-0", expanded && "rotate-90")} />
        )}
      </button>
      {expanded && (
        <div className="mt-2 pl-5 space-y-1.5 border-l-2 border-[var(--border-subtle)] ml-1.5">
          {item.request_url && (
            <div className="rounded bg-[var(--surface-2)] p-2">
              <p className="text-[10px] font-medium text-[var(--text-muted)] mb-0.5">{t("endpointForm.finalUrl")}</p>
              <p className="text-[11px] text-[var(--text-secondary)] break-all font-mono">{item.request_url}</p>
            </div>
          )}
          {item.request_summary && (
            <div className="rounded bg-[var(--surface-2)] p-2">
              <p className="text-[10px] font-medium text-[var(--text-muted)] mb-0.5">{t("endpointForm.requestSummary")}</p>
              <p className="text-[11px] text-[var(--text-secondary)] whitespace-pre-wrap">{item.request_summary}</p>
            </div>
          )}
          {item.detail && (
            <div className="rounded bg-[var(--surface-2)] p-2">
              <p className="text-[10px] font-medium text-[var(--text-muted)] mb-0.5">{t("endpointForm.details")}</p>
              <p className="text-[11px] text-[var(--text-secondary)] whitespace-pre-wrap break-all">{item.detail}</p>
            </div>
          )}
          {item.urls_tried && item.urls_tried.length > 1 && (
            <div className="rounded bg-[var(--surface-2)] p-2">
              <p className="text-[10px] font-medium text-[var(--text-muted)] mb-0.5">
                {t("endpointForm.urlsFromProfile", { count: item.urls_tried.length, branch: item.test_branch ? `, ${item.test_branch}` : "" })}
              </p>
              {item.urls_tried.map((u, i) => (
                <p key={i} className="text-[11px] text-[var(--text-secondary)] break-all font-mono">
                  {i === 0 ? "* " : ""}{t("endpointForm.urlPrefix", { index: i + 1 })}: {u}
                </p>
              ))}
            </div>
          )}
          {!item.detail && !item.request_url && !item.request_summary && (
            <p className="text-[11px] text-[var(--text-muted)] italic">{t("endpointForm.noDiagnostics")}</p>
          )}
        </div>
      )}
    </div>
  );
}

// ── Main component ──

export default function EndpointsSection() {
  const { t } = useTranslation();
  const config = useAppStore((s) => s.config);
  const endpoints = config?.endpoints ?? [];
  const [showCreate, setShowCreate] = useState(false);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [testing, setTesting] = useState<string | null>(null);
  const [testReports, setTestReports] = useState<Record<string, EndpointTestReport>>({});
  const [testProgress, setTestProgress] = useState<Record<string, EndpointTestProgress>>({});
  const [expandedTests, setExpandedTests] = useState<Record<string, boolean>>({});
  const testStartRef = useRef<Record<string, number>>({});
  const [elapsed, setElapsed] = useState<Record<string, number>>({});

  useEffect(() => {
    const unlisten = api.onTestProgress((progress) => {
      setTestProgress((prev) => ({ ...prev, [progress.endpoint_id]: progress }));
    });
    return () => { unlisten.then((fn) => fn()); };
  }, []);

  useEffect(() => {
    if (!testing) return;
    const id = setInterval(() => {
      setElapsed((prev) => {
        const start = testStartRef.current[testing];
        if (!start) return prev;
        return { ...prev, [testing]: Math.floor((Date.now() - start) / 1000) };
      });
    }, 1000);
    return () => clearInterval(id);
  }, [testing]);

  const handleTest = async (id: string) => {
    setTesting(id);
    setExpandedTests((s) => ({ ...s, [id]: true }));
    testStartRef.current[id] = Date.now();
    setElapsed((s) => ({ ...s, [id]: 0 }));
    setTestProgress((s) => { const n = { ...s }; delete n[id]; return n; });
    try {
      const report = await api.testEndpoint(id);
      setTestReports((r) => ({ ...r, [id]: report }));
    } catch (e) {
      setTestReports((r) => ({
        ...r,
        [id]: {
          endpoint_id: id, endpoint_name: "", endpoint_type_name: "",
          items: [{ model_id: "-", capability: "-", status: "failed" as const, summary: String(e), duration_ms: 0, urls_tried: [] }],
          duration_ms: 0, total_count: 1, success_count: 0, failed_count: 1, skipped_count: 0,
        },
      }));
    } finally {
      setTesting(null);
    }
  };

  const handleDelete = async (id: string) => {
    await api.removeEndpoint(id);
    const cfg = await api.getConfig();
    useAppStore.getState().setConfig(cfg);
    await api.refreshProviders();
  };

  return (
    <div className="max-w-3xl">
      <SectionHeader
        title={t("settingsSections.endpointsTitle")}
        description={t("settingsSections.endpointsDesc")}
        action={
          <Button size="sm" onClick={() => setShowCreate(true)}>
            <Plus size={14} /> {t("settingsSections.addEndpoint")}
          </Button>
        }
      />

      {showCreate && (
        <FadeIn>
          <EndpointForm onClose={() => setShowCreate(false)} />
        </FadeIn>
      )}

      {endpoints.length === 0 && !showCreate ? (
        <EmptyState
          icon={<Globe size={40} />}
          title={t("settingsSections.noEndpoints")}
          description={t("settingsSections.noEndpointsDesc")}
          action={
            <Button size="sm" onClick={() => setShowCreate(true)}>
              <Plus size={14} /> {t("settingsSections.addEndpoint")}
            </Button>
          }
        />
      ) : (
        <div className="space-y-3 mt-4">
          {endpoints.map((ep, i) => {
            const vendor = VENDOR_BADGE[ep.endpoint_type] ?? VENDOR_BADGE.custom;
            const report = testReports[ep.id];
            const progress = testProgress[ep.id];
            const isExpanded = expandedTests[ep.id];
            const isEditing = editingId === ep.id;
            const isSpeechEp = ep.endpoint_type === "azure_speech";
            const isTesting = testing === ep.id;
            const liveItems = isTesting && progress ? progress.items : report?.items;

            return (
              <FadeIn key={ep.id} delay={i * 0.05}>
                <GlassCard className="py-3">
                  <div className="flex items-center gap-3">
                    <div className="w-8 h-8 rounded-lg flex items-center justify-center shrink-0 overflow-hidden bg-[var(--surface-2)]">
                      <img src={vendor.icon} alt={t(vendor.labelKey)} className="w-6 h-6 object-contain" />
                    </div>
                    <div className="flex-1 min-w-0">
                      <div className="flex items-center gap-2">
                        <div className={cn(
                          "w-2 h-2 rounded-full shrink-0",
                          ep.enabled ? "bg-emerald-400 shadow-sm shadow-emerald-400/50" : "bg-[var(--text-muted)]",
                        )} />
                        <span className="font-medium text-[var(--text-primary)] truncate">{ep.name}</span>
                        <Badge variant={(vendor.color === "orange" ? "amber" : vendor.color === "purple" ? "blue" : vendor.color === "yellow" ? "amber" : vendor.color) as "blue" | "green" | "red" | "amber" | "gray" | undefined}>
                          {vendor.badge}
                        </Badge>
                        <span className="text-xs text-[var(--text-muted)]">{t(vendor.labelKey)}</span>
                      </div>
                      {isSpeechEp ? (
                        <div className="mt-0.5 pl-4 space-y-0.5">
                          <p className="text-xs text-[var(--text-muted)] truncate">
                            {t("endpointForm.regionLabel")}: <span className="text-[var(--text-secondary)]">{ep.speech_region || t("endpointForm.notConfigured")}</span>
                            {ep.speech_region && (
                              <span className="ml-2 text-emerald-400 text-[10px]">
                                ✓ {ep.speech_endpoint?.includes(".azure.cn") || ep.speech_region.startsWith("china")
                                  ? t("endpointForm.chinaRegion") : t("endpointForm.globalRegion")}
                              </span>
                            )}
                          </p>
                          {ep.speech_endpoint && (
                            <p className="text-[10px] text-[var(--text-muted)] truncate">{ep.speech_endpoint}</p>
                          )}
                        </div>
                      ) : (
                        <>
                          <p className="text-xs text-[var(--text-muted)] truncate mt-0.5 pl-4">{ep.url}</p>
                          {ep.models.length > 0 && (
                            <div className="flex flex-wrap gap-1 mt-1 pl-4">
                              {ep.models.map((m) => (
                                <span key={m.model_id} className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded text-[10px] bg-[var(--surface-2)] text-[var(--text-secondary)]">
                                  {m.capabilities.map((c) => {
                                    const Icon = CAPABILITY_ICONS[c];
                                    return Icon ? <Icon key={c} size={11} style={{ color: CAPABILITY_COLORS[c] }} /> : null;
                                  })}
                                  {m.display_name || m.model_id}
                                </span>
                              ))}
                            </div>
                          )}
                        </>
                      )}
                    </div>
                    <div className="flex items-center gap-1 shrink-0">
                      <Button variant="ghost" size="icon" className="h-7 w-7"
                        onClick={() => handleTest(ep.id)} disabled={testing === ep.id}>
                        {testing === ep.id ? <Loader2 size={14} className="animate-spin" /> : <TestTube size={14} />}
                      </Button>
                      <Button variant="ghost" size="icon" className="h-7 w-7"
                        onClick={() => setEditingId(isEditing ? null : ep.id)}>
                        <Edit2 size={14} />
                      </Button>
                      <Button variant="ghost" size="icon" className="h-7 w-7 text-red-400"
                        onClick={() => handleDelete(ep.id)}>
                        <Trash2 size={14} />
                      </Button>
                    </div>
                  </div>

                  {(liveItems || report) && (
                    <div className="mt-2 pt-2 border-t border-[var(--border-subtle)]">
                      <button
                        className="flex items-center gap-1 text-xs text-[var(--text-secondary)] hover:text-[var(--text-primary)] transition-colors w-full"
                        onClick={() => setExpandedTests((s) => ({ ...s, [ep.id]: !isExpanded }))}
                      >
                        <ChevronRight size={12} className={cn("transition-transform", isExpanded && "rotate-90")} />
                        <span className="font-medium">{isTesting ? t("endpointForm.testInProgress") : t("endpointForm.testReport")}</span>
                        {isTesting && progress && (
                          <span className="flex items-center gap-1.5 ml-2 text-[10px]">
                            <span className="text-blue-400">{t("endpointForm.totalItems", { count: progress.total_count })}</span>
                            {progress.running_count > 0 && <span className="text-yellow-400">⟳{progress.running_count}</span>}
                            {progress.success_count > 0 && <span className="text-emerald-500">✓{progress.success_count}</span>}
                            {progress.failed_count > 0 && <span className="text-red-400">✗{progress.failed_count}</span>}
                            {progress.pending_count > 0 && <span className="text-[var(--text-muted)]">…{progress.pending_count}</span>}
                          </span>
                        )}
                        {!isTesting && report && <TestReportSummaryBadges items={report.items} />}
                        <span className="ml-auto text-[var(--text-muted)] text-[10px]">
                          {isTesting
                            ? `${t("endpointForm.elapsed")} ${String(Math.floor((elapsed[ep.id] || 0) / 60)).padStart(2, "0")}:${String((elapsed[ep.id] || 0) % 60).padStart(2, "0")}`
                            : report ? `${(report.duration_ms / 1000).toFixed(1)}s` : ""}
                        </span>
                      </button>
                      {isExpanded && liveItems && (
                        <div className="mt-2 space-y-1">
                          {liveItems.map((item, idx) => (
                            <TestResultRow key={idx} item={item} />
                          ))}
                        </div>
                      )}
                    </div>
                  )}

                  {isEditing && (
                    <div className="mt-3 pt-3 border-t border-[var(--border-subtle)]">
                      <EndpointEditPanel endpoint={ep} onClose={() => setEditingId(null)} />
                    </div>
                  )}
                </GlassCard>
              </FadeIn>
            );
          })}
        </div>
      )}
    </div>
  );
}
