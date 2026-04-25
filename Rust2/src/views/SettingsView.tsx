import { useState } from "react";
import { useTranslation } from "react-i18next";
import {
  Plus, Trash2, Edit2, Check, X, Globe, Brain, Image,
  Volume2, ChevronDown, TestTube, Sparkles, Monitor, Cloud,
} from "lucide-react";
import { cn } from "../lib/utils";
import {
  Button, GlassCard, Input, Select, Label, Badge, Switch,
  Separator, FadeIn, EmptyState, SectionHeader,
} from "../components/ui";
import { useAppStore } from "../stores/app-store";
import { api, type AiEndpoint } from "../lib/tauri-api";

type SettingsTab = "endpoints" | "audio" | "translation" | "ai" | "image" | "ui" | "cloud";

const ENDPOINT_TYPES = [
  { value: "azure_open_ai", label: "Azure OpenAI" },
  { value: "azure_speech", label: "Azure Speech" },
  { value: "azure_translator", label: "Azure Translator" },
  { value: "open_ai", label: "OpenAI" },
  { value: "deep_l", label: "DeepL" },
  { value: "google", label: "Google" },
  { value: "tencent_cloud", label: "腾讯云" },
  { value: "alibaba_cloud", label: "阿里云" },
  { value: "custom", label: "自定义" },
];

export function SettingsView() {
  const { t } = useTranslation();
  const [activeTab, setActiveTab] = useState<SettingsTab>("endpoints");

  const tabs: { id: SettingsTab; label: string; icon: React.ReactNode }[] = [
    { id: "endpoints", label: t("settings.endpoints"), icon: <Globe size={15} /> },
    { id: "translation", label: t("settings.translation"), icon: <Globe size={15} /> },
    { id: "audio", label: t("settings.audio"), icon: <Volume2 size={15} /> },
    { id: "ai", label: t("settings.ai"), icon: <Brain size={15} /> },
    { id: "image", label: t("settings.image"), icon: <Image size={15} /> },
    { id: "ui", label: t("settings.ui"), icon: <Monitor size={15} /> },
    { id: "cloud", label: t("settings.cloud"), icon: <Cloud size={15} /> },
  ];

  return (
    <div className="flex h-full">
      {/* 左侧标签栏 */}
      <div className="w-44 border-r border-white/[0.06] bg-white/[0.01] py-4 px-2 space-y-0.5 shrink-0">
        {tabs.map((tab) => (
          <button
            key={tab.id}
            onClick={() => setActiveTab(tab.id)}
            className={cn(
              "flex items-center gap-2 w-full rounded-xl px-3 py-2 text-sm transition-all duration-200",
              activeTab === tab.id
                ? "bg-brand-600/15 text-brand-300 shadow-sm shadow-brand-600/5"
                : "text-slate-500 hover:bg-white/[0.04] hover:text-slate-300",
            )}
          >
            {tab.icon}
            <span>{tab.label}</span>
          </button>
        ))}
      </div>

      {/* 右侧内容 */}
      <div className="flex-1 overflow-y-auto p-6">
        <FadeIn key={activeTab}>
          {activeTab === "endpoints" && <EndpointsSection />}
          {activeTab === "translation" && <TranslationSection />}
          {activeTab === "audio" && <AudioSection />}
          {activeTab === "ai" && <AiSection />}
          {activeTab === "image" && <ImageSection />}
          {activeTab === "ui" && <UiSection />}
          {activeTab === "cloud" && <CloudSection />}
        </FadeIn>
      </div>
    </div>
  );
}

// ── 端点管理 ──

