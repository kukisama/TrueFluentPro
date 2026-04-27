import { useState, useCallback, useEffect, useRef } from "react";
import { save as dialogSave, open as dialogOpen } from "@tauri-apps/plugin-dialog";
import { listen } from "@tauri-apps/api/event";
import {
  Plus, Trash2, Edit2, Check, X, Globe, Brain, Image, Volume2,
  TestTube, Monitor, Cloud, FileText,
  Mic, Search, Video, ArrowUpDown, Download, Upload, Info,
  Sun, Moon, Shield, Zap, Loader2, ChevronRight, ChevronLeft,
  CheckCircle2, XCircle, SkipForward,
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
const VENDOR_BADGE: Record<string, { label: string; badge: string; icon: string; color: string }> = {
  azure_open_ai:          { label: "Azure OpenAI",  badge: "AZ", icon: "/icons/azure-openai.svg",       color: "blue" },
  api_management_gateway: { label: "APIM 网关",     badge: "AP", icon: "/icons/apim-gateway.svg",       color: "purple" },
  open_ai_compatible:     { label: "OpenAI 兼容",   badge: "OA", icon: "/icons/openai-compatible.svg",  color: "green" },
  azure_speech:           { label: "Azure Speech",   badge: "SP", icon: "/icons/azure-speech.svg",       color: "yellow" },
  azure_translator:       { label: "Azure Translator",badge: "TR", icon: "/icons/azure-openai.svg",     color: "blue" },
  deep_l:                 { label: "DeepL",          badge: "DL", icon: "/icons/openai-compatible.svg",  color: "blue" },
  tencent_cloud:          { label: "腾讯云",         badge: "TC", icon: "/icons/openai-compatible.svg",  color: "blue" },
  alibaba_cloud:          { label: "阿里云",         badge: "AL", icon: "/icons/openai-compatible.svg",  color: "orange" },
  custom:                 { label: "自定义",         badge: "CU", icon: "/icons/openai-compatible.svg",  color: "gray" },
};

const CAPABILITY_LABELS: Record<string, string> = {
  text: "文字", image: "图片", video: "视频",
  speech_to_text: "语音识别", text_to_speech: "语音合成",
};

const CAPABILITY_ICONS: Record<string, typeof Globe> = {
  text: FileText, image: Image, video: Video,
  speech_to_text: Mic, text_to_speech: Volume2,
};

const CAPABILITY_COLORS: Record<string, string> = {
  text: "var(--cap-text)", image: "var(--cap-image)", video: "var(--cap-video)",
  speech_to_text: "var(--cap-stt)", text_to_speech: "var(--cap-tts)",
};

const CAPABILITY_TIPS: Record<string, string> = {
  text: "文字对话：洞察、复盘、快问、会话",
  image: "图片生成：AI 绘图、图片编辑",
  video: "视频生成：AI 视频创作",
  speech_to_text: "语音转文字：音频转写、实时听写",
  text_to_speech: "文字转语音：语音合成、朗读",
};

const ALL_CAPABILITIES: ModelCapability[] = ["text", "image", "video", "speech_to_text", "text_to_speech"];

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  端点管理（对齐 C# EndpointsSectionVM + EndpointBatchTestService）
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

function EndpointsSection() {
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
        title="端点管理"
        description="注册 AI 服务端点，支持 Azure OpenAI、OpenAI 兼容、Azure Speech、APIM 网关等"
        action={
          <Button size="sm" onClick={() => setShowCreate(true)}>
            <Plus size={14} /> 添加端点
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
          title="暂无端点配置"
          description="添加 Azure OpenAI、OpenAI 兼容等服务端点以开始使用"
          action={
            <Button size="sm" onClick={() => setShowCreate(true)}>
              <Plus size={14} /> 添加端点
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
                      <img src={vendor.icon} alt={vendor.label} className="w-6 h-6 object-contain" />
                    </div>
                    <div className="flex-1 min-w-0">
                      <div className="flex items-center gap-2">
                        <div className={cn(
                          "w-2 h-2 rounded-full shrink-0",
                          ep.enabled ? "bg-emerald-400 shadow-sm shadow-emerald-400/50" : "bg-[var(--text-muted)]",
                        )} />
                        <span className="font-medium text-[var(--text-primary)] truncate">{ep.name}</span>
                        <Badge variant={(vendor.color === "orange" ? "amber" : vendor.color === "purple" ? "blue" : vendor.color === "yellow" ? "amber" : vendor.color) as "blue" | "green" | "red" | "amber" | "gray" | undefined}>{vendor.badge}</Badge>
                        <span className="text-xs text-[var(--text-muted)]">{vendor.label}</span>
                      </div>
                      {/* Speech 端点显示区域+终结点，AI 端点显示 URL */}
                      {isSpeechEp ? (
                        <div className="mt-0.5 pl-4 space-y-0.5">
                          <p className="text-xs text-[var(--text-muted)] truncate">
                            区域: <span className="text-[var(--text-secondary)]">{ep.speech_region || "未配置"}</span>
                            {ep.speech_region && (
                              <span className="ml-2 text-emerald-400 text-[10px]">
                                ✓ {ep.speech_endpoint?.includes(".azure.cn") || ep.speech_region.startsWith("china") ? "中国区" : "国际版"}
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
                        <span className="font-medium">{isTesting ? "测试进行中" : "测试报告"}</span>
                        {isTesting && progress && (
                          <span className="flex items-center gap-1.5 ml-2 text-[10px]">
                            <span className="text-blue-400">共 {progress.total_count} 项</span>
                            {progress.running_count > 0 && <span className="text-yellow-400">⟳{progress.running_count}</span>}
                            {progress.success_count > 0 && <span className="text-emerald-500">✓{progress.success_count}</span>}
                            {progress.failed_count > 0 && <span className="text-red-400">✗{progress.failed_count}</span>}
                            {progress.pending_count > 0 && <span className="text-[var(--text-muted)]">…{progress.pending_count}</span>}
                          </span>
                        )}
                        {!isTesting && report && <TestReportSummaryBadges items={report.items} />}
                        <span className="ml-auto text-[var(--text-muted)] text-[10px]">
                          {isTesting
                            ? `已运行 ${String(Math.floor((elapsed[ep.id] || 0) / 60)).padStart(2, "0")}:${String((elapsed[ep.id] || 0) % 60).padStart(2, "0")}`
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

const STATUS_CONFIG: Record<string, { icon: React.ReactNode; bg: string; text: string; label: string }> = {
  pending: { icon: <div className="w-3 h-3 rounded-full border border-[var(--text-muted)] shrink-0" />, bg: "bg-[var(--surface-2)]", text: "text-[var(--text-muted)]", label: "等待" },
  running: { icon: <Loader2 size={13} className="text-blue-400 animate-spin shrink-0" />, bg: "bg-blue-500/10", text: "text-blue-400", label: "运行中" },
  success: { icon: <CheckCircle2 size={13} className="text-emerald-500 shrink-0" />, bg: "bg-emerald-500/15", text: "text-emerald-500", label: "通过" },
  failed: { icon: <XCircle size={13} className="text-red-400 shrink-0" />, bg: "bg-red-400/15", text: "text-red-400", label: "失败" },
  skipped: { icon: <SkipForward size={13} className="text-[var(--text-muted)] shrink-0" />, bg: "bg-[var(--surface-2)]", text: "text-[var(--text-muted)]", label: "跳过" },
};

function TestResultRow({ item }: { item: EndpointTestItem }) {
  const [expanded, setExpanded] = useState(false);
  const sc = STATUS_CONFIG[item.status] ?? STATUS_CONFIG.pending;

  return (
    <div className={cn("rounded-md px-2.5 py-1.5 transition-colors", item.status === "running" ? "bg-blue-500/5 border border-blue-500/20" : "bg-[var(--surface-1)]")}>
      <button className="flex items-center gap-2 w-full text-left" onClick={() => setExpanded(!expanded)}>
        {sc.icon}
        <span className="text-xs text-[var(--text-primary)] flex-1">{item.summary}</span>
        <span className={cn("text-[10px] px-1.5 py-0.5 rounded shrink-0", sc.bg, sc.text)}>
          {item.capability} · {sc.label}
        </span>
        {item.duration_ms > 0 && (
          <span className="text-[10px] text-[var(--text-muted)] shrink-0 ml-1">
            耗时 {item.duration_ms < 1000 ? `${item.duration_ms}ms` : `${(item.duration_ms / 1000).toFixed(1)}s`}
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
              <p className="text-[10px] font-medium text-[var(--text-muted)] mb-0.5">最终访问 URL</p>
              <p className="text-[11px] text-[var(--text-secondary)] break-all font-mono">{item.request_url}</p>
            </div>
          )}
          {item.request_summary && (
            <div className="rounded bg-[var(--surface-2)] p-2">
              <p className="text-[10px] font-medium text-[var(--text-muted)] mb-0.5">请求摘要</p>
              <p className="text-[11px] text-[var(--text-secondary)] whitespace-pre-wrap">{item.request_summary}</p>
            </div>
          )}
          {item.detail && (
            <div className="rounded bg-[var(--surface-2)] p-2">
              <p className="text-[10px] font-medium text-[var(--text-muted)] mb-0.5">详细信息</p>
              <p className="text-[11px] text-[var(--text-secondary)] whitespace-pre-wrap break-all">{item.detail}</p>
            </div>
          )}
          {item.urls_tried && item.urls_tried.length > 1 && (
            <div className="rounded bg-[var(--surface-2)] p-2">
              <p className="text-[10px] font-medium text-[var(--text-muted)] mb-0.5">URL 地址 (共 {item.urls_tried.length} 条资料包声明{item.test_branch ? `，${item.test_branch}` : ""})</p>
              {item.urls_tried.map((u, i) => (
                <p key={i} className="text-[11px] text-[var(--text-secondary)] break-all font-mono">
                  {i === 0 ? "* " : ""}{`地址 ${i + 1}`}: {u}
                </p>
              ))}
            </div>
          )}
          {!item.detail && !item.request_url && !item.request_summary && (
            <p className="text-[11px] text-[var(--text-muted)] italic">无额外诊断信息</p>
          )}
        </div>
      )}
    </div>
  );
}

/* ─── 新建端点表单 ─── */

/* ─── 模型引用选择器（对齐 C# ModelOption 二级下拉） ─── */

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
  const config = useAppStore((s) => s.config);
  const endpoints = config?.endpoints ?? [];

  // 构建 "端点名/模型名" 列表（对齐 C# BuildModelOptions）
  const options = endpoints
    .filter((ep) => ep.enabled && ep.endpoint_type !== "azure_speech")
    .flatMap((ep) =>
      ep.models
        .filter((m) => !capability || m.capabilities.includes(capability))
        .map((m) => ({
          endpoint_id: ep.id,
          model_id: m.model_id,
          display: `${ep.name} / ${m.display_name || m.model_id}`,
        })),
    );

  const currentKey = `${value.endpoint_id}|${value.model_id}`;

  return (
    <div>
      <Label>{label}</Label>
      <Select
        className="w-full"
        value={currentKey}
        onChange={(e) => {
          const [eid, mid] = e.target.value.split("|");
          onChange({ endpoint_id: eid || "", model_id: mid || "" });
        }}
      >
        <option value="|">未配置</option>
        {options.map((o) => (
          <option key={`${o.endpoint_id}|${o.model_id}`} value={`${o.endpoint_id}|${o.model_id}`}>
            {o.display}
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
        <h3 className="text-sm font-semibold text-[var(--text-primary)]">新建端点</h3>
      </div>

      {/* 名称 + 类型 */}
      <div className="grid grid-cols-2 gap-4">
        <div>
          <Label>名称</Label>
          <Input value={form.name} onChange={(e) => update("name", e.target.value)} placeholder={isSpeech ? "东南亚" : "我的 AI 端点"} />
        </div>
        <div>
          <Label>类型</Label>
          <Select className="w-full" value={form.endpoint_type} onChange={(e) => update("endpoint_type", e.target.value)}>
            {Object.entries(VENDOR_BADGE).map(([k, v]) => (
              <option key={k} value={k}>{v.badge} {v.label}</option>
            ))}
          </Select>
        </div>
      </div>

      {isSpeech ? (
        /* ═══ Speech 端点表单 ═══ */
        <div className="space-y-4 mt-4">
          <div className="rounded-lg bg-amber-500/10 border border-amber-500/20 p-3">
            <p className="text-xs text-[var(--text-secondary)]">
              🎤 Azure Speech 端点使用订阅密钥 + 区域连接，不需要配置模型列表。
              密钥可在 Azure 门户 → 语音服务 → 密钥和终结点中获取。
            </p>
          </div>
          <div>
            <Label>Speech 终结点</Label>
            <Input value={form.speech_endpoint}
              onChange={(e) => update("speech_endpoint", e.target.value)}
              placeholder="https://southeastasia.api.cognitive.microsoft.com" />
            <p className="text-[10px] text-[var(--text-muted)] mt-0.5">完整终结点 URL，留空则根据区域自动生成</p>
          </div>
          <div className="grid grid-cols-2 gap-4">
            <div>
              <Label>订阅密钥</Label>
              <Input type="password" value={form.speech_subscription_key}
                onChange={(e) => update("speech_subscription_key", e.target.value)}
                placeholder="Azure Speech 订阅密钥" />
            </div>
            <div>
              <Label>区域</Label>
              <Input value={form.speech_region}
                onChange={(e) => update("speech_region", e.target.value)}
                placeholder="southeastasia" />
              {form.speech_region && (
                <p className="text-[10px] mt-0.5 text-emerald-400">
                  ✓ {form.speech_endpoint?.includes(".azure.cn") || form.speech_region.startsWith("china") ? "中国区" : "国际版"}
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
              <Label>端点 URL</Label>
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
              >API Key（默认）</button>
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
                <Label>区域 {form.endpoint_type.startsWith("azure") ? "(AZURE)" : "(可选)"}</Label>
                <Input value={form.region} onChange={(e) => update("region", e.target.value)} placeholder="eastus" />
              </div>
            </div>
          )}

          {/* AAD 面板 — AAD 模式时显示（对齐 C# IsSelectedEndpointAad） */}
          {form.auth_mode === "aad" && (
            <div className="grid grid-cols-2 gap-4 mt-2">
              <div>
                <Label>Tenant ID</Label>
                <Input value={form.azure_tenant_id} onChange={(e) => update("azure_tenant_id", e.target.value)} placeholder="留空则自动检测（多租户时需选择）" />
              </div>
              <div>
                <Label>Client ID（可选）</Label>
                <Input value={form.azure_client_id} onChange={(e) => update("azure_client_id", e.target.value)} placeholder="留空使用默认应用" />
              </div>
              <div className="col-span-2">
                <div className="rounded-lg bg-blue-500/10 border border-blue-500/20 p-3">
                  <p className="text-xs text-[var(--text-secondary)]">
                    🔐 AAD 登录功能即将上线。配置 Tenant ID 后可通过 Microsoft Entra ID 进行交互式认证，无需 API Key。
                  </p>
                </div>
              </div>
            </div>
          )}

          {/* API 版本 — 有默认值的类型才显示（对齐 C# 各 profile 的 defaults.apiVersion） */}
          {activeProfile && activeProfile.default_api_version && (
            <div className="mt-4" style={{ maxWidth: "50%" }}>
              <Label>API 版本</Label>
              <Input value={form.api_version} onChange={(e) => update("api_version", e.target.value)} placeholder={activeProfile.default_api_version} />
            </div>
          )}

          <Separator className="my-4" />

          {/* ─ 模型列表 ─ */}
          <h4 className="text-xs font-semibold text-[var(--text-primary)] mb-2">模型列表</h4>

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
                              {CAPABILITY_LABELS[c]}
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
                              <button key={c} title={CAPABILITY_TIPS[c]}
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
                                {CAPABILITY_LABELS[c]}
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
                placeholder={form.endpoint_type === "azure_open_ai" ? "部署名称 (如 gpt-4o)" : "模型 ID (如 gpt-4o)"}
                onKeyDown={(e) => e.key === "Enter" && addModel()} />
            </div>
            <div className="flex gap-1">
              {ALL_CAPABILITIES.map((c) => {
                const Icon = CAPABILITY_ICONS[c];
                const color = CAPABILITY_COLORS[c];
                const isActive = newModelCaps.includes(c);
                return (
                  <button key={c} title={CAPABILITY_TIPS[c]}
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
                {discovering ? " 发现中..." : " 从终结点拉取模型"}
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
                        {models.some((x) => x.model_id === m.id) ? "已添加" : "+ 添加"}
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
        <Button variant="secondary" size="sm" onClick={onClose}><X size={14} /> 取消</Button>
        <Button size="sm" onClick={handleSave} disabled={!canSave}>
          <Check size={14} /> 保存
        </Button>
      </div>
    </GlassCard>
  );
}

/* ─── 内联编辑面板（即时保存模式） ─── */

function EndpointEditPanel({ endpoint, onClose }: { endpoint: AiEndpoint; onClose: () => void }) {
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
          <span>返回</span>
        </button>
        <span className="flex-1" />
        {saving && <span className="text-[10px] text-[var(--text-muted)] animate-pulse">保存中...</span>}
        <SettingRow label="启用" description="">
          <Switch checked={form.enabled} onCheckedChange={(v) => update("enabled", v)} />
        </SettingRow>
      </div>

      {/* ── 基本信息 ── */}
      <div className="grid grid-cols-2 gap-3">
        <div><Label>名称</Label><Input value={form.name} onBlur={() => scheduleSave()} onChange={(e) => setForm((s) => ({ ...s, name: e.target.value }))} /></div>
        <div>
          <Label>端点类型</Label>
          <Select className="w-full" value={form.endpoint_type} onChange={(e) => update("endpoint_type", e.target.value)}>
            {Object.entries(VENDOR_BADGE).map(([k, v]) => (
              <option key={k} value={k}>{v.label}</option>
            ))}
          </Select>
        </div>
      </div>

      {isSpeech ? (
        /* ═══ Speech 编辑表单 ═══ */
        <div className="space-y-3">
          <div className="grid grid-cols-2 gap-3">
            <div><Label>区域</Label><Input value={form.speech_region} onChange={(e) => setForm((s) => ({ ...s, speech_region: e.target.value }))} onBlur={() => scheduleSave()} placeholder="southeastasia" /></div>
          </div>
          <div><Label>Speech 终结点</Label><Input value={form.speech_endpoint} onChange={(e) => setForm((s) => ({ ...s, speech_endpoint: e.target.value }))} onBlur={() => scheduleSave()} placeholder="https://region.api.cognitive.microsoft.com" /></div>
          <div><Label>订阅密钥</Label><Input type="password" value={form.speech_subscription_key} onChange={(e) => setForm((s) => ({ ...s, speech_subscription_key: e.target.value }))} onBlur={() => scheduleSave()} /></div>
        </div>
      ) : (
        /* ═══ AI 编辑表单 ═══ */
        <>
          <div className="grid grid-cols-2 gap-3">
            <div><Label>端点 URL</Label><Input value={form.url} onChange={(e) => setForm((s) => ({ ...s, url: e.target.value }))} onBlur={() => scheduleSave()} /></div>
            <div><Label>API Key</Label><Input type="password" value={form.api_key} onChange={(e) => setForm((s) => ({ ...s, api_key: e.target.value }))} onBlur={() => scheduleSave()} /></div>
            <div><Label>API 版本</Label><Input value={form.api_version || ""} onChange={(e) => setForm((s) => ({ ...s, api_version: e.target.value }))} onBlur={() => scheduleSave()} /></div>
            <div><Label>区域</Label><Input value={form.region || ""} onChange={(e) => setForm((s) => ({ ...s, region: e.target.value }))} onBlur={() => scheduleSave()} /></div>
            <div>
              <Label>认证方式</Label>
              <Select className="w-full" value={form.auth_header_mode} onChange={(e) => update("auth_header_mode", e.target.value)}>
                <option value="api_key">api-key Header</option>
                <option value="bearer">Bearer Token</option>
              </Select>
            </div>
          </div>

          <Separator />

          {/* ── 模型列表 ── */}
          <div className="flex items-center justify-between">
            <h4 className="text-xs font-semibold text-[var(--text-primary)]">模型列表</h4>
            <span className="text-[10px] text-[var(--text-muted)]">点击模型条目可修改能力</span>
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
                            {CAPABILITY_LABELS[c]}
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
                            <button key={c} title={CAPABILITY_TIPS[c]}
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
                              {CAPABILITY_LABELS[c]}
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
            <Input value={newModelId} onChange={(e) => setNewModelId(e.target.value)} placeholder="模型 ID / 部署名" className="flex-1"
              onKeyDown={(e) => e.key === "Enter" && addModel()} />
            <div className="flex gap-1">
              {ALL_CAPABILITIES.map((c) => {
                const Icon = CAPABILITY_ICONS[c];
                const color = CAPABILITY_COLORS[c];
                const isActive = newModelCaps.includes(c);
                return (
                  <button key={c} title={CAPABILITY_TIPS[c]}
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
              {discovering ? " 发现中..." : " 从终结点拉取模型"}
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
                    {models.some((x) => x.model_id === m.id) ? "已添加" : "+ 添加"}
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
//  主入口 — SettingsView
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

export function SettingsView() {
  const [activeTab, setActiveTab] = useState<SettingsTab>("endpoints");

  const tabs: { id: SettingsTab; label: string; icon: React.ReactNode; group: string }[] = [
    { id: "endpoints", label: "端点管理", icon: <Globe size={15} />, group: "connect" },
    { id: "recognition", label: "识别与翻译", icon: <Mic size={15} />, group: "voice" },
    { id: "storage", label: "存储与导出", icon: <FileText size={15} />, group: "voice" },
    { id: "audio", label: "音频预处理", icon: <Volume2 size={15} />, group: "voice" },
    { id: "insight", label: "AI 洞察", icon: <Brain size={15} />, group: "ai" },
    { id: "image", label: "图片生成", icon: <Image size={15} />, group: "ai" },
    { id: "video", label: "视频生成", icon: <Video size={15} />, group: "ai" },
    { id: "search", label: "网页搜索", icon: <Search size={15} />, group: "ai" },
    { id: "cloud", label: "云服务", icon: <Cloud size={15} />, group: "system" },
    { id: "transfer", label: "导入导出", icon: <ArrowUpDown size={15} />, group: "system" },
    { id: "ui", label: "界面设置", icon: <Monitor size={15} />, group: "system" },
    { id: "about", label: "关于", icon: <Info size={15} />, group: "system" },
  ];

  const groups = [
    { key: "connect", label: "连接" },
    { key: "voice", label: "语音 & 翻译" },
    { key: "ai", label: "AI" },
    { key: "system", label: "系统" },
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
            {tabs.filter((t) => t.group === group.key).map((tab) => (
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
  const config = useAppStore((s) => s.config);
  const rec = config?.recognition;
  const updateConfig = useConfigUpdater();

  if (!rec) return null;

  return (
    <div className="max-w-2xl space-y-6">
      <SectionHeader title="识别与翻译" description="语音识别参数、翻译语言、语气词过滤等" />

      <GlassCard className="space-y-4">
        <h3 className="text-sm font-semibold text-[var(--text-primary)]">翻译语言</h3>
        <div className="grid grid-cols-2 gap-4">
          <div>
            <Label>默认源语言</Label>
            <Select className="w-full" value={config?.default_source_lang || "zh-Hans"}
              onChange={(e) => updateConfig((cfg) => { cfg.default_source_lang = e.target.value; })}>
              <option value="auto">自动检测</option><option value="zh-Hans">中文（简体）</option><option value="en">English</option><option value="ja">日本語</option>
            </Select>
          </div>
          <div>
            <Label>默认目标语言</Label>
            <Select className="w-full" value={config?.default_target_langs[0] || "en"}
              onChange={(e) => updateConfig((cfg) => { cfg.default_target_langs = [e.target.value]; })}>
              <option value="en">English</option><option value="ja">日本語</option><option value="zh-Hans">中文（简体）</option><option value="ko">한국어</option>
            </Select>
          </div>
        </div>
      </GlassCard>

      <GlassCard className="space-y-4">
        <h3 className="text-sm font-semibold text-[var(--text-primary)]">语音识别</h3>
        <SettingRow label="语气词过滤" description="自动移除'嗯、啊、那个'等语气词">
          <Switch checked={rec.filter_modal_particles} onCheckedChange={(v) => updateConfig((cfg) => { cfg.recognition.filter_modal_particles = v; })} />
        </SettingRow>
        <div className="grid grid-cols-2 gap-4">
          <div><Label>历史条数上限</Label>
            <Input type="number" value={rec.max_history_items} className="w-32"
              onChange={(e) => updateConfig((cfg) => { cfg.recognition.max_history_items = Number(e.target.value) || 500; })} />
          </div>
          <div><Label>实时最大长度</Label>
            <Input type="number" value={rec.realtime_max_length} className="w-32"
              onChange={(e) => updateConfig((cfg) => { cfg.recognition.realtime_max_length = Number(e.target.value) || 150; })} />
          </div>
        </div>
      </GlassCard>

      <GlassCard className="space-y-4">
        <h3 className="text-sm font-semibold text-[var(--text-primary)]">超时与静默</h3>
        <SettingRow label="启用自动超时" description="静默超过阈值后自动停止识别">
          <Switch checked={rec.enable_auto_timeout} onCheckedChange={(v) => updateConfig((cfg) => { cfg.recognition.enable_auto_timeout = v; })} />
        </SettingRow>
        <div className="grid grid-cols-2 gap-4">
          <div><Label>初始静默超时 (秒)</Label>
            <Input type="number" value={rec.initial_silence_timeout_seconds} className="w-32"
              onChange={(e) => updateConfig((cfg) => { cfg.recognition.initial_silence_timeout_seconds = Number(e.target.value) || 25; })} />
          </div>
          <div><Label>段间超时 (秒)</Label>
            <Input type="number" value={rec.end_silence_timeout_seconds} className="w-32"
              onChange={(e) => updateConfig((cfg) => { cfg.recognition.end_silence_timeout_seconds = Number(e.target.value) || 1; })} />
          </div>
        </div>
      </GlassCard>
    </div>
  );
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  存储与导出 (匹配 C# StorageSectionVM)
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

function StorageSection() {
  const config = useAppStore((s) => s.config);
  const storage = config?.storage;
  const updateConfig = useConfigUpdater();
  const [validating, setValidating] = useState(false);
  const [validationMsg, setValidationMsg] = useState("");

  const handleValidateStorage = async () => {
    if (!storage?.batch_storage_connection_string?.trim()) {
      setValidationMsg("请填写存储账号连接字符串");
      return;
    }
    setValidating(true);
    setValidationMsg("验证中...");
    try {
      // 通过后端验证连接字符串
      await api.validateStorageConnection(storage.batch_storage_connection_string);
      setValidationMsg("✓ 存储账号验证成功");
      updateConfig((cfg) => { cfg.storage.batch_storage_is_valid = true; });
    } catch (e) {
      setValidationMsg(`✗ 验证失败: ${e}`);
      updateConfig((cfg) => { cfg.storage.batch_storage_is_valid = false; });
    } finally {
      setValidating(false);
    }
  };

  if (!storage) return null;

  return (
    <div className="max-w-2xl space-y-6">
      <SectionHeader title="存储与导出" description="Azure Blob 存储、录音保存、字幕导出" />

      <GlassCard className="space-y-4">
        <h3 className="text-sm font-semibold text-[var(--text-primary)]">Azure Blob 存储（批量处理）</h3>
        <p className="text-xs text-[var(--text-muted)]">批量语音转写需要 Azure Blob 存储来上传音频和获取结果</p>
        <div>
          <Label>连接字符串</Label>
          <Input type="password" value={storage.batch_storage_connection_string}
            onChange={(e) => updateConfig((cfg) => {
              cfg.storage.batch_storage_connection_string = e.target.value;
              cfg.storage.batch_storage_is_valid = false;
            })}
            placeholder="DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;EndpointSuffix=..." />
        </div>
        <div className="grid grid-cols-2 gap-4">
          <div><Label>音频容器名</Label>
            <Input value={storage.batch_audio_container_name}
              onChange={(e) => updateConfig((cfg) => { cfg.storage.batch_audio_container_name = e.target.value; })} />
          </div>
          <div><Label>结果容器名</Label>
            <Input value={storage.batch_result_container_name}
              onChange={(e) => updateConfig((cfg) => { cfg.storage.batch_result_container_name = e.target.value; })} />
          </div>
        </div>
        <div className="flex items-center gap-3">
          <Button size="sm" variant="secondary" onClick={handleValidateStorage} disabled={validating}>
            {validating ? <Loader2 size={12} className="animate-spin" /> : <TestTube size={12} />}
            {validating ? " 验证中..." : " 验证连接"}
          </Button>
          {storage.batch_storage_is_valid && (
            <Badge variant="green">已验证</Badge>
          )}
          {validationMsg && (
            <span className={cn("text-xs", validationMsg.startsWith("✓") ? "text-emerald-500" : validationMsg.startsWith("✗") ? "text-red-400" : "text-[var(--text-muted)]")}>
              {validationMsg}
            </span>
          )}
        </div>
      </GlassCard>

      <GlassCard className="space-y-4">
        <h3 className="text-sm font-semibold text-[var(--text-primary)]">录音</h3>
        <SettingRow label="启用录音" description="翻译时同步录制音频">
          <Switch checked={storage.enable_recording} onCheckedChange={(v) => updateConfig((cfg) => { cfg.storage.enable_recording = v; })} />
        </SettingRow>
        <div><Label>MP3 码率 (kbps)</Label>
          <Select className="w-40" value={String(storage.recording_mp3_bitrate_kbps)}
            onChange={(e) => updateConfig((cfg) => { cfg.storage.recording_mp3_bitrate_kbps = Number(e.target.value); })}>
            <option value="128">128</option><option value="192">192</option><option value="256">256</option><option value="320">320</option>
          </Select>
        </div>
      </GlassCard>

      <GlassCard className="space-y-4">
        <h3 className="text-sm font-semibold text-[var(--text-primary)]">字幕导出</h3>
        <SettingRow label="导出 SRT 格式" description="SubRip 字幕格式">
          <Switch checked={storage.export_srt_subtitles} onCheckedChange={(v) => updateConfig((cfg) => { cfg.storage.export_srt_subtitles = v; })} />
        </SettingRow>
        <SettingRow label="导出 VTT 格式" description="WebVTT 字幕格式">
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
  return (
    <div className="max-w-2xl space-y-6">
      <SectionHeader title="音频预处理" description="WebRTC APM 参数、设备选择" />

      <GlassCard className="space-y-4">
        <h3 className="text-sm font-semibold text-[var(--text-primary)]">设备</h3>
        <div><Label>输入设备（麦克风）</Label><Select className="w-full"><option>默认设备</option></Select></div>
        <div><Label>回环采集设备（系统声音）</Label><Select className="w-full"><option>默认输出设备</option></Select></div>
        <div><Label>采样率</Label><Select className="w-40"><option>16000 Hz</option><option>44100 Hz</option><option>48000 Hz</option></Select></div>
      </GlassCard>

      <GlassCard className="space-y-4">
        <h3 className="text-sm font-semibold text-[var(--text-primary)]">WebRTC APM</h3>
        <SettingRow label="回声消除 (AEC)" description="消除扬声器回声"><Switch defaultChecked /></SettingRow>
        <SettingRow label="噪音抑制 (NS)" description="降低环境噪音"><Switch defaultChecked /></SettingRow>
        <SettingRow label="自动增益 (AGC)" description="自动调整音量"><Switch defaultChecked /></SettingRow>
        <SettingRow label="高通滤波" description="去除低频杂音"><Switch /></SettingRow>
        <SettingRow label="前置增益 (PreAmp)" description="输入预放大"><Switch /></SettingRow>
      </GlassCard>

      <GlassCard className="space-y-4">
        <h3 className="text-sm font-semibold text-[var(--text-primary)]">VAD 门控</h3>
        <SettingRow label="启用 VAD 门控" description="语音活动检测，静默时不发送数据"><Switch defaultChecked /></SettingRow>
        <div className="grid grid-cols-2 gap-4">
          <div><Label>VAD 灵敏度</Label><Select className="w-full"><option>低</option><option>中</option><option>高</option></Select></div>
          <div><Label>静默持续 (毫秒)</Label><Input type="number" defaultValue="300" className="w-full" /></div>
        </div>
      </GlassCard>
    </div>
  );
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  AI 洞察 (匹配 C# InsightSectionVM)
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

function InsightSection() {
  const config = useAppStore((s) => s.config);
  const ai = config?.ai;
  const updateConfig = useConfigUpdater();

  const setModelRef = (field: string) => (ref: { endpoint_id: string; model_id: string }) => {
    updateConfig((cfg) => { Object.assign(cfg.ai, { [field]: ref }); });
  };

  if (!ai) return null;

  return (
    <div className="max-w-2xl space-y-6">
      <SectionHeader title="AI 洞察" description="模型选择器（端点/模型二级下拉）、系统提示词、参数" />

      <GlassCard className="space-y-4">
        <h3 className="text-sm font-semibold text-[var(--text-primary)]">模型选择</h3>
        <div className="grid grid-cols-2 gap-4">
          <ModelRefSelector label="洞察模型" value={ai.insight_model} onChange={setModelRef("insight_model")} capability="text" />
          <ModelRefSelector label="摘要模型" value={ai.summary_model} onChange={setModelRef("summary_model")} capability="text" />
          <ModelRefSelector label="快问模型" value={ai.quick_model} onChange={setModelRef("quick_model")} capability="text" />
          <ModelRefSelector label="对话模型" value={ai.conversation_model} onChange={setModelRef("conversation_model")} capability="text" />
          <ModelRefSelector label="意图识别模型" value={ai.intent_model} onChange={setModelRef("intent_model")} capability="text" />
        </div>
      </GlassCard>

      <GlassCard className="space-y-4">
        <h3 className="text-sm font-semibold text-[var(--text-primary)]">参数</h3>
        <SettingRow label="启用推理模式 (Reasoning)" description="让模型进行更深入的思考">
          <Switch checked={ai.enable_reasoning} onCheckedChange={(v) => updateConfig((cfg) => { cfg.ai.enable_reasoning = v; })} />
        </SettingRow>
        <div><Label>最大对话轮次</Label>
          <Input type="number" value={ai.max_conversation_turns} className="w-24"
            onChange={(e) => updateConfig((cfg) => { cfg.ai.max_conversation_turns = Number(e.target.value) || 20; })} />
        </div>
      </GlassCard>

      <GlassCard className="space-y-4">
        <h3 className="text-sm font-semibold text-[var(--text-primary)]">系统提示词</h3>
        <Textarea rows={4} value={ai.insight_system_prompt}
          onChange={(e) => updateConfig((cfg) => { cfg.ai.insight_system_prompt = e.target.value; })}
          placeholder="你是一个会议助手，擅长分析对话内容..." />
      </GlassCard>
    </div>
  );
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  图片生成 (匹配 C# ImageGenSectionVM)
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

function ImageSection() {
  const config = useAppStore((s) => s.config);
  const media = config?.media;
  const updateConfig = useConfigUpdater();

  if (!media) return null;

  return (
    <div className="max-w-2xl space-y-6">
      <SectionHeader title="图片生成" description="gpt-image-2 / DALL-E 参数" />

      <GlassCard className="space-y-4">
        <ModelRefSelector label="图片模型" value={media.image_model}
          onChange={(ref) => updateConfig((cfg) => { cfg.media.image_model = ref; })} capability="image" />
        <div className="grid grid-cols-2 gap-4">
          <div><Label>质量</Label>
            <Select className="w-full" value={media.image_quality}
              onChange={(e) => updateConfig((cfg) => { cfg.media.image_quality = e.target.value; })}>
              <option value="auto">auto</option><option value="low">low</option><option value="medium">medium</option><option value="high">high</option>
            </Select>
          </div>
          <div><Label>输出格式</Label>
            <Select className="w-full" value={media.image_format}
              onChange={(e) => updateConfig((cfg) => { cfg.media.image_format = e.target.value; })}>
              <option value="png">PNG</option><option value="jpeg">JPEG</option><option value="webp">WebP</option>
            </Select>
          </div>
        </div>
        <div className="grid grid-cols-2 gap-4">
          <div><Label>生成数量</Label>
            <Input type="number" min={1} max={5} value={media.image_count} className="w-24"
              onChange={(e) => updateConfig((cfg) => { cfg.media.image_count = Number(e.target.value) || 1; })} />
          </div>
          <div><Label>背景</Label>
            <Select className="w-full" value={media.image_background}
              onChange={(e) => updateConfig((cfg) => { cfg.media.image_background = e.target.value; })}>
              <option value="auto">auto</option><option value="opaque">opaque</option><option value="transparent">transparent</option>
            </Select>
          </div>
        </div>
      </GlassCard>

      <GlassCard className="space-y-4">
        <h3 className="text-sm font-semibold text-[var(--text-primary)]">尺寸预设</h3>
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
  const config = useAppStore((s) => s.config);
  const media = config?.media;
  const updateConfig = useConfigUpdater();

  if (!media) return null;

  return (
    <div className="max-w-2xl space-y-6">
      <SectionHeader title="视频生成" description="Sora 等模型参数" />
      <GlassCard className="space-y-4">
        <ModelRefSelector label="视频模型" value={media.video_model}
          onChange={(ref) => updateConfig((cfg) => { cfg.media.video_model = ref; })} capability="video" />
        <div className="grid grid-cols-2 gap-4">
          <div><Label>宽高比</Label>
            <Select className="w-full" value={media.video_aspect_ratio}
              onChange={(e) => updateConfig((cfg) => { cfg.media.video_aspect_ratio = e.target.value; })}>
              <option>16:9</option><option>9:16</option><option>1:1</option><option>4:3</option><option>3:4</option>
            </Select>
          </div>
          <div><Label>分辨率</Label>
            <Select className="w-full" value={media.video_resolution}
              onChange={(e) => updateConfig((cfg) => { cfg.media.video_resolution = e.target.value; })}>
              <option>720p</option><option>1080p</option>
            </Select>
          </div>
        </div>
        <div className="grid grid-cols-2 gap-4">
          <div><Label>时长 (秒)</Label>
            <Input type="number" value={media.video_seconds} className="w-24"
              onChange={(e) => updateConfig((cfg) => { cfg.media.video_seconds = Number(e.target.value) || 5; })} />
          </div>
          <div><Label>变体数</Label>
            <Input type="number" min={1} max={4} value={media.video_variants} className="w-24"
              onChange={(e) => updateConfig((cfg) => { cfg.media.video_variants = Number(e.target.value) || 1; })} />
          </div>
        </div>
        <div><Label>轮询间隔 (毫秒)</Label>
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
  const config = useAppStore((s) => s.config);
  const ws = config?.web_search;
  const updateConfig = useConfigUpdater();

  if (!ws) return null;

  return (
    <div className="max-w-2xl space-y-6">
      <SectionHeader title="网页搜索" description="AI 对话中的联网搜索能力" />
      <GlassCard className="space-y-4">
        <div><Label>搜索引擎</Label>
          <Select className="w-full" value={ws.provider_id}
            onChange={(e) => updateConfig((cfg) => { cfg.web_search.provider_id = e.target.value; })}>
            <option value="bing">Bing</option><option value="google">Google</option>
            <option value="duckduckgo">DuckDuckGo</option><option value="brave">Brave</option>
            <option value="mcp">自定义 MCP</option>
          </Select>
        </div>
        <div><Label>触发模式</Label>
          <Select className="w-full" value={ws.trigger_mode}
            onChange={(e) => updateConfig((cfg) => { cfg.web_search.trigger_mode = e.target.value; })}>
            <option value="auto">Auto — AI 判断是否需要搜索</option>
            <option value="always">Always — 每次对话都搜索</option>
            <option value="manual">Manual — 仅手动触发</option>
          </Select>
        </div>
        <div><Label>最大结果数</Label>
          <Input type="number" value={ws.max_results} className="w-24"
            onChange={(e) => updateConfig((cfg) => { cfg.web_search.max_results = Number(e.target.value) || 5; })} />
        </div>
        <SettingRow label="AI 意图分析" description="让 AI 判断用户问题是否需要联网搜索">
          <Switch checked={ws.enable_intent_analysis} onCheckedChange={(v) => updateConfig((cfg) => { cfg.web_search.enable_intent_analysis = v; })} />
        </SettingRow>
        <SettingRow label="结果压缩" description="对搜索结果进行摘要压缩后注入上下文">
          <Switch checked={ws.enable_result_compression} onCheckedChange={(v) => updateConfig((cfg) => { cfg.web_search.enable_result_compression = v; })} />
        </SettingRow>
      </GlassCard>

      <GlassCard className="space-y-4">
        <h3 className="text-sm font-semibold text-[var(--text-primary)]">MCP 配置（自定义搜索）</h3>
        <div><Label>MCP 端点 URL</Label>
          <Input value={ws.mcp_endpoint} placeholder="https://mcp-server.example.com"
            onChange={(e) => updateConfig((cfg) => { cfg.web_search.mcp_endpoint = e.target.value; })} />
        </div>
        <div><Label>工具名称</Label>
          <Input value={ws.mcp_tool_name} placeholder="web_search"
            onChange={(e) => updateConfig((cfg) => { cfg.web_search.mcp_tool_name = e.target.value; })} />
        </div>
        <div><Label>API Key</Label>
          <Input type="password" value={ws.mcp_api_key} placeholder="可选"
            onChange={(e) => updateConfig((cfg) => { cfg.web_search.mcp_api_key = e.target.value; })} />
        </div>
        <SettingRow label="调试模式" description="输出搜索请求和响应日志">
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
  return (
    <div className="max-w-2xl space-y-6">
      <SectionHeader title="云服务" description="后端网关连接与 AAD 登录" />

      <GlassCard className="space-y-4">
        <div><Label>服务模式</Label>
          <Select className="w-full">
            <option value="self_hosted">本地直连 (SelfHosted)</option>
            <option value="cloud">云端网关 (Cloud)</option>
          </Select>
        </div>
        <div><Label>后端 URL</Label><Input placeholder="https://gateway.truefluent.pro" /></div>
      </GlassCard>

      <GlassCard className="space-y-4">
        <h3 className="text-sm font-semibold text-[var(--text-primary)]">Azure AD 登录</h3>
        <div><Label>租户 ID (Tenant)</Label><Input placeholder="00000000-0000-0000-0000-000000000000" /></div>
        <div><Label>客户端 ID (Client)</Label><Input placeholder="00000000-0000-0000-0000-000000000000" /></div>
        <div><Label>Scope</Label><Input placeholder="api://truefluent/.default" /></div>
        <div className="flex gap-2">
          <Button size="sm"><Shield size={14} /> 登录</Button>
          <Button variant="secondary" size="sm"><Zap size={14} /> 健康检查</Button>
        </div>
      </GlassCard>
    </div>
  );
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  导入导出 (匹配 C# TransferSectionVM)
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

function TransferSection() {
  const [status, setStatus] = useState("");

  const handleExport = async (fullConfig: boolean) => {
    try {
      const json = await api.exportConfig();
      let data = json;
      if (!fullConfig) {
        // 仅导出端点部分
        const parsed = JSON.parse(json);
        data = JSON.stringify({ endpoints: parsed.endpoints, ai: parsed.ai, media: parsed.media }, null, 2);
      }
      const path = await dialogSave({
        title: fullConfig ? "导出完整配置" : "导出基础 AI 配置",
        defaultPath: fullConfig ? "truefluent-config-full.json" : "truefluent-config.json",
        filters: [{ name: "JSON", extensions: ["json"] }],
      });
      if (!path) return;
      await api.writeTextFile(path, data);
      setStatus(`✓ 已导出到 ${path}`);
    } catch (e) {
      setStatus(`✗ 导出失败: ${e}`);
    }
  };

  const handleImport = async (fullConfig: boolean) => {
    try {
      const path = await dialogOpen({
        title: fullConfig ? "导入完整配置" : "导入基础 AI 配置",
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
        // 合并端点部分到现有配置
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
      setStatus("✓ 导入成功，配置已更新");
    } catch (e) {
      setStatus(`✗ 导入失败: ${e}`);
    }
  };

  return (
    <div className="max-w-2xl space-y-6">
      <SectionHeader title="导入导出" description="配置备份与迁移" />

      <GlassCard className="space-y-4">
        <h3 className="text-sm font-semibold text-[var(--text-primary)]">基础 AI 配置</h3>
        <p className="text-xs text-[var(--text-muted)]">仅包含端点和模型引用</p>
        <div className="flex gap-2">
          <Button variant="secondary" size="sm" onClick={() => handleExport(false)}><Download size={14} /> 导出</Button>
          <Button variant="secondary" size="sm" onClick={() => handleImport(false)}><Upload size={14} /> 导入</Button>
        </div>
      </GlassCard>

      <GlassCard className="space-y-4">
        <h3 className="text-sm font-semibold text-[var(--text-primary)]">完整配置</h3>
        <p className="text-xs text-[var(--text-muted)]">包含所有设置、端点、主题等</p>
        <div className="flex gap-2">
          <Button variant="secondary" size="sm" onClick={() => handleExport(true)}><Download size={14} /> 导出全部</Button>
          <Button variant="secondary" size="sm" onClick={() => handleImport(true)}><Upload size={14} /> 导入全部</Button>
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
  const themeMode = useThemeStore((s) => s.mode);
  const setMode = useThemeStore((s) => s.setMode);
  const fontSize = useThemeStore((s) => s.fontSize);
  const setFontSize = useThemeStore((s) => s.setFontSize);
  const transitionDuration = useThemeStore((s) => s.transitionDuration);
  const setTransitionDuration = useThemeStore((s) => s.setTransitionDuration);

  return (
    <div className="max-w-2xl space-y-6">
      <SectionHeader title="界面设置" description="主题、字号、语言" />

      <GlassCard className="space-y-4">
        <h3 className="text-sm font-semibold text-[var(--text-primary)]">主题</h3>
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
              <span className="text-sm">{m === "system" ? "跟随系统" : m === "light" ? "浅色" : "深色"}</span>
            </button>
          ))}
        </div>
      </GlassCard>

      <GlassCard className="space-y-4">
        <div>
          <Label>字号 ({fontSize}px)</Label>
          <Input className="w-24" type="number" min={12} max={20} value={fontSize}
            onChange={(e) => setFontSize(Number(e.target.value) || 14)} />
        </div>
        <div>
          <Label>页面切换动画 ({transitionDuration}ms)</Label>
          <div className="flex items-center gap-3">
            <input type="range" min={0} max={500} step={10} value={transitionDuration}
              onChange={(e) => setTransitionDuration(Number(e.target.value))}
              className="flex-1 accent-brand-500" />
            <Input className="w-20" type="number" min={0} max={1000} value={transitionDuration}
              onChange={(e) => setTransitionDuration(Number(e.target.value) || 0)} />
          </div>
          <p className="text-[10px] text-[var(--text-muted)] mt-1">设为 0 可关闭动画</p>
        </div>
        <div><Label>界面语言</Label>
          <Select className="w-40"><option value="zh-CN">简体中文</option><option value="en">English</option></Select>
        </div>
      </GlassCard>
    </div>
  );
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  关于 (匹配 C# AboutSectionVM)
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

function AboutSection() {
  return (
    <div className="max-w-2xl space-y-6">
      <SectionHeader title="关于" />

      <GlassCard className="space-y-3">
        <div className="flex items-center gap-3 mb-2">
          <span className="text-xl font-bold text-gradient">译见 Pro</span>
          <Badge variant="blue">v0.1.0</Badge>
        </div>
        <p className="text-sm text-[var(--text-secondary)]">
          Tauri 2 + React 19 + Rust — 全平台 AI 翻译 & 创作工具
        </p>
      </GlassCard>

      <GlassCard className="space-y-4">
        <h3 className="text-sm font-semibold text-[var(--text-primary)]">技术架构</h3>
        <div className="text-sm text-[var(--text-secondary)] space-y-1">
          <p>前端: React 19 + TypeScript + Tailwind CSS + Radix UI</p>
          <p>后端: Rust + Tauri 2 + reqwest (SSE streaming)</p>
          <p>存储: SQLite (rusqlite)</p>
          <p>Provider: OpenAI Chat / Image (可扩展)</p>
        </div>
      </GlassCard>

      <GlassCard className="space-y-4">
        <SettingRow label="自动检查更新" description="启动时检查新版本"><Switch defaultChecked /></SettingRow>
      </GlassCard>
    </div>
  );
}
