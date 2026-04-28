import { useState, useCallback, useEffect, useRef } from "react";
import { useTranslation } from "react-i18next";
import { save as dialogSave, open as dialogOpen } from "@tauri-apps/plugin-dialog";
import { listen } from "@tauri-apps/api/event";
import {
  Plus, Trash2, Edit2, Check, X, Globe, Brain, Image, Volume2,
  TestTube, Monitor, Cloud, FileText,
  Mic, Search, Video, ArrowUpDown, Download, Upload, Info,
  Sun, Moon, Shield, Zap, Loader2, ChevronRight, ChevronLeft,
  CheckCircle2, XCircle, SkipForward, Copy, Clipboard,
} from "lucide-react";
import { cn } from "../lib/utils";
import {
  Button, GlassCard, Input, Select, Label, Badge, Switch,
  Separator, FadeIn, EmptyState, SectionHeader, Textarea,
  SettingRow,
} from "../components/ui";
import { useAppStore } from "../stores/app-store";
import { useThemeStore, type ThemeMode } from "../stores/theme-store";
import {
  api,
  type AiEndpoint,
  type AiModelEntry,
  type ModelCapability,
  type ModelReference,
  type VendorProfile,
  type EndpointTestReport,
  type EndpointTestItem,
  type EndpointTestProgress,
  type DiscoveredModel,
} from "../lib/tauri-api";

/* ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
   设置中心 — 匹配 C# 16 分区结构（精简合并版）
   分区: 端点 | 识别 | 存储 | 音频 | AI洞察 | 图片 | 视频 | 搜索 | 云 | 导入导出 | 界面 | 关于
   ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━ */

type SettingsTab =
  | "endpoints" | "recognition" | "storage" | "audio"
  | "insight" | "image" | "video" | "search"
  | "cloud" | "transfer" | "ui" | "about";

/* 厂商资料包类型映射（前端静态缓存，与 Rust get_vendor_profiles 对齐） */
const VENDOR_BADGE: Record<string, { labelKey: string; badge: string; icon: string; color: string }> = {
  azure_open_ai:          { labelKey: "vendorLabels.azure_open_ai",          badge: "AZ", icon: "/icons/azure-openai.svg",       color: "blue" },
  api_management_gateway: { labelKey: "vendorLabels.api_management_gateway", badge: "AP", icon: "/icons/apim-gateway.svg",       color: "purple" },
  open_ai_compatible:     { labelKey: "vendorLabels.open_ai_compatible",     badge: "OA", icon: "/icons/openai-compatible.svg",  color: "green" },
  azure_speech:           { labelKey: "vendorLabels.azure_speech",           badge: "SP", icon: "/icons/azure-speech.svg",       color: "yellow" },
  azure_translator:       { labelKey: "vendorLabels.azure_open_ai",          badge: "TR", icon: "/icons/azure-openai.svg",     color: "blue" },
  deep_l:                 { labelKey: "vendorLabels.deepl",                  badge: "DL", icon: "/icons/openai-compatible.svg",  color: "blue" },
  tencent_cloud:          { labelKey: "vendorLabels.tencent_cloud",          badge: "TC", icon: "/icons/openai-compatible.svg",  color: "blue" },
  alibaba_cloud:          { labelKey: "vendorLabels.alibaba_cloud",          badge: "AL", icon: "/icons/openai-compatible.svg",  color: "orange" },
  custom:                 { labelKey: "vendorLabels.custom",                 badge: "CU", icon: "/icons/openai-compatible.svg",  color: "gray" },
};

const CAPABILITY_LABEL_KEYS: Record<string, string> = {
  text: "capLabels.text", image: "capLabels.image", video: "capLabels.video",
  speech_to_text: "capLabels.speech_to_text", text_to_speech: "capLabels.text_to_speech",
};

const CAPABILITY_ICONS: Record<string, typeof Globe> = {
  text: FileText, image: Image, video: Video,
  speech_to_text: Mic, text_to_speech: Volume2,
};

const CAPABILITY_COLORS: Record<string, string> = {
  text: "var(--cap-text)", image: "var(--cap-image)", video: "var(--cap-video)",
  speech_to_text: "var(--cap-stt)", text_to_speech: "var(--cap-tts)",
};

const CAPABILITY_TIP_KEYS: Record<string, string> = {
  text: "capTips.text",
  image: "capTips.image",
  video: "capTips.video",
  speech_to_text: "capTips.speech_to_text",
  text_to_speech: "capTips.text_to_speech",
};

const ALL_CAPABILITIES: ModelCapability[] = ["text", "image", "video", "speech_to_text", "text_to_speech"];

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  端点管理（对齐 C# EndpointsSectionVM + EndpointBatchTestService）
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

