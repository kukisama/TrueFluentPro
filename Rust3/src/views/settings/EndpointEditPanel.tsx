import { useState, useCallback, useEffect, useRef } from "react";
import { useTranslation } from "react-i18next";
import { Plus, X, ChevronLeft, Search, Loader2 } from "lucide-react";
import { cn } from "../../lib/utils";
import {
  Button, Input, Select, Label, Separator, Switch, SettingRow,
} from "../../components/ui";
import { useAppStore } from "../../stores/app-store";
import { api } from "../../lib/api";
import type {
  AiEndpoint, AiModelEntry, ModelCapability, DiscoveredModel,
} from "../../lib/types";
import {
  VENDOR_BADGE, ALL_CAPABILITIES, CAPABILITY_ICONS, CAPABILITY_COLORS,
  CAPABILITY_LABEL_KEYS, CAPABILITY_TIP_KEYS,
} from "../SettingsView";
import AadLoginButton from "./AadLoginButton";

export default function EndpointEditPanel({
  endpoint,
  onClose,
}: {
  endpoint: AiEndpoint;
  onClose: () => void;
}) {
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

  const saveTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const formRef = useRef(form);
  const modelsRef = useRef(models);
  formRef.current = form;
  modelsRef.current = models;

  const persistNow = useCallback(async () => {
    setSaving(true);
    try {
      const updated: AiEndpoint = {
        ...formRef.current,
        models: formRef.current.endpoint_type === "azure_speech" ? [] : modelsRef.current,
      };
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

  useEffect(() => () => {
    if (saveTimerRef.current) clearTimeout(saveTimerRef.current);
  }, []);

  const update = (f: string, v: string | boolean) => {
    setForm((s) => ({ ...s, [f]: v }));
    scheduleSave();
  };

  const addModel = () => {
    if (!newModelId.trim()) return;
    setModels((prev) => [
      ...prev,
      { model_id: newModelId.trim(), display_name: newModelId.trim(), capabilities: [...newModelCaps] },
    ]);
    setNewModelId("");
    scheduleSave();
  };

  const removeModel = (modelId: string) => {
    setModels((prev) => prev.filter((m) => m.model_id !== modelId));
    if (expandedModelIdx !== null) setExpandedModelIdx(null);
    scheduleSave();
  };

  const toggleModelCapability = (idx: number, cap: ModelCapability) => {
    setModels((prev) =>
      prev.map((m, i) => {
        if (i !== idx) return m;
        const has = m.capabilities.includes(cap);
        if (has && m.capabilities.length <= 1) return m;
        const caps = has ? m.capabilities.filter((c) => c !== cap) : [...m.capabilities, cap];
        return { ...m, capabilities: caps };
      }),
    );
    scheduleSave();
  };

  const handleDiscover = async () => {
    setDiscovering(true);
    try {
      const found = await api.discoverModels(endpoint.id);
      setDiscoveredModels(found);
    } catch {
      setDiscoveredModels([]);
    } finally {
      setDiscovering(false);
    }
  };

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
      <div className="flex items-center gap-2">
        <button onClick={handleBack}
          className="flex items-center gap-1 text-xs text-[var(--text-muted)] hover:text-[var(--text-primary)] transition-colors">
          <ChevronLeft size={14} />
          <span>{t("endpointForm.back")}</span>
        </button>
        <span className="flex-1" />
        {saving && (
          <span className="text-[10px] text-[var(--text-muted)] animate-pulse">{t("endpointForm.saving")}</span>
        )}
        <SettingRow label={t("endpointForm.enabled")} description="">
          <Switch checked={form.enabled} onCheckedChange={(v) => update("enabled", v)} />
        </SettingRow>
      </div>

      <div className="grid grid-cols-2 gap-3">
        <div>
          <Label>{t("endpointForm.name")}</Label>
          <Input value={form.name}
            onBlur={() => scheduleSave()}
            onChange={(e) => setForm((s) => ({ ...s, name: e.target.value }))} />
        </div>
        <div>
          <Label>{t("endpointForm.endpointType")}</Label>
          <Select className="w-full" value={form.endpoint_type}
            onChange={(e) => update("endpoint_type", e.target.value)}>
            {Object.entries(VENDOR_BADGE).map(([k, v]) => (
              <option key={k} value={k}>{t(v.labelKey)}</option>
            ))}
          </Select>
        </div>
      </div>

      {isSpeech ? (
        <div className="space-y-3">
          <div className="grid grid-cols-2 gap-3">
            <div>
              <Label>{t("endpointForm.region")}</Label>
              <Input value={form.speech_region}
                onChange={(e) => setForm((s) => ({ ...s, speech_region: e.target.value }))}
                onBlur={() => scheduleSave()} placeholder="southeastasia" />
            </div>
          </div>
          <div>
            <Label>{t("endpointForm.speechEndpoint")}</Label>
            <Input value={form.speech_endpoint}
              onChange={(e) => setForm((s) => ({ ...s, speech_endpoint: e.target.value }))}
              onBlur={() => scheduleSave()} placeholder="https://region.api.cognitive.microsoft.com" />
          </div>
          <div>
            <Label>{t("endpointForm.subscriptionKey")}</Label>
            <Input type="password" value={form.speech_subscription_key}
              onChange={(e) => setForm((s) => ({ ...s, speech_subscription_key: e.target.value }))}
              onBlur={() => scheduleSave()} />
          </div>
        </div>
      ) : (
        <>
          <div className="grid grid-cols-2 gap-3">
            <div>
              <Label>{t("endpointForm.endpointUrl")}</Label>
              <Input value={form.url}
                onChange={(e) => setForm((s) => ({ ...s, url: e.target.value }))}
                onBlur={() => scheduleSave()} />
            </div>
            <div>
              <Label>{t("endpointForm.apiKey")}</Label>
              <Input type="password" value={form.api_key}
                onChange={(e) => setForm((s) => ({ ...s, api_key: e.target.value }))}
                onBlur={() => scheduleSave()} />
            </div>
            <div>
              <Label>{t("endpointForm.apiVersion")}</Label>
              <Input value={form.api_version || ""}
                onChange={(e) => setForm((s) => ({ ...s, api_version: e.target.value }))}
                onBlur={() => scheduleSave()} />
            </div>
            <div>
              <Label>{t("endpointForm.regionOptional")}</Label>
              <Input value={form.region || ""}
                onChange={(e) => setForm((s) => ({ ...s, region: e.target.value }))}
                onBlur={() => scheduleSave()} />
            </div>
          </div>

          {(form.endpoint_type === "azure_open_ai" || form.endpoint_type === "api_management_gateway") && (
            <div className="mt-2">
              <AadLoginButton endpointId={endpoint.id}
                tenantId={form.azure_tenant_id || ""} clientId={form.azure_client_id || ""} />
            </div>
          )}

          <Separator />

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
                              onClick={() => toggleModelCapability(idx, c)}>
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

          <div className="flex items-end gap-2">
            <Input value={newModelId} onChange={(e) => setNewModelId(e.target.value)}
              placeholder={t("endpointForm.modelIdPlaceholder")} className="flex-1"
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
                    onClick={() => setNewModelCaps([c])}>
                    <Icon size={14} />
                  </button>
                );
              })}
            </div>
            <Button size="sm" variant="secondary" onClick={addModel} disabled={!newModelId.trim()}>
              <Plus size={12} />
            </Button>
          </div>

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
