import { useState, useCallback } from "react";
import { useTranslation } from "react-i18next";
import {
  Globe, Mic, Brain, Image, Video, Volume2, FileText,
  Search, Cloud, ArrowUpDown, Monitor, Info,
} from "lucide-react";
import { cn } from "../lib/utils";
import { FadeIn, Select, Label } from "../components/ui";
import { useAppStore } from "../stores/app-store";
import { api } from "../lib/api";
import type { ModelCapability, ModelReference } from "../lib/types";
import { RecognitionSection, StorageSection, AudioSection, InsightSection, ImageSection, VideoSection } from "./settings/GeneralSections";
import { SearchSection, CloudSection, TransferSection, UiSection, AboutSection } from "./settings/SystemSections";
import EndpointsSection from "./settings/EndpointsSection";

export type SettingsTab =
  | "endpoints" | "recognition" | "storage" | "audio"
  | "insight" | "image" | "video" | "search"
  | "cloud" | "transfer" | "ui" | "about";

export const VENDOR_BADGE: Record<string, { labelKey: string; badge: string; icon: string; color: string }> = {
  azure_open_ai:          { labelKey: "vendorLabels.azure_open_ai",          badge: "AZ", icon: "/icons/azure-openai.svg",       color: "blue" },
  api_management_gateway: { labelKey: "vendorLabels.api_management_gateway", badge: "AP", icon: "/icons/apim-gateway.svg",       color: "purple" },
  open_ai_compatible:     { labelKey: "vendorLabels.open_ai_compatible",     badge: "OA", icon: "/icons/openai-compatible.svg",  color: "green" },
  azure_speech:           { labelKey: "vendorLabels.azure_speech",           badge: "SP", icon: "/icons/azure-speech.svg",       color: "yellow" },
  azure_translator:       { labelKey: "vendorLabels.azure_open_ai",          badge: "TR", icon: "/icons/azure-openai.svg",       color: "blue" },
  deep_l:                 { labelKey: "vendorLabels.deepl",                  badge: "DL", icon: "/icons/openai-compatible.svg",  color: "blue" },
  tencent_cloud:          { labelKey: "vendorLabels.tencent_cloud",          badge: "TC", icon: "/icons/openai-compatible.svg",  color: "blue" },
  alibaba_cloud:          { labelKey: "vendorLabels.alibaba_cloud",          badge: "AL", icon: "/icons/openai-compatible.svg",  color: "orange" },
  custom:                 { labelKey: "vendorLabels.custom",                 badge: "CU", icon: "/icons/openai-compatible.svg",  color: "gray" },
};

export const CAPABILITY_LABEL_KEYS: Record<string, string> = {
  text: "capLabels.text", image: "capLabels.image", video: "capLabels.video",
  speech_to_text: "capLabels.speech_to_text", text_to_speech: "capLabels.text_to_speech",
};
export const CAPABILITY_ICONS: Record<string, typeof Globe> = {
  text: FileText, image: Image, video: Video, speech_to_text: Mic, text_to_speech: Volume2,
};
export const CAPABILITY_COLORS: Record<string, string> = {
  text: "var(--cap-text)", image: "var(--cap-image)", video: "var(--cap-video)",
  speech_to_text: "var(--cap-stt)", text_to_speech: "var(--cap-tts)",
};
export const CAPABILITY_TIP_KEYS: Record<string, string> = {
  text: "capTips.text", image: "capTips.image", video: "capTips.video",
  speech_to_text: "capTips.speech_to_text", text_to_speech: "capTips.text_to_speech",
};
export const ALL_CAPABILITIES: ModelCapability[] = ["text", "image", "video", "speech_to_text", "text_to_speech"];

export function ModelRefSelector({
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
  const filteredEndpoints = endpoints
    .filter((ep) => ep.enabled && ep.endpoint_type !== "azure_speech")
    .filter((ep) => !capability || ep.models.some((m) => m.capabilities.includes(capability)));

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

  const currentValue = value.endpoint_id && value.model_id ? `${value.endpoint_id}::${value.model_id}` : "";
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

export function useConfigUpdater() {
  return useCallback(async (updater: (cfg: NonNullable<ReturnType<typeof useAppStore.getState>["config"]>) => void) => {
    const cfg = useAppStore.getState().config;
    if (!cfg) return;
    const copy = JSON.parse(JSON.stringify(cfg));
    updater(copy);
    await api.updateConfig(copy);
    useAppStore.getState().setConfig(copy);
  }, []);
}

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