function EndpointsSection() {
  const { t } = useTranslation();
  const config = useAppStore((s) => s.config);
  const endpoints = config?.endpoints ?? [];
  const [showCreate, setShowCreate] = useState(false);
  const [testing, setTesting] = useState<string | null>(null);

  const handleTest = async (id: string) => {
    setTesting(id);
    try {
      await api.testEndpoint(id);
    } catch {
      // ignore
    } finally {
      setTesting(null);
    }
  };

  const handleDelete = async (id: string) => {
    await api.removeEndpoint(id);
    const cfg = await api.getConfig();
    useAppStore.getState().setConfig(cfg);
  };

  return (
    <div className="max-w-3xl">
      <SectionHeader
        title={t("settings.endpoints")}
        description={t("settings.endpointsDesc")}
        action={
          <Button size="sm" onClick={() => setShowCreate(true)}>
            <Plus size={14} /> {t("settings.addEndpoint")}
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
          title={t("settings.noEndpoints")}
          description={t("settings.noEndpointsHint")}
          action={
            <Button size="sm" onClick={() => setShowCreate(true)}>
              <Plus size={14} /> {t("settings.addEndpoint")}
            </Button>
          }
        />
      ) : (
        <div className="space-y-3 mt-4">
          {endpoints.map((ep, i) => (
            <FadeIn key={ep.id} delay={i * 0.05}>
              <GlassCard className="flex items-center gap-4 py-3">
                <div
                  className={cn(
                    "w-2 h-2 rounded-full shrink-0",
                    ep.enabled ? "bg-emerald-400 shadow-sm shadow-emerald-400/50" : "bg-slate-600",
                  )}
                />
                <div className="flex-1 min-w-0">
                  <div className="flex items-center gap-2">
                    <span className="font-medium text-slate-200 truncate">{ep.name}</span>
                    <Badge variant="blue">
                      {ENDPOINT_TYPES.find((t) => t.value === ep.endpoint_type)?.label ?? ep.endpoint_type}
                    </Badge>
                  </div>
                  <p className="text-xs text-slate-500 truncate mt-0.5">{ep.url}</p>
                </div>
                <Button
                  variant="ghost" size="icon"
                  onClick={() => handleTest(ep.id)}
                  disabled={testing === ep.id}
                  className="h-7 w-7"
                >
                  {testing === ep.id ? (
                    <Sparkles size={14} className="animate-spin" />
                  ) : (
                    <TestTube size={14} />
                  )}
                </Button>
                <Button variant="ghost" size="icon" className="h-7 w-7">
                  <Edit2 size={14} />
                </Button>
                <Button
                  variant="ghost" size="icon"
                  className="h-7 w-7 text-red-400 hover:text-red-300"
                  onClick={() => handleDelete(ep.id)}
                >
                  <Trash2 size={14} />
                </Button>
              </GlassCard>
            </FadeIn>
          ))}
        </div>
      )}
    </div>
  );
}