function EndpointsSection() {
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

  // 监听实时进度事件
  useEffect(() => {
    const unlisten = listen<EndpointTestProgress>("endpoint-test-progress", (event) => {
      const progress = event.payload;
      setTestProgress((prev) => ({ ...prev, [progress.endpoint_id]: progress }));
    });
    return () => { unlisten.then((fn) => fn()); };
  }, []);

  // 计时器：测试进行中时每秒更新耗时
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
    // 清除旧报告，保留进度
    setTestProgress((s) => { const n = { ...s }; delete n[id]; return n; });
    try {
      const report = await api.testEndpoint(id);
      setTestReports((r) => ({ ...r, [id]: report }));
    } catch (e) {
      setTestReports((r) => ({
        ...r,
        [id]: {
          endpoint_id: id,
          endpoint_name: "",
          endpoint_type_name: "",
          items: [{ model_id: "-", capability: "-", status: "failed" as const, summary: String(e), duration_ms: 0, urls_tried: [] }],
          duration_ms: 0,
          total_count: 1,
          success_count: 0,
          failed_count: 1,
          skipped_count: 0,
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
                  {/* ─ 头部行 ─ */}
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
                        <Badge variant={(vendor.color === "orange" ? "amber" : vendor.color === "purple" ? "blue" : vendor.color === "yellow" ? "amber" : vendor.color) as "blue" | "green" | "red" | "amber" | "gray" | undefined}>{vendor.badge}</Badge>
                        <span className="text-xs text-[var(--text-muted)]">{t(vendor.labelKey)}</span>
                      </div>
                      {/* Speech 端点显示区域+终结点，AI 端点显示 URL */}
                      {isSpeechEp ? (
                        <div className="mt-0.5 pl-4 space-y-0.5">
                          <p className="text-xs text-[var(--text-muted)] truncate">
                            {t("endpointForm.regionLabel")}: <span className="text-[var(--text-secondary)]">{ep.speech_region || t("endpointForm.notConfigured")}</span>
                            {ep.speech_region && (
                              <span className="ml-2 text-emerald-400 text-[10px]">
                                ✓ {ep.speech_endpoint?.includes(".azure.cn") || ep.speech_region.startsWith("china") ? t("endpointForm.chinaRegion") : t("endpointForm.globalRegion")}
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
                          {/* 模型标签 */}
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

                  {/* ─ 实时测试进度 / 最终报告（可展开） ─ */}
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

                  {/* ─ 编辑面板（内联展开） ─ */}
                  {isEditing && (
                    <div className="mt-3 pt-3 border-t border-[var(--border-subtle)]">
                      <EndpointEditPanel
                        endpoint={ep}
                        onClose={() => setEditingId(null)}
                      />
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

/* ─── 测试报告小组件 ─── */

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
    <div className={cn("rounded-md px-2.5 py-1.5 transition-colors", item.status === "running" ? "bg-blue-500/5 border border-blue-500/20" : "bg-[var(--surface-1)]")}>
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
              <p className="text-[10px] font-medium text-[var(--text-muted)] mb-0.5">{t("endpointForm.urlsFromProfile", { count: item.urls_tried.length, branch: item.test_branch ? `, ${item.test_branch}` : "" })}</p>
              {item.urls_tried.map((u, i) => (
                <p key={i} className="text-[11px] text-[var(--text-secondary)] break-all font-mono">
                  {i === 0 ? "* " : ""}{`${t("endpointForm.urlPrefix", { index: i + 1 })}`}: {u}
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

/* ─── 新建端点表单 ─── */

/* ─── O-19: 端点→模型级联选择器（单格：端点名/模型名）─── */

function ModelRefSelector({
  label,
  value,
  onChange,
  capability,
}: {
  label: string;
  value: ModelReference;
  onChange: (ref: ModelReference) => void;
  capability?: ModelCapability;
}) {
  const { t } = useTranslation();
  const config = useAppStore((s) => s.config);
  const endpoints = config?.endpoints ?? [];

  // 过滤有合适模型的端点
  const filteredEndpoints = endpoints
    .filter((ep) => ep.enabled && ep.endpoint_type !== "azure_speech")
    .filter((ep) => !capability || ep.models.some((m) => m.capabilities.includes(capability)));

  // 构建扁平化的 "端点名/模型名" 选项列表
  const options: { endpoint_id: string; model_id: string; displayLabel: string }[] = [];
  for (const ep of filteredEndpoints) {
    const models = ep.models.filter((m) => !capability || m.capabilities.includes(capability));
    for (const m of models) {
      options.push({
        endpoint_id: ep.id,
        model_id: m.model_id,
        displayLabel: `${ep.name} / ${m.display_name || m.model_id}`,
      });
    }
  }

  const currentValue = value.endpoint_id && value.model_id
    ? `${value.endpoint_id}::${value.model_id}`
    : "";

  return (
    <div>
      <Label>{label}</Label>
      <Select
        className="w-full"
        value={currentValue}
        onChange={(e) => {
          const v = e.target.value;
          if (!v) { onChange({ endpoint_id: "", model_id: "" }); return; }
          const [eid, ...rest] = v.split("::");
          onChange({ endpoint_id: eid, model_id: rest.join("::") });
        }}
      >
        <option value="">{t("settingsSections.selectModel")}</option>
        {options.map((o) => (
          <option key={`${o.endpoint_id}::${o.model_id}`} value={`${o.endpoint_id}::${o.model_id}`}>
            {o.displayLabel}
          </option>
        ))}
      </Select>
    </div>
  );
}

/* ─── 配置保存 hook ─── */

function useConfigUpdater() {
  return useCallback(async (updater: (cfg: NonNullable<ReturnType<typeof useAppStore.getState>["config"]>) => void) => {
    const cfg = useAppStore.getState().config;
    if (!cfg) return;
    const copy = JSON.parse(JSON.stringify(cfg));
    updater(copy);
    await api.updateConfig(copy);
    useAppStore.getState().setConfig(copy);
  }, []);
}

function EndpointForm({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation();
  const [profiles, setProfiles] = useState<VendorProfile[]>([]);
  const [form, setForm] = useState({
    name: "",
    endpoint_type: "open_ai_compatible",
    url: "",
    api_key: "",
    api_version: "",
    region: "",
    auth_header_mode: "api_key",
    auth_mode: "api_key",
    azure_tenant_id: "",
    azure_client_id: "",
    // Speech 专属
    speech_subscription_key: "",
    speech_region: "",
    speech_endpoint: "",
  });
  const [models, setModels] = useState<AiModelEntry[]>([]);
  const [newModelId, setNewModelId] = useState("");
  const [newModelCaps, setNewModelCaps] = useState<ModelCapability[]>(["text"]);
  const [discovering, setDiscovering] = useState(false);
  const [discoveredModels, setDiscoveredModels] = useState<DiscoveredModel[]>([]);
  const [expandedModelIdx, setExpandedModelIdx] = useState<number | null>(null);

  const isSpeech = form.endpoint_type === "azure_speech";

  useEffect(() => {
    api.getVendorProfiles().then(setProfiles).catch(() => {});
  }, []);

  const update = (f: string, v: string) => {
    setForm((s) => {
      const next = { ...s, [f]: v };
      // 切换类型时自动填充默认值（对齐 C# ApplyTemplate）
      if (f === "endpoint_type") {
        const p = profiles.find((p) => p.endpoint_type === v);
        if (p) {
          next.auth_header_mode = p.default_auth_header;
          next.api_version = p.default_api_version;
          // 切换类型时重置 auth_mode
          next.auth_mode = "api_key";
          // 非 AAD 支持的类型清空 AAD 字段（对齐 C# clearAzureIdentityFields）
          if (!p.supports_aad) {
            next.azure_tenant_id = "";
            next.azure_client_id = "";
          }
        }
      }
      return next;
    });
  };

  const addModel = () => {
    if (!newModelId.trim()) return;
    setModels((prev) => [
      ...prev,
      { model_id: newModelId.trim(), display_name: newModelId.trim(), capabilities: [...newModelCaps] },
    ]);
    setNewModelId("");
  };

  const addDiscoveredModel = (m: DiscoveredModel) => {
    if (models.some((x) => x.model_id === m.id)) return;
    setModels((prev) => [
      ...prev,
      { model_id: m.id, display_name: m.display_name || m.id, capabilities: ["text"] },
    ]);
  };

  const removeModel = (modelId: string) => {
    setModels((prev) => prev.filter((m) => m.model_id !== modelId));
    if (expandedModelIdx !== null) setExpandedModelIdx(null);
  };

  const toggleModelCapability = (idx: number, cap: ModelCapability) => {
    setModels((prev) => prev.map((m, i) => {
      if (i !== idx) return m;
      const has = m.capabilities.includes(cap);
      if (has && m.capabilities.length <= 1) return m;
      const caps = has ? m.capabilities.filter((c) => c !== cap) : [...m.capabilities, cap];
      return { ...m, capabilities: caps };
    }));
  };

  const handleDiscover = async () => {
    // 创建临时终结点来触发发现
    const tempId = crypto.randomUUID();
    const tempEp: AiEndpoint = {
      id: tempId, name: "temp", endpoint_type: form.endpoint_type,
      url: form.url, api_key: form.api_key, api_version: form.api_version || undefined,
      models: [], enabled: true, auth_header_mode: form.auth_header_mode,
      auth_mode: form.auth_mode, azure_tenant_id: form.azure_tenant_id,
      azure_client_id: form.azure_client_id,
      speech_subscription_key: "", speech_region: "", speech_endpoint: "",
    };
    await api.addEndpoint(tempEp);
    setDiscovering(true);
    try {
      const found = await api.discoverModels(tempId);
      setDiscoveredModels(found);
    } catch (e) {
      setDiscoveredModels([]);
      alert(String(e));
    } finally {
      setDiscovering(false);
      await api.removeEndpoint(tempId);
    }
  };

  const handleSave = useCallback(async () => {
    const ep: AiEndpoint = {
      id: crypto.randomUUID(),
      name: form.name,
      endpoint_type: form.endpoint_type,
      url: isSpeech ? "" : form.url,
      api_key: isSpeech ? "" : form.api_key,
      api_version: isSpeech ? undefined : (form.api_version || undefined),
      region: form.region || undefined,
      models: isSpeech ? [] : models,
      enabled: true,
      auth_header_mode: form.auth_header_mode,
      auth_mode: form.auth_mode,
      azure_tenant_id: form.azure_tenant_id,
      azure_client_id: form.azure_client_id,
      speech_subscription_key: isSpeech ? form.speech_subscription_key : "",
      speech_region: isSpeech ? form.speech_region : "",
      speech_endpoint: isSpeech ? form.speech_endpoint : "",
    };
    await api.addEndpoint(ep);
    const cfg = await api.getConfig();
    useAppStore.getState().setConfig(cfg);
    await api.refreshProviders();
    onClose();
  }, [form, models, isSpeech, onClose]);

  const activeProfile = profiles.find((p) => p.endpoint_type === form.endpoint_type);

  // Speech 端点保存条件：name + (subscription_key + region)
  // AI 端点保存条件：name + url + 至少一个 model
  const canSave = isSpeech
    ? !!(form.name && form.speech_subscription_key && form.speech_region)
    : !!(form.name && form.url && models.length > 0);

  return (
    <GlassCard className="mb-4 border-brand-500/20" glow>
      <div className="flex items-center gap-2 mb-4">
        <Button variant="ghost" size="icon" className="h-7 w-7" onClick={onClose}>
          <ChevronLeft size={16} />
        </Button>
        <h3 className="text-sm font-semibold text-[var(--text-primary)]">{t("endpointForm.newEndpoint")}</h3>
      </div>

      {/* 名称 + 类型 */}
      <div className="grid grid-cols-2 gap-4">
        <div>
          <Label>{t("endpointForm.name")}</Label>
          <Input value={form.name} onChange={(e) => update("name", e.target.value)} placeholder={isSpeech ? "Southeast Asia" : "My AI Endpoint"} />
        </div>
        <div>
          <Label>{t("endpointForm.type")}</Label>
          <Select className="w-full" value={form.endpoint_type} onChange={(e) => update("endpoint_type", e.target.value)}>
            {Object.entries(VENDOR_BADGE).map(([k, v]) => (
              <option key={k} value={k}>{v.badge} {t(v.labelKey)}</option>
            ))}
          </Select>
        </div>
      </div>

      {isSpeech ? (
        /* ═══ Speech 端点表单 ═══ */
        <div className="space-y-4 mt-4">
          <div className="rounded-lg bg-amber-500/10 border border-amber-500/20 p-3">
            <p className="text-xs text-[var(--text-secondary)]">
              🎤 {t("endpointForm.speechNote")}
            </p>
          </div>
          <div>
            <Label>{t("endpointForm.speechEndpoint")}</Label>
            <Input value={form.speech_endpoint}
              onChange={(e) => update("speech_endpoint", e.target.value)}
              placeholder="https://southeastasia.api.cognitive.microsoft.com" />
            <p className="text-[10px] text-[var(--text-muted)] mt-0.5">{t("endpointForm.speechEndpointHint")}</p>
          </div>
          <div className="grid grid-cols-2 gap-4">
            <div>
              <Label>{t("endpointForm.subscriptionKey")}</Label>
              <Input type="password" value={form.speech_subscription_key}
                onChange={(e) => update("speech_subscription_key", e.target.value)}
                placeholder="Azure Speech subscription key" />
            </div>
            <div>
              <Label>{t("endpointForm.region")}</Label>
              <Input value={form.speech_region}
                onChange={(e) => update("speech_region", e.target.value)}
                placeholder="southeastasia" />
              {form.speech_region && (
                <p className="text-[10px] mt-0.5 text-emerald-400">
                  ✓ {form.speech_endpoint?.includes(".azure.cn") || form.speech_region.startsWith("china") ? t("endpointForm.chinaRegion") : t("endpointForm.globalRegion")}
                </p>
              )}
            </div>
          </div>
        </div>
      ) : (
        /* ═══ AI 端点表单 ═══ */
        <>
          <div className="grid grid-cols-1 gap-4 mt-4">
            <div>
              <Label>{t("endpointForm.endpointUrl")}</Label>
              <Input value={form.url} onChange={(e) => update("url", e.target.value)} placeholder="https://your-endpoint.openai.azure.com/openai" />
            </div>
          </div>

          {/* 认证方式选项卡 — 仅 Azure OpenAI 显示 ApiKey / AAD 切换（对齐 C# CanSelectedEndpointUseAad） */}
          {activeProfile?.supports_aad && (
            <div className="flex gap-2 mt-4 mb-2">
              <button
                className={cn("px-3 py-1 text-xs rounded-md border transition-colors",
                  form.auth_mode === "api_key"
                    ? "bg-brand-500/20 border-brand-500/40 text-brand-400"
                    : "bg-transparent border-[var(--border-subtle)] text-[var(--text-muted)] hover:text-[var(--text-secondary)]"
                )}
                onClick={() => update("auth_mode", "api_key")}
              >{t("endpointForm.apiKeyDefault")}</button>
              <button
                className={cn("px-3 py-1 text-xs rounded-md border transition-colors",
                  form.auth_mode === "aad"
                    ? "bg-brand-500/20 border-brand-500/40 text-brand-400"
                    : "bg-transparent border-[var(--border-subtle)] text-[var(--text-muted)] hover:text-[var(--text-secondary)]"
                )}
                onClick={() => update("auth_mode", "aad")}
              >Microsoft Entra ID (AAD)</button>
            </div>
          )}

          {/* API Key 面板 — 非 AAD 模式时显示（对齐 C# !IsSelectedEndpointAad） */}
          {form.auth_mode !== "aad" && (
            <div className="grid grid-cols-2 gap-4 mt-2">
              <div>
                <Label>API Key</Label>
                <Input type="password" value={form.api_key} onChange={(e) => update("api_key", e.target.value)} placeholder="sk-..." />
              </div>
              <div>
                <Label>{t("endpointForm.region")} {form.endpoint_type.startsWith("azure") ? "(AZURE)" : `(${t("endpointForm.optional")})`}</Label>
                <Input value={form.region} onChange={(e) => update("region", e.target.value)} placeholder="eastus" />
              </div>
            </div>
          )}

          {/* AAD 面板 — AAD 模式时显示（对齐 C# IsSelectedEndpointAad） */}
          {form.auth_mode === "aad" && (
            <div className="grid grid-cols-2 gap-4 mt-2">
              <div>
                <Label>Tenant ID</Label>
                <Input value={form.azure_tenant_id} onChange={(e) => update("azure_tenant_id", e.target.value)} placeholder={t("endpointForm.aadTenantHint")} />
              </div>
              <div>
                <Label>{t("endpointForm.clientIdOptional")}</Label>
                <Input value={form.azure_client_id} onChange={(e) => update("azure_client_id", e.target.value)} placeholder={t("endpointForm.clientIdHint")} />
              </div>
              <div className="col-span-2">
                <AadLoginButton endpointId={form.name || "new"} tenantId={form.azure_tenant_id} clientId={form.azure_client_id} />
              </div>
            </div>
          )}

          {/* API 版本 — 有默认值的类型才显示（对齐 C# 各 profile 的 defaults.apiVersion） */}
          {activeProfile && activeProfile.default_api_version && (
            <div className="mt-4" style={{ maxWidth: "50%" }}>
              <Label>{t("endpointForm.apiVersion")}</Label>
              <Input value={form.api_version} onChange={(e) => update("api_version", e.target.value)} placeholder={activeProfile.default_api_version} />
            </div>
          )}

          <Separator className="my-4" />

          {/* ─ 模型列表 ─ */}
          <h4 className="text-xs font-semibold text-[var(--text-primary)] mb-2">{t("endpointForm.modelList")}</h4>

          {models.length > 0 && (
            <div className="space-y-1 mb-3">
              {models.map((m, idx) => {
                const isExpanded = expandedModelIdx === idx;
                return (
                  <div key={m.model_id} className={cn(
                    "rounded border transition-all",
                    isExpanded
                      ? "bg-[var(--model-item-selected-bg)] border-[var(--model-item-selected-border)]"
                      : "bg-[var(--model-item-bg)] border-transparent hover:border-[var(--border-subtle)]",
                  )}>
                    <div className="flex items-center gap-2 px-2.5 py-1.5 cursor-pointer"
                      onClick={() => setExpandedModelIdx(isExpanded ? null : idx)}>
                      <span className="text-xs text-[var(--text-primary)] flex-1 truncate">
                        {m.display_name || m.model_id}
                      </span>
                      <div className="flex gap-1 shrink-0">
                        {m.capabilities.map((c) => {
                          const Icon = CAPABILITY_ICONS[c];
                          return (
                            <span key={c} className="flex items-center gap-0.5 text-[10px]"
                              style={{ color: CAPABILITY_COLORS[c] }}>
                              {Icon && <Icon size={11} />}
                              {t(CAPABILITY_LABEL_KEYS[c])}
                            </span>
                          );
                        })}
                      </div>
                      <button onClick={(e) => { e.stopPropagation(); removeModel(m.model_id); }}
                        className="text-red-400 hover:text-red-300 shrink-0 ml-1">
                        <X size={12} />
                      </button>
                    </div>
                    {isExpanded && (
                      <div className="px-2.5 pb-2.5 pt-1 border-t border-[var(--border-subtle)]">
                        <div className="flex items-center gap-1.5 flex-wrap">
                          {ALL_CAPABILITIES.map((c) => {
                            const Icon = CAPABILITY_ICONS[c];
                            const color = CAPABILITY_COLORS[c];
                            const isActive = m.capabilities.includes(c);
                            return (
                              <button key={c} title={t(CAPABILITY_TIP_KEYS[c])}
                                className={cn(
                                  "h-7 px-2 rounded flex items-center gap-1.5 border text-xs transition-all",
                                  isActive
                                    ? "border-current shadow-sm font-medium"
                                    : "border-[var(--border-subtle)] text-[var(--text-muted)] opacity-50 hover:opacity-80",
                                )}
                                style={isActive ? { color, borderColor: color } : undefined}
                                onClick={() => toggleModelCapability(idx, c)}
                              >
                                <Icon size={13} />
                                {t(CAPABILITY_LABEL_KEYS[c])}
                              </button>
                            );
                          })}
                        </div>
                      </div>
                    )}
                  </div>
                );
              })}
            </div>
          )}

          {/* 添加模型行 */}
          <div className="flex items-end gap-2">
            <div className="flex-1">
              <Input value={newModelId} onChange={(e) => setNewModelId(e.target.value)}
                placeholder={form.endpoint_type === "azure_open_ai" ? t("endpointForm.deploymentPlaceholder") : t("endpointForm.modelIdPlaceholder")}
                onKeyDown={(e) => e.key === "Enter" && addModel()} />
            </div>
            <div className="flex gap-1">
              {ALL_CAPABILITIES.map((c) => {
                const Icon = CAPABILITY_ICONS[c];
                const color = CAPABILITY_COLORS[c];
                const isActive = newModelCaps.includes(c);
                return (
                  <button key={c} title={t(CAPABILITY_TIP_KEYS[c])}
                    className={cn(
                      "w-7 h-7 rounded flex items-center justify-center border transition-all",
                      isActive
                        ? "border-current shadow-sm"
                        : "border-[var(--border-subtle)] text-[var(--text-muted)] opacity-40 hover:opacity-70",
                    )}
                    style={isActive ? { color, borderColor: color } : undefined}
                    onClick={() => setNewModelCaps([c])}
                  >
                    <Icon size={14} />
                  </button>
                );
              })}
            </div>
            <Button size="sm" variant="secondary" onClick={addModel} disabled={!newModelId.trim()}>
              <Plus size={12} />
            </Button>
          </div>

          {/* 模型发现 */}
          {activeProfile?.supports_model_discovery && (
            <div className="mt-3">
              <Button size="sm" variant="secondary" onClick={handleDiscover}
                disabled={discovering || !form.url || !form.api_key}>
                {discovering ? <Loader2 size={12} className="animate-spin" /> : <Search size={12} />}
                {discovering ? t("endpointForm.discovering") : " " + t("endpointForm.discoverModels")}
              </Button>
              {discoveredModels.length > 0 && (
                <div className="mt-2 max-h-40 overflow-y-auto space-y-0.5 rounded bg-[var(--surface-1)] p-2">
                  {discoveredModels.map((m) => (
                    <div key={m.id} className="flex items-center justify-between px-2 py-1 rounded hover:bg-[var(--hover-bg)] transition-colors">
                      <span className="text-xs text-[var(--text-primary)]">{m.id}</span>
                      {m.owned_by && <span className="text-[10px] text-[var(--text-muted)]">{m.owned_by}</span>}
                      <Button size="sm" variant="ghost" className="h-5 px-1.5 text-[10px]"
                        onClick={() => addDiscoveredModel(m)}
                        disabled={models.some((x) => x.model_id === m.id)}>
                        {models.some((x) => x.model_id === m.id) ? t("endpointForm.alreadyAdded") : t("endpointForm.addModel")}
                      </Button>
                    </div>
                  ))}
                </div>
              )}
            </div>
          )}
        </>
      )}

      <div className="flex justify-end gap-2 mt-4">
        <Button variant="secondary" size="sm" onClick={onClose}><X size={14} /> {t("endpointForm.cancel")}</Button>
        <Button size="sm" onClick={handleSave} disabled={!canSave}>
          <Check size={14} /> {t("endpointForm.save")}
        </Button>
      </div>
    </GlassCard>
  );
}

/* ─── 内联编辑面板（即时保存模式） ─── */

function EndpointEditPanel({ endpoint, onClose }: { endpoint: AiEndpoint; onClose: () => void }) {
  const { t } = useTranslation();
  const [form, setForm] = useState({ ...endpoint });
  const [models, setModels] = useState<AiModelEntry[]>([...endpoint.models]);
  const [newModelId, setNewModelId] = useState("");
  const [newModelCaps, setNewModelCaps] = useState<ModelCapability[]>(["text"]);
  const [discovering, setDiscovering] = useState(false);
  const [discoveredModels, setDiscoveredModels] = useState<DiscoveredModel[]>([]);
  const [expandedModelIdx, setExpandedModelIdx] = useState<number | null>(null);
  const [saving, setSaving] = useState(false);

  const isSpeech = form.endpoint_type === "azure_speech";

  // ── 即时保存：form 或 models 变化时自动持久化（防抖 600ms）──
  const saveTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const formRef = useRef(form);
  const modelsRef = useRef(models);
  formRef.current = form;
  modelsRef.current = models;

  const persistNow = useCallback(async () => {
    setSaving(true);
    try {
      const updated: AiEndpoint = { ...formRef.current, models: formRef.current.endpoint_type === "azure_speech" ? [] : modelsRef.current };
      await api.updateEndpoint(updated);
      const cfg = await api.getConfig();
      useAppStore.getState().setConfig(cfg);
      await api.refreshProviders();
    } finally {
      setSaving(false);
    }
  }, []);

  const scheduleSave = useCallback(() => {
    if (saveTimerRef.current) clearTimeout(saveTimerRef.current);
    saveTimerRef.current = setTimeout(() => persistNow(), 600);
  }, [persistNow]);

  // 清理定时器
  useEffect(() => () => { if (saveTimerRef.current) clearTimeout(saveTimerRef.current); }, []);

  const update = (f: string, v: string | boolean) => {
    setForm((s) => ({ ...s, [f]: v }));
    scheduleSave();
  };

  const addModel = () => {
    if (!newModelId.trim()) return;
    setModels((prev) => {
      const next = [...prev, { model_id: newModelId.trim(), display_name: newModelId.trim(), capabilities: [...newModelCaps] }];
      return next;
    });
    setNewModelId("");
    scheduleSave();
  };

  const removeModel = (modelId: string) => {
    setModels((prev) => prev.filter((m) => m.model_id !== modelId));
    if (expandedModelIdx !== null) setExpandedModelIdx(null);
    scheduleSave();
  };

  const toggleModelCapability = (idx: number, cap: ModelCapability) => {
    setModels((prev) => prev.map((m, i) => {
      if (i !== idx) return m;
      const has = m.capabilities.includes(cap);
      // 至少保留一个能力
      if (has && m.capabilities.length <= 1) return m;
      const caps = has ? m.capabilities.filter((c) => c !== cap) : [...m.capabilities, cap];
      return { ...m, capabilities: caps };
    }));
    scheduleSave();
  };

  const handleDiscover = async () => {
    setDiscovering(true);
    try {
      const found = await api.discoverModels(endpoint.id);
      setDiscoveredModels(found);
    } catch (e) {
      alert(String(e));
    } finally {
      setDiscovering(false);
    }
  };

  // 返回：先 flush 未保存的变更，再关闭
  const handleBack = async () => {
    if (saveTimerRef.current) {
      clearTimeout(saveTimerRef.current);
      saveTimerRef.current = null;
      await persistNow();
    }
    onClose();
  };

  return (
    <div className="space-y-3">
      {/* ── 顶部：返回 + 标题 + 保存状态 ── */}
      <div className="flex items-center gap-2">
        <button onClick={handleBack} className="flex items-center gap-1 text-xs text-[var(--text-muted)] hover:text-[var(--text-primary)] transition-colors">
          <ChevronLeft size={14} />
          <span>{t("endpointForm.back")}</span>
        </button>
        <span className="flex-1" />
        {saving && <span className="text-[10px] text-[var(--text-muted)] animate-pulse">{t("endpointForm.saving")}</span>}
        <SettingRow label={t("endpointForm.enabled")} description="">
          <Switch checked={form.enabled} onCheckedChange={(v) => update("enabled", v)} />
        </SettingRow>
      </div>

      {/* ── 基本信息 ── */}
      <div className="grid grid-cols-2 gap-3">
        <div><Label>{t("endpointForm.name")}</Label><Input value={form.name} onBlur={() => scheduleSave()} onChange={(e) => setForm((s) => ({ ...s, name: e.target.value }))} /></div>
        <div>
          <Label>{t("endpointForm.endpointType")}</Label>
          <Select className="w-full" value={form.endpoint_type} onChange={(e) => update("endpoint_type", e.target.value)}>
            {Object.entries(VENDOR_BADGE).map(([k, v]) => (
              <option key={k} value={k}>{t(v.labelKey)}</option>
            ))}
          </Select>
        </div>
      </div>

      {isSpeech ? (
        /* ═══ Speech 编辑表单 ═══ */
        <div className="space-y-3">
          <div className="grid grid-cols-2 gap-3">
            <div><Label>{t("endpointForm.region")}</Label><Input value={form.speech_region} onChange={(e) => setForm((s) => ({ ...s, speech_region: e.target.value }))} onBlur={() => scheduleSave()} placeholder="southeastasia" /></div>
          </div>
          <div><Label>{t("endpointForm.speechEndpoint")}</Label><Input value={form.speech_endpoint} onChange={(e) => setForm((s) => ({ ...s, speech_endpoint: e.target.value }))} onBlur={() => scheduleSave()} placeholder="https://region.api.cognitive.microsoft.com" /></div>
          <div><Label>{t("endpointForm.subscriptionKey")}</Label><Input type="password" value={form.speech_subscription_key} onChange={(e) => setForm((s) => ({ ...s, speech_subscription_key: e.target.value }))} onBlur={() => scheduleSave()} /></div>
        </div>
      ) : (
        /* ═══ AI 编辑表单 ═══ */
        <>
          <div className="grid grid-cols-2 gap-3">
            <div><Label>{t("endpointForm.endpointUrl")}</Label><Input value={form.url} onChange={(e) => setForm((s) => ({ ...s, url: e.target.value }))} onBlur={() => scheduleSave()} /></div>
            <div><Label>{t("endpointForm.apiKey")}</Label><Input type="password" value={form.api_key} onChange={(e) => setForm((s) => ({ ...s, api_key: e.target.value }))} onBlur={() => scheduleSave()} /></div>
            <div><Label>{t("endpointForm.apiVersion")}</Label><Input value={form.api_version || ""} onChange={(e) => setForm((s) => ({ ...s, api_version: e.target.value }))} onBlur={() => scheduleSave()} /></div>
            <div><Label>{t("endpointForm.regionOptional")}</Label><Input value={form.region || ""} onChange={(e) => setForm((s) => ({ ...s, region: e.target.value }))} onBlur={() => scheduleSave()} /></div>
          </div>

          {/* AAD 登录 — 仅 Azure 端点显示 */}
          {(form.endpoint_type === "azure_open_ai" || form.endpoint_type === "api_management_gateway") && (
            <div className="mt-2">
              <AadLoginButton endpointId={endpoint.id} tenantId={form.azure_tenant_id || ""} clientId={form.azure_client_id || ""} />
            </div>
          )}

          <Separator />

          {/* ── 模型列表 ── */}
          <div className="flex items-center justify-between">
            <h4 className="text-xs font-semibold text-[var(--text-primary)]">{t("endpointForm.modelList")}</h4>
            <span className="text-[10px] text-[var(--text-muted)]">{t("endpointForm.clickToModify")}</span>
          </div>
          <div className="space-y-1">
            {models.map((m, idx) => {
              const isExpanded = expandedModelIdx === idx;
              return (
                <div key={m.model_id} className={cn(
                  "rounded border transition-all",
                  isExpanded
                    ? "bg-[var(--model-item-selected-bg)] border-[var(--model-item-selected-border)]"
                    : "bg-[var(--model-item-bg)] border-transparent hover:border-[var(--border-subtle)]",
                )}>
                  {/* 模型行 */}
                  <div className="flex items-center gap-2 px-2.5 py-1.5 cursor-pointer"
                    onClick={() => setExpandedModelIdx(isExpanded ? null : idx)}>
                    <span className="text-xs text-[var(--text-primary)] flex-1 truncate">
                      {m.display_name || m.model_id}
                    </span>
                    <div className="flex gap-1 shrink-0">
                      {m.capabilities.map((c) => {
                        const Icon = CAPABILITY_ICONS[c];
                        return (
                          <span key={c} className="flex items-center gap-0.5 text-[10px]"
                            style={{ color: CAPABILITY_COLORS[c] }}>
                            {Icon && <Icon size={11} />}
                            {t(CAPABILITY_LABEL_KEYS[c])}
                          </span>
                        );
                      })}
                    </div>
                    <button onClick={(e) => { e.stopPropagation(); removeModel(m.model_id); }}
                      className="text-red-400 hover:text-red-300 shrink-0 ml-1">
                      <X size={12} />
                    </button>
                  </div>
                  {/* 展开区：能力切换 */}
                  {isExpanded && (
                    <div className="px-2.5 pb-2.5 pt-1 border-t border-[var(--border-subtle)]">
                      <div className="flex items-center gap-1.5 flex-wrap">
                        {ALL_CAPABILITIES.map((c) => {
                          const Icon = CAPABILITY_ICONS[c];
                          const color = CAPABILITY_COLORS[c];
                          const isActive = m.capabilities.includes(c);
                          return (
                            <button key={c} title={t(CAPABILITY_TIP_KEYS[c])}
                              className={cn(
                                "h-7 px-2 rounded flex items-center gap-1.5 border text-xs transition-all",
                                isActive
                                  ? "border-current shadow-sm font-medium"
                                  : "border-[var(--border-subtle)] text-[var(--text-muted)] opacity-50 hover:opacity-80",
                              )}
                              style={isActive ? { color, borderColor: color } : undefined}
                              onClick={() => toggleModelCapability(idx, c)}
                            >
                              <Icon size={13} />
                              {t(CAPABILITY_LABEL_KEYS[c])}
                            </button>
                          );
                        })}
                      </div>
                    </div>
                  )}
                </div>
              );
            })}
          </div>

          {/* 添加模型 */}
          <div className="flex items-end gap-2">
            <Input value={newModelId} onChange={(e) => setNewModelId(e.target.value)} placeholder={t("endpointForm.modelIdPlaceholder")} className="flex-1"
              onKeyDown={(e) => e.key === "Enter" && addModel()} />
            <div className="flex gap-1">
              {ALL_CAPABILITIES.map((c) => {
                const Icon = CAPABILITY_ICONS[c];
                const color = CAPABILITY_COLORS[c];
                const isActive = newModelCaps.includes(c);
                return (
                  <button key={c} title={t(CAPABILITY_TIP_KEYS[c])}
                    className={cn(
                      "w-7 h-7 rounded flex items-center justify-center border transition-all",
                      isActive
                        ? "border-current shadow-sm"
                        : "border-[var(--border-subtle)] text-[var(--text-muted)] opacity-40 hover:opacity-70",
                    )}
                    style={isActive ? { color, borderColor: color } : undefined}
                    onClick={() => setNewModelCaps([c])}
                  >
                    <Icon size={14} />
                  </button>
                );
              })}
            </div>
            <Button size="sm" variant="secondary" onClick={addModel} disabled={!newModelId.trim()}><Plus size={12} /></Button>
          </div>

          {/* 模型发现 */}
          {!["azure_open_ai", "azure_speech"].includes(form.endpoint_type) && (
            <Button size="sm" variant="secondary" onClick={handleDiscover} disabled={discovering}>
              {discovering ? <Loader2 size={12} className="animate-spin" /> : <Search size={12} />}
              {discovering ? t("endpointForm.discovering") : " " + t("endpointForm.discoverModels")}
            </Button>
          )}
          {discoveredModels.length > 0 && (
            <div className="max-h-32 overflow-y-auto space-y-0.5 rounded bg-[var(--surface-1)] p-2">
              {discoveredModels.map((m) => (
                <div key={m.id} className="flex items-center justify-between px-2 py-0.5 rounded hover:bg-[var(--hover-bg)]">
                  <span className="text-xs">{m.id}</span>
                  <Button size="sm" variant="ghost" className="h-5 px-1.5 text-[10px]"
                    onClick={() => {
                      if (!models.some((x) => x.model_id === m.id)) {
                        setModels((prev) => [...prev, { model_id: m.id, display_name: m.display_name || m.id, capabilities: ["text"] }]);
                        scheduleSave();
                      }
                    }}
                    disabled={models.some((x) => x.model_id === m.id)}>
                    {models.some((x) => x.model_id === m.id) ? t("endpointForm.alreadyAdded") : t("endpointForm.addModel")}
                  </Button>
                </div>
              ))}
            </div>
          )}
        </>
      )}
    </div>
  );
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  AAD 设备代码流登录按钮
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

function AadLoginButton({ endpointId, tenantId, clientId }: { endpointId: string; tenantId: string; clientId: string }) {
  const { t } = useTranslation();
  const [status, setStatus] = useState<"idle" | "waiting" | "selecting-tenant" | "success" | "error">("idle");
  const [errorMsg, setErrorMsg] = useState("");
  const [warningMsg, setWarningMsg] = useState("");
  const [tenants, setTenants] = useState<Array<{ tenant_id: string; display_name: string; default_domain: string }>>([]);
  const [tenantCtx, setTenantCtx] = useState<{ client_id: string; scope: string }>({ client_id: "", scope: "" });

  useEffect(() => {
    const unlistenAuth = api.onAadAuthResult((result) => {
      if (result.endpoint_id !== endpointId) return;
      // 如果后端通知需要浏览器重新认证（如 MFA），切换到等待状态
      if ((result as any).reauth) {
        setStatus("waiting");
        return;
      }
      if (result.success) {
        setStatus("success");
        if (result.warning) setWarningMsg(result.warning);
      } else {
        setStatus("error");
        setErrorMsg(result.error || t("endpointForm.authFailed"));
      }
    });
    const unlistenTenant = api.onAadTenantSelection((event) => {
      if (event.endpoint_id !== endpointId) return;
      setTenants(event.tenants);
      setTenantCtx({ client_id: event.client_id, scope: event.scope });
      setStatus("selecting-tenant");
    });
    return () => {
      unlistenAuth.then(fn => fn());
      unlistenTenant.then(fn => fn());
    };
  }, [endpointId]);

  const handleLogin = async () => {
    if (!endpointId) return;
    setStatus("waiting");
    setErrorMsg("");
    setWarningMsg("");
    try {
      await api.aadStartDeviceCodeFlow(endpointId, tenantId, clientId);
    } catch (e: any) {
      setStatus("error");
      setErrorMsg(String(e));
    }
  };

  const handleSelectTenant = async (tid: string) => {
    setStatus("waiting");
    try {
      await api.aadSelectTenant(endpointId, tid, tenantCtx.client_id, tenantCtx.scope);
    } catch (e: any) {
      setStatus("error");
      setErrorMsg(String(e));
    }
  };

  return (
    <div className="rounded-lg p-3 space-y-2" style={{ background: 'var(--card-bg)', border: '1px solid var(--card-border)' }}>
      {status === "idle" && (
        <>
          <p className="text-xs text-[var(--text-secondary)]">
            🔐 {t("endpointForm.aadLoginDesc")}
          </p>
          <Button variant="secondary" size="sm" onClick={handleLogin}>
            <Shield size={14} /> {t("endpointForm.loginAad")}
          </Button>
        </>
      )}
      {status === "waiting" && (
        <div className="flex items-center gap-2 text-xs text-[var(--text-muted)]">
          <Loader2 size={12} className="animate-spin" /> {t("endpointForm.browserOpened")}
          <Button variant="ghost" size="sm" onClick={() => setStatus("idle")} className="ml-2 text-xs">{t("endpointForm.cancel")}</Button>
        </div>
      )}
      {status === "selecting-tenant" && (
        <div className="space-y-2">
          <p className="text-xs text-[var(--text-secondary)]">
            🏢 {t("endpointForm.multiTenantHint")}
          </p>
          <div className="space-y-0.5 max-h-40 overflow-y-auto rounded p-1" style={{ border: '1px solid var(--border-subtle)' }}>
            {tenants.map((tn) => (
              <button
                key={tn.tenant_id}
                className="w-full text-left px-3 py-2 rounded text-xs transition-colors flex flex-col gap-0.5"
                style={{ border: '1px solid transparent' }}
                onMouseEnter={(e) => { e.currentTarget.style.background = 'var(--hover-bg)'; e.currentTarget.style.borderColor = 'var(--border-medium)'; }}
                onMouseLeave={(e) => { e.currentTarget.style.background = ''; e.currentTarget.style.borderColor = 'transparent'; }}
                onClick={() => handleSelectTenant(tn.tenant_id)}
              >
                <div className="flex items-center gap-2">
                  <span className="font-medium" style={{ color: 'var(--text-primary)' }}>{tn.display_name || tn.default_domain || t("endpointForm.unnamedTenant")}</span>
                  {tn.default_domain && tn.display_name && (
                    <span className="text-[10px]" style={{ color: 'var(--text-muted)' }}>({tn.default_domain})</span>
                  )}
                </div>
                <span className="text-[10px] font-mono" style={{ color: 'var(--text-muted)' }}>{tn.tenant_id}</span>
              </button>
            ))}
          </div>
          <Button variant="ghost" size="sm" onClick={() => setStatus("idle")} className="text-xs">{t("endpointForm.cancel")}</Button>
        </div>
      )}
      {status === "success" && (
        <div className="space-y-1">
          <div className="flex items-center gap-1 text-xs" style={{ color: 'var(--active-text)' }}>
            <CheckCircle2 size={14} /> {t("endpointForm.aadSuccess")}
            <Button variant="ghost" size="sm" onClick={() => setStatus("idle")} className="ml-2 text-xs">{t("endpointForm.reLogin")}</Button>
          </div>
          {warningMsg && (
            <p className="text-xs" style={{ color: 'var(--text-secondary)' }}>{warningMsg}</p>
          )}
        </div>
      )}
      {status === "error" && (
        <div className="space-y-1">
          <p className="text-xs flex items-center gap-1" style={{ color: '#ef4444' }}>
            <XCircle size={14} /> {errorMsg}
          </p>
          <Button variant="secondary" size="sm" onClick={handleLogin}>{t("endpointForm.retry")}</Button>
        </div>
      )}
    </div>
  );
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  主入口 — SettingsView
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

export function SettingsView() {
  const [activeTab, setActiveTab] = useState<SettingsTab>("endpoints");
  const { t } = useTranslation();

  const tabs: { id: SettingsTab; label: string; icon: React.ReactNode; group: string }[] = [
    { id: "endpoints", label: t("settingsSidebar.tabEndpoints"), icon: <Globe size={15} />, group: "connect" },
    { id: "recognition", label: t("settingsSidebar.tabRecognition"), icon: <Mic size={15} />, group: "voice" },
    { id: "storage", label: t("settingsSidebar.tabStorage"), icon: <FileText size={15} />, group: "voice" },
    { id: "audio", label: t("settingsSidebar.tabAudio"), icon: <Volume2 size={15} />, group: "voice" },
    { id: "insight", label: t("settingsSidebar.tabInsight"), icon: <Brain size={15} />, group: "ai" },
    { id: "image", label: t("settingsSidebar.tabImage"), icon: <Image size={15} />, group: "ai" },
    { id: "video", label: t("settingsSidebar.tabVideo"), icon: <Video size={15} />, group: "ai" },
    { id: "search", label: t("settingsSidebar.tabSearch"), icon: <Search size={15} />, group: "ai" },
    { id: "cloud", label: t("settingsSidebar.tabCloud"), icon: <Cloud size={15} />, group: "system" },
    { id: "transfer", label: t("settingsSidebar.tabTransfer"), icon: <ArrowUpDown size={15} />, group: "system" },
    { id: "ui", label: t("settingsSidebar.tabUi"), icon: <Monitor size={15} />, group: "system" },
    { id: "about", label: t("settingsSidebar.tabAbout"), icon: <Info size={15} />, group: "system" },
  ];

  const groups = [
    { key: "connect", label: t("settingsSidebar.groupConnect") },
    { key: "voice", label: t("settingsSidebar.groupVoice") },
    { key: "ai", label: t("settingsSidebar.groupAi") },
    { key: "system", label: t("settingsSidebar.groupSystem") },
  ];

  return (
    <div className="flex h-full">
      <div className="w-48 border-r border-[var(--border-subtle)] py-3 px-2 space-y-1 shrink-0 overflow-y-auto"
        style={{ backgroundColor: "var(--sidebar-bg)" }}>
        {groups.map((group) => (
          <div key={group.key}>
            <p className="text-[10px] font-medium text-[var(--text-muted)] uppercase tracking-widest px-3 pt-3 pb-1">
              {group.label}
            </p>
            {tabs.filter((tb) => tb.group === group.key).map((tab) => (
              <button key={tab.id} onClick={() => setActiveTab(tab.id)}
                className={cn(
                  "flex items-center gap-2 w-full rounded-xl px-3 py-2 text-sm transition-all duration-200",
                  activeTab === tab.id
                    ? "bg-[var(--active-bg)] text-[var(--active-text)] shadow-sm"
                    : "text-[var(--text-muted)] hover:bg-[var(--hover-bg)] hover:text-[var(--text-secondary)]",
                )}>
                {tab.icon}
                <span>{tab.label}</span>
              </button>
            ))}
          </div>
        ))}
      </div>
      <div className="flex-1 overflow-y-auto p-6">
        <FadeIn key={activeTab}>
          {activeTab === "endpoints" && <EndpointsSection />}
          {activeTab === "recognition" && <RecognitionSection />}
          {activeTab === "storage" && <StorageSection />}
          {activeTab === "audio" && <AudioSection />}
          {activeTab === "insight" && <InsightSection />}
          {activeTab === "image" && <ImageSection />}
          {activeTab === "video" && <VideoSection />}
          {activeTab === "search" && <SearchSection />}
          {activeTab === "cloud" && <CloudSection />}
          {activeTab === "transfer" && <TransferSection />}
          {activeTab === "ui" && <UiSection />}
          {activeTab === "about" && <AboutSection />}
        </FadeIn>
      </div>
    </div>
  );
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  识别与翻译 (匹配 C# RecognitionSectionVM + TextSectionVM)
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

function RecognitionSection() {
  const { t } = useTranslation();
  const config = useAppStore((s) => s.config);
  const rec = config?.recognition;
  const updateConfig = useConfigUpdater();

  if (!rec) return null;

  return (
    <div className="max-w-2xl space-y-6">
      <SectionHeader title={t("settingsSections.recognitionTitle")} description={t("settingsSections.recognitionDesc")} />

      <GlassCard className="space-y-4">
        <h3 className="text-sm font-semibold text-[var(--text-primary)]">{t("settingsSections.translationLang")}</h3>
        <div className="grid grid-cols-2 gap-4">
          <div>
            <Label>{t("settingsSections.defaultSourceLang")}</Label>
            <Select className="w-full" value={config?.default_source_lang || "zh-Hans"}
              onChange={(e) => updateConfig((cfg) => { cfg.default_source_lang = e.target.value; })}>
              <option value="auto">{t("live.autoDetect")}</option><option value="zh-Hans">中文（简体）</option><option value="en">English</option><option value="ja">日本語</option>
            </Select>
          </div>
          <div>
            <Label>{t("settingsSections.defaultTargetLang")}</Label>
            <Select className="w-full" value={config?.default_target_langs[0] || "en"}
              onChange={(e) => updateConfig((cfg) => { cfg.default_target_langs = [e.target.value]; })}>
              <option value="en">English</option><option value="ja">日本語</option><option value="zh-Hans">中文（简体）</option><option value="ko">한국어</option>
            </Select>
          </div>
        </div>
      </GlassCard>

      <GlassCard className="space-y-4">
        <h3 className="text-sm font-semibold text-[var(--text-primary)]">{t("settingsSections.speechRecognition")}</h3>
        <SettingRow label={t("settingsSections.fillerWords")} description={t("settingsSections.fillerWordsDesc")}>
          <Switch checked={rec.filter_modal_particles} onCheckedChange={(v) => updateConfig((cfg) => { cfg.recognition.filter_modal_particles = v; })} />
        </SettingRow>
        <div className="grid grid-cols-2 gap-4">
          <div><Label>{t("settingsSections.historyLimit")}</Label>
            <Input type="number" value={rec.max_history_items} className="w-32"
              onChange={(e) => updateConfig((cfg) => { cfg.recognition.max_history_items = Number(e.target.value) || 500; })} />
          </div>
          <div><Label>{t("settingsSections.realtimeMaxLen")}</Label>
            <Input type="number" value={rec.realtime_max_length} className="w-32"
              onChange={(e) => updateConfig((cfg) => { cfg.recognition.realtime_max_length = Number(e.target.value) || 150; })} />
          </div>
        </div>
        <div className="grid grid-cols-2 gap-4">
          <div><Label>{t("settingsSections.chunkDuration")}</Label>
            <Input type="number" value={rec.chunk_duration_ms} className="w-32"
              onChange={(e) => updateConfig((cfg) => { cfg.recognition.chunk_duration_ms = Number(e.target.value) || 200; })} />
          </div>
          <div><Label>{t("settingsSections.audioActivityThreshold")}</Label>
            <Input type="number" value={rec.audio_activity_threshold} className="w-32"
              onChange={(e) => updateConfig((cfg) => { cfg.recognition.audio_activity_threshold = Number(e.target.value) || 600; })} />
          </div>
        </div>
        <div className="grid grid-cols-2 gap-4">
          <div><Label>{t("settingsSections.audioGain")}</Label>
            <Input type="number" step="0.1" value={rec.audio_level_gain} className="w-32"
              onChange={(e) => updateConfig((cfg) => { cfg.recognition.audio_level_gain = Number(e.target.value) || 2.0; })} />
          </div>
        </div>
        <SettingRow label={t("settingsSections.showReconnectMark")} description={t("settingsSections.showReconnectMarkDesc")}>
          <Switch checked={rec.show_reconnect_marker} onCheckedChange={(v) => updateConfig((cfg) => { cfg.recognition.show_reconnect_marker = v; })} />
        </SettingRow>
      </GlassCard>

      <GlassCard className="space-y-4">
        <h3 className="text-sm font-semibold text-[var(--text-primary)]">{t("settingsSections.timeoutAndSilence")}</h3>
        <SettingRow label={t("settingsSections.enableAutoTimeout")} description={t("settingsSections.enableAutoTimeoutDesc")}>
          <Switch checked={rec.enable_auto_timeout} onCheckedChange={(v) => updateConfig((cfg) => { cfg.recognition.enable_auto_timeout = v; })} />
        </SettingRow>
        <div className="grid grid-cols-2 gap-4">
          <div><Label>{t("settingsSections.initialSilenceTimeout")}</Label>
            <Input type="number" value={rec.initial_silence_timeout_seconds} className="w-32"
              onChange={(e) => updateConfig((cfg) => { cfg.recognition.initial_silence_timeout_seconds = Number(e.target.value) || 25; })} />
          </div>
          <div><Label>{t("settingsSections.segmentTimeout")}</Label>
            <Input type="number" value={rec.end_silence_timeout_seconds} className="w-32"
              onChange={(e) => updateConfig((cfg) => { cfg.recognition.end_silence_timeout_seconds = Number(e.target.value) || 1; })} />
          </div>
        </div>
      </GlassCard>

      <GlassCard className="space-y-4">
        <h3 className="text-sm font-semibold text-[var(--text-primary)]">{t("settingsSections.noEchoReconnect")}</h3>
        <SettingRow label={t("settingsSections.enableNoEchoReconnect")} description={t("settingsSections.enableNoEchoReconnectDesc")}>
          <Switch checked={rec.enable_no_response_restart} onCheckedChange={(v) => updateConfig((cfg) => { cfg.recognition.enable_no_response_restart = v; })} />
        </SettingRow>
        <div><Label>{t("settingsSections.noEchoTimeout")}</Label>
          <Input type="number" value={rec.no_response_restart_seconds} className="w-32"
            onChange={(e) => updateConfig((cfg) => { cfg.recognition.no_response_restart_seconds = Number(e.target.value) || 3; })} />
        </div>
      </GlassCard>
    </div>
  );
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  存储与导出 (匹配 C# StorageSectionVM)
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

function StorageSection() {
  const { t } = useTranslation();
  const config = useAppStore((s) => s.config);
  const storage = config?.storage;
  const updateConfig = useConfigUpdater();
  const [validating, setValidating] = useState(false);
  const [validationMsg, setValidationMsg] = useState("");

  const handleValidateStorage = async () => {
    if (!storage?.batch_storage_connection_string?.trim()) {
      setValidationMsg(t("settingsSections.connectionString"));
      return;
    }
    setValidating(true);
    setValidationMsg(t("settingsSections.validating"));
    try {
      await api.validateStorageConnection(storage.batch_storage_connection_string);
      setValidationMsg("✓ OK");
      updateConfig((cfg) => { cfg.storage.batch_storage_is_valid = true; });
    } catch (e) {
      setValidationMsg(`✗ ${e}`);
      updateConfig((cfg) => { cfg.storage.batch_storage_is_valid = false; });
    } finally {
      setValidating(false);
    }
  };

  if (!storage) return null;

  return (
    <div className="max-w-2xl space-y-6">
      <SectionHeader title={t("settingsSections.storageTitle")} description={t("settingsSections.storageDesc")} />

      <GlassCard className="space-y-4">
        <h3 className="text-sm font-semibold text-[var(--text-primary)]">{t("settingsSections.blobStorage")}</h3>
        <p className="text-xs text-[var(--text-muted)]">{t("settingsSections.blobStorageDesc")}</p>
        <div>
          <Label>{t("settingsSections.connectionString")}</Label>
          <Input type="password" value={storage.batch_storage_connection_string}
            onChange={(e) => updateConfig((cfg) => {
              cfg.storage.batch_storage_connection_string = e.target.value;
              cfg.storage.batch_storage_is_valid = false;
            })}
            placeholder="DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;EndpointSuffix=..." />
        </div>
        <div className="grid grid-cols-2 gap-4">
          <div><Label>{t("settingsSections.audioContainer")}</Label>
            <Input value={storage.batch_audio_container_name}
              onChange={(e) => updateConfig((cfg) => { cfg.storage.batch_audio_container_name = e.target.value; })} />
          </div>
          <div><Label>{t("settingsSections.resultContainer")}</Label>
            <Input value={storage.batch_result_container_name}
              onChange={(e) => updateConfig((cfg) => { cfg.storage.batch_result_container_name = e.target.value; })} />
          </div>
        </div>
        <div className="flex items-center gap-3">
          <Button size="sm" variant="secondary" onClick={handleValidateStorage} disabled={validating}>
            {validating ? <Loader2 size={12} className="animate-spin" /> : <TestTube size={12} />}
            {validating ? ` ${t("settingsSections.validating")}` : ` ${t("settingsSections.validateConnection")}`}
          </Button>
          {storage.batch_storage_is_valid && (
            <Badge variant="green">{t("settingsSections.validated")}</Badge>
          )}
          {validationMsg && (
            <span className={cn("text-xs", validationMsg.startsWith("✓") ? "text-emerald-500" : validationMsg.startsWith("✗") ? "text-red-400" : "text-[var(--text-muted)]")}>
              {validationMsg}
            </span>
          )}
        </div>
      </GlassCard>

      <GlassCard className="space-y-4">
        <h3 className="text-sm font-semibold text-[var(--text-primary)]">{t("settingsSections.recording")}</h3>
        <SettingRow label={t("settingsSections.enableRecording")} description={t("settingsSections.enableRecordingDesc")}>
          <Switch checked={storage.enable_recording} onCheckedChange={(v) => updateConfig((cfg) => { cfg.storage.enable_recording = v; })} />
        </SettingRow>
        <div><Label>{t("settingsSections.mp3Bitrate")}</Label>
          <Select className="w-40" value={String(storage.recording_mp3_bitrate_kbps)}
            onChange={(e) => updateConfig((cfg) => { cfg.storage.recording_mp3_bitrate_kbps = Number(e.target.value); })}>
            <option value="128">128</option><option value="192">192</option><option value="256">256</option><option value="320">320</option>
          </Select>
        </div>
      </GlassCard>

      <GlassCard className="space-y-4">
        <h3 className="text-sm font-semibold text-[var(--text-primary)]">{t("settingsSections.subtitleExport")}</h3>
        <SettingRow label={t("settingsSections.exportSrt")} description={t("settingsSections.exportSrtDesc")}>
          <Switch checked={storage.export_srt_subtitles} onCheckedChange={(v) => updateConfig((cfg) => { cfg.storage.export_srt_subtitles = v; })} />
        </SettingRow>
        <SettingRow label={t("settingsSections.exportVtt")} description={t("settingsSections.exportVttDesc")}>
          <Switch checked={storage.export_vtt_subtitles} onCheckedChange={(v) => updateConfig((cfg) => { cfg.storage.export_vtt_subtitles = v; })} />
        </SettingRow>
      </GlassCard>
    </div>
  );
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  音频预处理 (匹配 C# RecognitionSectionVM 的 WebRTC 部分)
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

function AudioSection() {
  const { t } = useTranslation();
  const config = useAppStore((s) => s.config);
  const audio = config?.audio;
  const updateConfig = useConfigUpdater();

  if (!audio) return null;

  return (
    <div className="max-w-2xl space-y-6">
      <SectionHeader title={t("settingsSections.audioTitle")} description={t("settingsSections.audioDesc")} />

      <GlassCard className="space-y-4">
        <h3 className="text-sm font-semibold text-[var(--text-primary)]">{t("settingsSections.devices")}</h3>
        <div><Label>{t("settingsSections.inputDevice")}</Label>
          <Select className="w-full" value={audio.input_device_id || ""}
            onChange={(e) => updateConfig((cfg) => { cfg.audio.input_device_id = e.target.value || undefined; })}>
            <option value="">{t("settingsSections.defaultDevice")}</option>
          </Select>
        </div>
        <div><Label>{t("settingsSections.loopbackDevice")}</Label>
          <Select className="w-full" value={audio.loopback_device_id || ""}
            onChange={(e) => updateConfig((cfg) => { cfg.audio.loopback_device_id = e.target.value || undefined; })}>
            <option value="">{t("settingsSections.defaultOutputDevice")}</option>
          </Select>
        </div>
        <div><Label>{t("settingsSections.sampleRate")}</Label>
          <Select className="w-40" value={audio.sample_rate.toString()}
            onChange={(e) => updateConfig((cfg) => { cfg.audio.sample_rate = Number(e.target.value); })}>
            <option value="16000">16000 Hz</option>
            <option value="44100">44100 Hz</option>
            <option value="48000">48000 Hz</option>
          </Select>
        </div>
      </GlassCard>

      <GlassCard className="space-y-4">
        <h3 className="text-sm font-semibold text-[var(--text-primary)]">WebRTC APM</h3>
        <SettingRow label={t("settingsSections.aec")} description={t("settingsSections.aecDesc")}>
          <Switch checked={audio.enable_aec} onCheckedChange={(v) => updateConfig((cfg) => { cfg.audio.enable_aec = v; })} />
        </SettingRow>
        <SettingRow label={t("settingsSections.ns")} description={t("settingsSections.nsDesc")}>
          <Switch checked={audio.enable_ns} onCheckedChange={(v) => updateConfig((cfg) => { cfg.audio.enable_ns = v; })} />
        </SettingRow>
        <SettingRow label={t("settingsSections.agc")} description={t("settingsSections.agcDesc")}>
          <Switch checked={audio.enable_agc} onCheckedChange={(v) => updateConfig((cfg) => { cfg.audio.enable_agc = v; })} />
        </SettingRow>
      </GlassCard>
    </div>
  );
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  AI 洞察 (匹配 C# InsightSectionVM)
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

function InsightSection() {
  const { t } = useTranslation();
  const config = useAppStore((s) => s.config);
  const ai = config?.ai;
  const updateConfig = useConfigUpdater();

  const setModelRef = (field: string) => (ref: { endpoint_id: string; model_id: string }) => {
    updateConfig((cfg) => { Object.assign(cfg.ai, { [field]: ref }); });
  };

  if (!ai) return null;

  return (
    <div className="max-w-2xl space-y-6">
      <SectionHeader title={t("settingsSections.insightTitle")} description={t("settingsSections.insightDesc")} />

      <GlassCard className="space-y-4">
        <h3 className="text-sm font-semibold text-[var(--text-primary)]">{t("settingsSections.modelSelection")}</h3>
        <div className="grid grid-cols-2 gap-4">
          <ModelRefSelector label={t("settingsSections.insightModel")} value={ai.insight_model} onChange={setModelRef("insight_model")} capability="text" />
          <ModelRefSelector label={t("settingsSections.summaryModel")} value={ai.summary_model} onChange={setModelRef("summary_model")} capability="text" />
          <ModelRefSelector label={t("settingsSections.quickModel")} value={ai.quick_model} onChange={setModelRef("quick_model")} capability="text" />
          <ModelRefSelector label={t("settingsSections.conversationModel")} value={ai.conversation_model} onChange={setModelRef("conversation_model")} capability="text" />
          <ModelRefSelector label={t("settingsSections.intentModel")} value={ai.intent_model} onChange={setModelRef("intent_model")} capability="text" />
          {/* O-09: 补齐缺失的复盘模型选择器 */}
          <ModelRefSelector label={t("settingsSections.reviewModel")} value={ai.review_model} onChange={setModelRef("review_model")} capability="text" />
        </div>
      </GlassCard>

      <GlassCard className="space-y-4">
        <h3 className="text-sm font-semibold text-[var(--text-primary)]">{t("settingsSections.parameters")}</h3>
        <SettingRow label={t("settingsSections.enableReasoning")} description={t("settingsSections.enableReasoningDesc")}>
          <Switch checked={ai.enable_reasoning} onCheckedChange={(v) => updateConfig((cfg) => { cfg.ai.enable_reasoning = v; })} />
        </SettingRow>
        <div><Label>{t("settingsSections.maxConversationTurns")}</Label>
          <Input type="number" value={ai.max_conversation_turns} className="w-24"
            onChange={(e) => updateConfig((cfg) => { cfg.ai.max_conversation_turns = Number(e.target.value) || 20; })} />
        </div>
      </GlassCard>

      <GlassCard className="space-y-4">
        <h3 className="text-sm font-semibold text-[var(--text-primary)]">{t("settingsSections.systemPrompt")}</h3>
        <Textarea rows={4} value={ai.insight_system_prompt}
          onChange={(e) => updateConfig((cfg) => { cfg.ai.insight_system_prompt = e.target.value; })}
          placeholder={t("settingsSections.systemPromptPlaceholder")} />
      </GlassCard>
    </div>
  );
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  图片生成 (匹配 C# ImageGenSectionVM)
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

function ImageSection() {
  const { t } = useTranslation();
  const config = useAppStore((s) => s.config);
  const media = config?.media;
  const updateConfig = useConfigUpdater();

  if (!media) return null;

  return (
    <div className="max-w-2xl space-y-6">
      <SectionHeader title={t("settingsSections.imageTitle")} description={t("settingsSections.imageDesc")} />

      <GlassCard className="space-y-4">
        <ModelRefSelector label={t("settingsSections.imageModel")} value={media.image_model}
          onChange={(ref) => updateConfig((cfg) => { cfg.media.image_model = ref; })} capability="image" />
        <div className="grid grid-cols-2 gap-4">
          <div><Label>{t("settingsSections.quality")}</Label>
            <Select className="w-full" value={media.image_quality}
              onChange={(e) => updateConfig((cfg) => { cfg.media.image_quality = e.target.value; })}>
              <option value="auto">auto</option><option value="low">low</option><option value="medium">medium</option><option value="high">high</option>
            </Select>
          </div>
          <div><Label>{t("settingsSections.outputFormat")}</Label>
            <Select className="w-full" value={media.image_format}
              onChange={(e) => updateConfig((cfg) => { cfg.media.image_format = e.target.value; })}>
              <option value="png">PNG</option><option value="jpeg">JPEG</option><option value="webp">WebP</option>
            </Select>
          </div>
        </div>
        <div className="grid grid-cols-2 gap-4">
          <div><Label>{t("settingsSections.generateCount")}</Label>
            <Input type="number" min={1} max={5} value={media.image_count} className="w-24"
              onChange={(e) => updateConfig((cfg) => { cfg.media.image_count = Number(e.target.value) || 1; })} />
          </div>
          <div><Label>{t("settingsSections.background")}</Label>
            <Select className="w-full" value={media.image_background}
              onChange={(e) => updateConfig((cfg) => { cfg.media.image_background = e.target.value; })}>
              <option value="auto">auto</option><option value="opaque">opaque</option><option value="transparent">transparent</option>
            </Select>
          </div>
        </div>
      </GlassCard>

      <GlassCard className="space-y-4">
        <h3 className="text-sm font-semibold text-[var(--text-primary)]">{t("settingsSections.sizePresets")}</h3>
        <div className="grid grid-cols-4 gap-2">
          {["1024×1024", "1792×1024", "1024×1792", "1024×768", "768×1024"].map((s) => (
            <button key={s} onClick={() => updateConfig((cfg) => { cfg.media.image_size = s.replace("×", "x"); })}
              className={cn(
                "px-3 py-1.5 rounded-lg text-xs font-medium transition-all border",
                media.image_size === s.replace("×", "x")
                  ? "bg-brand-600/15 border-brand-500/30 text-[var(--active-text)]"
                  : "border-[var(--border-subtle)] text-[var(--text-muted)] hover:bg-[var(--hover-bg)]",
              )}>
              {s}
            </button>
          ))}
        </div>
      </GlassCard>
    </div>
  );
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  视频生成 (匹配 C# VideoGenSectionVM)
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

function VideoSection() {
  const { t } = useTranslation();
  const config = useAppStore((s) => s.config);
  const media = config?.media;
  const updateConfig = useConfigUpdater();

  if (!media) return null;

  return (
    <div className="max-w-2xl space-y-6">
      <SectionHeader title={t("settingsSections.videoTitle")} description={t("settingsSections.videoDesc")} />
      <GlassCard className="space-y-4">
        <ModelRefSelector label={t("settingsSections.videoModel")} value={media.video_model}
          onChange={(ref) => updateConfig((cfg) => { cfg.media.video_model = ref; })} capability="video" />
        <div className="grid grid-cols-2 gap-4">
          <div><Label>{t("settingsSections.aspectRatio")}</Label>
            <Select className="w-full" value={media.video_aspect_ratio}
              onChange={(e) => updateConfig((cfg) => { cfg.media.video_aspect_ratio = e.target.value; })}>
              <option>16:9</option><option>9:16</option><option>1:1</option><option>4:3</option><option>3:4</option>
            </Select>
          </div>
          <div><Label>{t("settingsSections.resolution")}</Label>
            <Select className="w-full" value={media.video_resolution}
              onChange={(e) => updateConfig((cfg) => { cfg.media.video_resolution = e.target.value; })}>
              <option>720p</option><option>1080p</option>
            </Select>
          </div>
        </div>
        <div className="grid grid-cols-2 gap-4">
          <div><Label>{t("settingsSections.duration")}</Label>
            <Input type="number" value={media.video_seconds} className="w-24"
              onChange={(e) => updateConfig((cfg) => { cfg.media.video_seconds = Number(e.target.value) || 5; })} />
          </div>
          <div><Label>{t("settingsSections.variants")}</Label>
            <Input type="number" min={1} max={4} value={media.video_variants} className="w-24"
              onChange={(e) => updateConfig((cfg) => { cfg.media.video_variants = Number(e.target.value) || 1; })} />
          </div>
        </div>
        <div><Label>{t("settingsSections.pollInterval")}</Label>
          <Input type="number" value={media.video_poll_interval_ms} className="w-32"
            onChange={(e) => updateConfig((cfg) => { cfg.media.video_poll_interval_ms = Number(e.target.value) || 3000; })} />
        </div>
      </GlassCard>
    </div>
  );
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  网页搜索 (匹配 C# WebSearchSectionVM)
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

function SearchSection() {
  const { t } = useTranslation();
  const config = useAppStore((s) => s.config);
  const ws = config?.web_search;
  const updateConfig = useConfigUpdater();

  if (!ws) return null;

  return (
    <div className="max-w-2xl space-y-6">
      <SectionHeader title={t("settingsSections.searchTitle")} description={t("settingsSections.searchDesc")} />
      <GlassCard className="space-y-4">
        <div><Label>{t("settingsSections.searchEngine")}</Label>
          <Select className="w-full" value={ws.provider_id}
            onChange={(e) => updateConfig((cfg) => { cfg.web_search.provider_id = e.target.value; })}>
            <option value="bing">Bing</option><option value="google">Google</option>
            <option value="duckduckgo">DuckDuckGo</option><option value="brave">Brave</option>
            <option value="mcp">{t("settingsSections.customMcp")}</option>
          </Select>
        </div>
        <div><Label>{t("settingsSections.triggerMode")}</Label>
          <Select className="w-full" value={ws.trigger_mode}
            onChange={(e) => updateConfig((cfg) => { cfg.web_search.trigger_mode = e.target.value; })}>
            <option value="auto">{t("settingsSections.triggerAuto")}</option>
            <option value="always">{t("settingsSections.triggerAlways")}</option>
            <option value="manual">{t("settingsSections.triggerManual")}</option>
          </Select>
        </div>
        <div><Label>{t("settingsSections.maxResults")}</Label>
          <Input type="number" value={ws.max_results} className="w-24"
            onChange={(e) => updateConfig((cfg) => { cfg.web_search.max_results = Number(e.target.value) || 5; })} />
        </div>
        <SettingRow label={t("settingsSections.aiIntentAnalysis")} description={t("settingsSections.aiIntentAnalysisDesc")}>
          <Switch checked={ws.enable_intent_analysis} onCheckedChange={(v) => updateConfig((cfg) => { cfg.web_search.enable_intent_analysis = v; })} />
        </SettingRow>
        <SettingRow label={t("settingsSections.resultCompression")} description={t("settingsSections.resultCompressionDesc")}>
          <Switch checked={ws.enable_result_compression} onCheckedChange={(v) => updateConfig((cfg) => { cfg.web_search.enable_result_compression = v; })} />
        </SettingRow>
      </GlassCard>

      <GlassCard className="space-y-4">
        <h3 className="text-sm font-semibold text-[var(--text-primary)]">{t("settingsSections.mcpConfig")}</h3>
        <div><Label>{t("settingsSections.mcpEndpointUrl")}</Label>
          <Input value={ws.mcp_endpoint} placeholder="https://mcp-server.example.com"
            onChange={(e) => updateConfig((cfg) => { cfg.web_search.mcp_endpoint = e.target.value; })} />
        </div>
        <div><Label>{t("settingsSections.toolName")}</Label>
          <Input value={ws.mcp_tool_name} placeholder="web_search"
            onChange={(e) => updateConfig((cfg) => { cfg.web_search.mcp_tool_name = e.target.value; })} />
        </div>
        <div><Label>API Key</Label>
          <Input type="password" value={ws.mcp_api_key} placeholder=""
            onChange={(e) => updateConfig((cfg) => { cfg.web_search.mcp_api_key = e.target.value; })} />
        </div>
        <SettingRow label={t("settingsSections.debugMode")} description={t("settingsSections.debugModeDesc")}>
          <Switch checked={ws.debug_mode} onCheckedChange={(v) => updateConfig((cfg) => { cfg.web_search.debug_mode = v; })} />
        </SettingRow>
      </GlassCard>
    </div>
  );
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  云服务 (匹配 C# CloudSectionVM)
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

function CloudSection() {
  const { t } = useTranslation();
  return (
    <div className="max-w-2xl space-y-6">
      <SectionHeader title={t("settingsSections.cloudTitle")} description={t("settingsSections.cloudDesc")} />

      <GlassCard className="space-y-4">
        <div><Label>{t("settingsSections.serviceMode")}</Label>
          <Select className="w-full">
            <option value="self_hosted">{t("settingsSections.selfHosted")}</option>
            <option value="cloud">{t("settingsSections.cloudMode")}</option>
          </Select>
        </div>
        <div><Label>{t("settingsSections.backendUrl")}</Label><Input placeholder="https://gateway.truefluent.pro" /></div>
      </GlassCard>

      <GlassCard className="space-y-4">
        <h3 className="text-sm font-semibold text-[var(--text-primary)]">{t("settingsSections.aadLogin")}</h3>
        <div><Label>{t("settingsSections.tenantId")}</Label><Input placeholder="00000000-0000-0000-0000-000000000000" /></div>
        <div><Label>{t("settingsSections.clientId")}</Label><Input placeholder="00000000-0000-0000-0000-000000000000" /></div>
        <div><Label>Scope</Label><Input placeholder="api://truefluent/.default" /></div>
        <div className="flex gap-2">
          <Button size="sm"><Shield size={14} /> {t("settingsSections.login")}</Button>
          <Button variant="secondary" size="sm"><Zap size={14} /> {t("settingsSections.healthCheck")}</Button>
        </div>
      </GlassCard>
    </div>
  );
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  导入导出 (匹配 C# TransferSectionVM)
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

function TransferSection() {
  const { t } = useTranslation();
  const [status, setStatus] = useState("");

  const handleExport = async (fullConfig: boolean) => {
    try {
      const json = await api.exportConfig();
      let data = json;
      if (!fullConfig) {
        const parsed = JSON.parse(json);
        data = JSON.stringify({ endpoints: parsed.endpoints, ai: parsed.ai, media: parsed.media }, null, 2);
      }
      const path = await dialogSave({
        title: fullConfig ? t("settingsSections.fullConfig") : t("settingsSections.basicAiConfig"),
        defaultPath: fullConfig ? "truefluent-config-full.json" : "truefluent-config.json",
        filters: [{ name: "JSON", extensions: ["json"] }],
      });
      if (!path) return;
      await api.writeTextFile(path, data);
      setStatus(`✓ ${path}`);
    } catch (e) {
      setStatus(`✗ ${e}`);
    }
  };

  const handleExportToClipboard = async (fullConfig: boolean) => {
    try {
      const json = await api.exportConfig();
      let data = json;
      if (!fullConfig) {
        const parsed = JSON.parse(json);
        data = JSON.stringify({ endpoints: parsed.endpoints, ai: parsed.ai, media: parsed.media }, null, 2);
      }
      await navigator.clipboard.writeText(data);
      setStatus("✓ OK");
    } catch (e) {
      setStatus(`✗ ${e}`);
    }
  };

  const handleImportFromClipboard = async () => {
    try {
      const json = await navigator.clipboard.readText();
      if (!json.trim()) { setStatus("✗ Empty"); return; }
      JSON.parse(json);
      const partial = JSON.parse(json);
      const currentJson = await api.exportConfig();
      const current = JSON.parse(currentJson);
      if (partial.endpoints) current.endpoints = partial.endpoints;
      if (partial.ai) current.ai = partial.ai;
      if (partial.media) current.media = partial.media;
      await api.importConfig(JSON.stringify(current));
      const cfg = await api.getConfig();
      useAppStore.getState().setConfig(cfg);
      await api.refreshProviders();
      setStatus("✓ OK");
    } catch (e) {
      setStatus(`✗ ${e}`);
    }
  };

  const handleImport = async (fullConfig: boolean) => {
    try {
      const path = await dialogOpen({
        title: fullConfig ? t("settingsSections.fullConfig") : t("settingsSections.basicAiConfig"),
        multiple: false,
        filters: [{ name: "JSON", extensions: ["json"] }],
      });
      if (!path) return;
      const filePath = typeof path === "string" ? path : (path as string[])[0];
      if (!filePath) return;
      const json = await api.readTextFile(filePath);
      if (fullConfig) {
        await api.importConfig(json);
      } else {
        const partial = JSON.parse(json);
        const currentJson = await api.exportConfig();
        const current = JSON.parse(currentJson);
        if (partial.endpoints) current.endpoints = partial.endpoints;
        if (partial.ai) current.ai = partial.ai;
        if (partial.media) current.media = partial.media;
        await api.importConfig(JSON.stringify(current));
      }
      const cfg = await api.getConfig();
      useAppStore.getState().setConfig(cfg);
      await api.refreshProviders();
      setStatus("✓ OK");
    } catch (e) {
      setStatus(`✗ ${e}`);
    }
  };

  return (
    <div className="max-w-2xl space-y-6">
      <SectionHeader title={t("settingsSections.transferTitle")} description={t("settingsSections.transferDesc")} />

      <GlassCard className="space-y-4">
        <h3 className="text-sm font-semibold text-[var(--text-primary)]">{t("settingsSections.basicAiConfig")}</h3>
        <p className="text-xs text-[var(--text-muted)]">{t("settingsSections.basicAiConfigDesc")}</p>
        <div className="flex gap-2">
          <Button variant="secondary" size="sm" onClick={() => handleExport(false)}><Download size={14} /> {t("settingsSections.exportBtn")}</Button>
          <Button variant="secondary" size="sm" onClick={() => handleImport(false)}><Upload size={14} /> {t("settingsSections.importBtn")}</Button>
        </div>
      </GlassCard>

      <GlassCard className="space-y-4">
        <h3 className="text-sm font-semibold text-[var(--text-primary)]">{t("settingsSections.fullConfig")}</h3>
        <p className="text-xs text-[var(--text-muted)]">{t("settingsSections.fullConfigDesc")}</p>
        <div className="flex gap-2">
          <Button variant="secondary" size="sm" onClick={() => handleExport(true)}><Download size={14} /> {t("settingsSections.exportAll")}</Button>
          <Button variant="secondary" size="sm" onClick={() => handleImport(true)}><Upload size={14} /> {t("settingsSections.importAll")}</Button>
        </div>
      </GlassCard>

      <GlassCard className="space-y-4">
        <h3 className="text-sm font-semibold text-[var(--text-primary)]">{t("settingsSections.configMigration")}</h3>
        <p className="text-xs text-[var(--text-muted)]">{t("settingsSections.configMigrationDesc")}</p>
        <div className="flex gap-2 flex-wrap">
          <Button variant="secondary" size="sm" onClick={() => handleExportToClipboard(true)}><Copy size={14} /> {t("settingsSections.copyAiConfig")}</Button>
          <Button variant="secondary" size="sm" onClick={() => handleImportFromClipboard()}><Clipboard size={14} /> {t("settingsSections.importFromClipboard")}</Button>
        </div>
      </GlassCard>

      {status && (
        <p className={cn("text-xs", status.startsWith("✓") ? "text-emerald-500" : "text-red-400")}>{status}</p>
      )}
    </div>
  );
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  界面设置 (匹配 C# ThemeMode + UI 参数)
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

function UiSection() {
  const { t, i18n } = useTranslation();
  const themeMode = useThemeStore((s) => s.mode);
  const setMode = useThemeStore((s) => s.setMode);
  const fontSize = useThemeStore((s) => s.fontSize);
  const setFontSize = useThemeStore((s) => s.setFontSize);
  const transitionDuration = useThemeStore((s) => s.transitionDuration);
  const setTransitionDuration = useThemeStore((s) => s.setTransitionDuration);

  return (
    <div className="max-w-2xl space-y-6">
      <SectionHeader title={t("settingsSections.uiTitle")} description={t("settingsSections.uiDesc")} />

      <GlassCard className="space-y-4">
        <h3 className="text-sm font-semibold text-[var(--text-primary)]">{t("settingsUi.themeLabel")}</h3>
        <div className="flex gap-3">
          {(["system", "light", "dark"] as ThemeMode[]).map((m) => (
            <button
              key={m}
              onClick={() => setMode(m)}
              className={cn(
                "flex items-center gap-2 px-4 py-3 rounded-xl border transition-all duration-200",
                themeMode === m
                  ? "border-brand-500 bg-brand-600/10 text-[var(--active-text)]"
                  : "border-[var(--border-medium)] text-[var(--text-muted)] hover:border-[var(--text-muted)]",
              )}
            >
              {m === "system" && <Monitor size={16} />}
              {m === "light" && <Sun size={16} />}
              {m === "dark" && <Moon size={16} />}
              <span className="text-sm">{m === "system" ? t("settingsUi.themeSystem") : m === "light" ? t("settingsUi.themeLight") : t("settingsUi.themeDark")}</span>
            </button>
          ))}
        </div>
      </GlassCard>

      <GlassCard className="space-y-4">
        <div>
          <Label>{t("settingsUi.fontSizeLabel")} ({fontSize}px)</Label>
          <Input className="w-24" type="number" min={12} max={20} value={fontSize}
            onChange={(e) => setFontSize(Number(e.target.value) || 14)} />
        </div>
        <div>
          <Label>{t("settingsUi.transitionLabel")} ({transitionDuration}ms)</Label>
          <div className="flex items-center gap-3">
            <input type="range" min={0} max={500} step={10} value={transitionDuration}
              onChange={(e) => setTransitionDuration(Number(e.target.value))}
              className="flex-1 accent-brand-500" />
            <Input className="w-20" type="number" min={0} max={1000} value={transitionDuration}
              onChange={(e) => setTransitionDuration(Number(e.target.value) || 0)} />
          </div>
          <p className="text-[10px] text-[var(--text-muted)] mt-1">{t("settingsUi.transitionHint")}</p>
        </div>
        <div><Label>{t("settingsUi.languageLabel")}</Label>
          <Select className="w-40" value={i18n.language} onChange={(e) => i18n.changeLanguage(e.target.value)}>
            <option value="zh-CN">简体中文</option><option value="en">English</option>
          </Select>
        </div>
      </GlassCard>
    </div>
  );
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  关于 (匹配 C# AboutSectionVM)
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

function AboutSection() {
  const { t } = useTranslation();
  return (
    <div className="max-w-2xl space-y-6">
      <SectionHeader title={t("settingsSections.aboutTitle")} />

      <GlassCard className="space-y-3">
        <div className="flex items-center gap-3 mb-2">
          <span className="text-xl font-bold text-gradient">{t("settingsAbout.appName")}</span>
          <Badge variant="blue">v0.1.0</Badge>
        </div>
        <p className="text-sm text-[var(--text-secondary)]">
          {t("settingsAbout.appDesc")}
        </p>
      </GlassCard>

      <GlassCard className="space-y-4">
        <h3 className="text-sm font-semibold text-[var(--text-primary)]">{t("settingsAbout.techStackTitle")}</h3>
        <div className="text-sm text-[var(--text-secondary)] space-y-1">
          <p>{t("settingsAbout.techFrontend")}</p>
          <p>{t("settingsAbout.techBackend")}</p>
          <p>{t("settingsAbout.techStorage")}</p>
          <p>{t("settingsAbout.techProvider")}</p>
        </div>
      </GlassCard>

      <GlassCard className="space-y-4">
        <SettingRow label={t("settingsSections.autoUpdate")} description={t("settingsSections.autoUpdateDesc")}><Switch defaultChecked /></SettingRow>
      </GlassCard>
    </div>
  );
}