function EndpointForm({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation();
  const [form, setForm] = useState({
    name: "",
    endpoint_type: "azure_open_ai",
    url: "",
    api_key: "",
    region: "",
    deployment: "",
  });

  const update = (field: string, value: string) =>
    setForm((f) => ({ ...f, [field]: value }));

  const handleSave = async () => {
    const ep: AiEndpoint = {
      id: crypto.randomUUID(),
      ...form,
      enabled: true,
    };
    await api.addEndpoint(ep);
    const cfg = await api.getConfig();
    useAppStore.getState().setConfig(cfg);
    onClose();
  };

  return (
    <GlassCard className="mb-4 border-brand-500/20" glow>
      <h3 className="text-sm font-semibold text-slate-200 mb-4">{t("settings.newEndpoint")}</h3>
      <div className="grid grid-cols-2 gap-4">
        <div>
          <Label>{t("settings.name")}</Label>
          <Input value={form.name} onChange={(e) => update("name", e.target.value)} placeholder="My Azure OpenAI" />
        </div>
        <div>
          <Label>{t("settings.type")}</Label>
          <div className="relative">
            <Select className="w-full" value={form.endpoint_type} onChange={(e) => update("endpoint_type", e.target.value)}>
              {ENDPOINT_TYPES.map((t) => <option key={t.value} value={t.value}>{t.label}</option>)}
            </Select>
            <ChevronDown size={14} className="absolute right-2.5 top-1/2 -translate-y-1/2 text-slate-500 pointer-events-none" />
          </div>
        </div>
        <div className="col-span-2">
          <Label>{t("settings.endpointUrl")}</Label>
          <Input value={form.url} onChange={(e) => update("url", e.target.value)} placeholder="https://..." />
        </div>
        <div>
          <Label>{t("settings.apiKey")}</Label>
          <Input type="password" value={form.api_key} onChange={(e) => update("api_key", e.target.value)} placeholder="sk-..." />
        </div>
        <div>
          <Label>{t("settings.region")}</Label>
          <Input value={form.region} onChange={(e) => update("region", e.target.value)} placeholder="eastus" />
        </div>
      </div>
      <div className="flex justify-end gap-2 mt-4">
        <Button variant="secondary" size="sm" onClick={onClose}><X size={14} /> {t("common.cancel")}</Button>
        <Button size="sm" onClick={handleSave}><Check size={14} /> {t("common.save")}</Button>
      </div>
    </GlassCard>
  );
}

// ── 设置项组件 ──

function SettingRow({ label, description, children }: { label: string; description?: string; children: React.ReactNode }) {
  return (
    <div className="flex items-center justify-between gap-4">
      <div className="min-w-0">
        <p className="text-sm text-slate-200">{label}</p>
        {description && <p className="text-xs text-slate-500">{description}</p>}
      </div>
      <div className="shrink-0">{children}</div>
    </div>
  );
}

// ── 翻译设置 ──

function TranslationSection() {
  const { t } = useTranslation();
  return (
    <div className="max-w-2xl space-y-6">
      <SectionHeader title={t("settings.translation")} />
      <GlassCard className="space-y-5">
        <div>
          <Label>{t("settings.defaultSourceLang")}</Label>
          <Select className="w-60"><option>中文（简体）</option><option>English</option></Select>
        </div>
        <div>
          <Label>{t("settings.defaultTargetLang")}</Label>
          <Select className="w-60"><option>English</option><option>日本語</option></Select>
        </div>
        <Separator />
        <SettingRow label={t("settings.enablePartial")} description={t("settings.enablePartialDesc")}>
          <Switch defaultChecked />
        </SettingRow>
        <SettingRow label={t("settings.profanityFilter")} description={t("settings.profanityFilterDesc")}>
          <Switch />
        </SettingRow>
      </GlassCard>
    </div>
  );
}

// ── 音频设置 ──

function AudioSection() {
  const { t } = useTranslation();
  return (
    <div className="max-w-2xl space-y-6">
      <SectionHeader title={t("settings.audio")} />
      <GlassCard className="space-y-4">
        <div>
          <Label>{t("settings.inputDevice")}</Label>
          <Select><option>{t("common.defaultDevice")}</option></Select>
        </div>
        <div>
          <Label>{t("settings.loopbackDevice")}</Label>
          <Select><option>{t("common.defaultOutputDevice")}</option></Select>
        </div>
        <div>
          <Label>{t("settings.sampleRate")}</Label>
          <Select className="w-40"><option>16000 Hz</option><option>44100 Hz</option><option>48000 Hz</option></Select>
        </div>
      </GlassCard>

      <GlassCard className="space-y-4">
        <h3 className="text-sm font-semibold text-slate-200">{t("settings.audioPreprocessing")}</h3>
        <SettingRow label={t("settings.aec")} description={t("settings.aecDesc")}>
          <Switch defaultChecked />
        </SettingRow>
        <SettingRow label={t("settings.ns")} description={t("settings.nsDesc")}>
          <Switch defaultChecked />
        </SettingRow>
        <SettingRow label={t("settings.agc")} description={t("settings.agcDesc")}>
          <Switch defaultChecked />
        </SettingRow>
      </GlassCard>
    </div>
  );
}

// ── AI 模型 ──

function AiSection() {
  const { t } = useTranslation();
  return (
    <div className="max-w-2xl space-y-6">
      <SectionHeader title={t("settings.ai")} />
      <GlassCard className="space-y-4">
        <div>
          <Label>{t("settings.defaultModel")}</Label>
          <Input placeholder="gpt-4.1" />
        </div>
        <div>
          <Label>{t("settings.temperature")}</Label>
          <Input className="w-32" type="number" step="0.1" min="0" max="2" defaultValue="0.7" />
        </div>
        <div>
          <Label>{t("settings.maxTokens")}</Label>
          <Input className="w-32" type="number" step="100" min="100" max="128000" defaultValue="4096" />
        </div>
      </GlassCard>
    </div>
  );
}

// ── 图片生成 ──

function ImageSection() {
  const { t } = useTranslation();
  return (
    <div className="max-w-2xl space-y-6">
      <SectionHeader title={t("settings.image")} />
      <GlassCard className="space-y-4">
        <div>
          <Label>{t("settings.imageModel")}</Label>
          <Input placeholder="gpt-image-2" />
        </div>
        <div className="grid grid-cols-2 gap-4">
          <div>
            <Label>{t("settings.imageWidth")}</Label>
            <Input type="number" defaultValue="1024" />
          </div>
          <div>
            <Label>{t("settings.imageHeight")}</Label>
            <Input type="number" defaultValue="1024" />
          </div>
        </div>
        <div>
          <Label>{t("settings.imageQuality")}</Label>
          <Select className="w-40"><option>standard</option><option>high</option><option>low</option></Select>
        </div>
      </GlassCard>
    </div>
  );
}

// ── 界面设置 ──

function UiSection() {
  const { t } = useTranslation();
  return (
    <div className="max-w-2xl space-y-6">
      <SectionHeader title={t("settings.ui")} />
      <GlassCard className="space-y-4">
        <div>
          <Label>{t("settings.theme")}</Label>
          <Select className="w-40"><option value="dark">{t("settings.themeDark")}</option><option value="light">{t("settings.themeLight")}</option></Select>
        </div>
        <div>
          <Label>{t("settings.fontSize")}</Label>
          <Input className="w-32" type="number" min="12" max="20" defaultValue="14" />
        </div>
        <div>
          <Label>{t("settings.uiLanguage")}</Label>
          <Select className="w-40"><option value="zh-CN">简体中文</option><option value="en">English</option></Select>
        </div>
      </GlassCard>
    </div>
  );
}

// ── 云服务 ──

function CloudSection() {
  const { t } = useTranslation();
  return (
    <div className="max-w-2xl space-y-6">
      <SectionHeader title={t("settings.cloud")} />
      <GlassCard>
        <SettingRow label={t("settings.cloudSync")} description={t("settings.cloudSyncDesc")}>
          <Switch />
        </SettingRow>
      </GlassCard>
      <GlassCard>
        <p className="text-sm text-slate-400">{t("settings.cloudNote")}</p>
      </GlassCard>
    </div>
  );
}
