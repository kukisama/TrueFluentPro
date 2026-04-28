import { useState, useEffect, useCallback } from "react";
import { useTranslation } from "react-i18next";
import { Plus, X, Check, ChevronLeft, Search, Loader2 } from "lucide-react";
import { cn } from "../../lib/utils";
import {
  Button, GlassCard, Input, Select, Label, Separator,
} from "../../components/ui";
import { useAppStore } from "../../stores/app-store";
import { api } from "../../lib/api";
import type {
  AiEndpoint, AiModelEntry, ModelCapability, VendorProfile, DiscoveredModel,
} from "../../lib/types";
import {
  VENDOR_BADGE, ALL_CAPABILITIES, CAPABILITY_ICONS, CAPABILITY_COLORS,
  CAPABILITY_LABEL_KEYS, CAPABILITY_TIP_KEYS,
} from "../SettingsView";
import AadLoginButton from "./AadLoginButton";

export default function EndpointForm({ onClose }: { onClose: () => void }) {
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
      if (f === "endpoint_type") {
        const p = profiles.find((pr) => pr.endpoint_type === v);
        if (p) {
          next.auth_header_mode = p.default_auth_header;
          next.api_version = p.default_api_version;
          next.auth_mode = "api_key";
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
    setModels((prev) =>
      prev.map((m, i) => {
        if (i !== idx) return m;
        const has = m.capabilities.includes(cap);
        if (has && m.capabilities.length <= 1) return m;
        const caps = has ? m.capabilities.filter((c) => c !== cap) : [...m.capabilities, cap];
        return { ...m, capabilities: caps };
      }),
    );
  };

  const handleDiscover = async () => {
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
    } catch {
      setDiscoveredModels([]);
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

      <div className="grid grid-cols-2 gap-4">
        <div>
          <Label>{t("endpointForm.name")}</Label>
          <Input value={form.name} onChange={(e) => update("name", e.target.value)}
            placeholder={isSpeech ? "Southeast Asia" : "My AI Endpoint"} />
        </div>
        <div>
          <Label>{t("endpointForm.type")}</Label>
          <Select className="w-full" value={form.endpoint_type}
            onChange={(e) => update("endpoint_type", e.target.value)}>
            {Object.entries(VENDOR_BADGE).map(([k, v]) => (
              <option key={k} value={k}>{v.badge} {t(v.labelKey)}</option>
            ))}
          </Select>
        </div>
      </div>

      {isSpeech ? (
        <div className="space-y-4 mt-4">
          <div className="rounded-lg bg-amber-500/10 border border-amber-500/20 p-3">
            <p className="text-xs text-[var(--text-secondary)]">🎤 {t("endpointForm.speechNote")}</p>
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
                  ✓ {form.speech_endpoint?.includes(".azure.cn") || form.speech_region.startsWith("china")
                    ? t("endpointForm.chinaRegion") : t("endpointForm.globalRegion")}
                </p>
              )}
            </div>
          </div>
        </div>
      ) : (
        <>
          <div className="grid grid-cols-1 gap-4 mt-4">
            <div>
              <Label>{t("endpointForm.endpointUrl")}</Label>
              <Input value={form.url} onChange={(e) => update("url", e.target.value)}
                placeholder="https://your-endpoint.openai.azure.com/openai" />
            </div>
          </div>

          {activeProfile?.supports_aad && (
            <div className="flex gap-2 mt-4 mb-2">
              <button
                className={cn("px-3 py-1 text-xs rounded-md border transition-colors",
                  form.auth_mode === "api_key"
                    ? "bg-brand-500/20 border-brand-500/40 text-brand-400"
                    : "bg-transparent border-[var(--border-subtle)] text-[var(--text-muted)] hover:text-[var(--text-secondary)]",
                )}
                onClick={() => update("auth_mode", "api_key")}
              >{t("endpointForm.apiKeyDefault")}</button>
              <button
                className={cn("px-3 py-1 text-xs rounded-md border transition-colors",
                  form.auth_mode === "aad"
                    ? "bg-brand-500/20 border-brand-500/40 text-brand-400"
                    : "bg-transparent border-[var(--border-subtle)] text-[var(--text-muted)] hover:text-[var(--text-secondary)]",
                )}
                onClick={() => update("auth_mode", "aad")}
              >Microsoft Entra ID (AAD)</button>
            </div>
          )}

          {form.auth_mode !== "aad" && (
            <div className="grid grid-cols-2 gap-4 mt-2">
              <div>
                <Label>API Key</Label>
                <Input type="password" value={form.api_key}
                  onChange={(e) => update("api_key", e.target.value)} placeholder="sk-..." />
              </div>
              <div>
                <Label>
                  {t("endpointForm.region")}{" "}
                  {form.endpoint_type.startsWith("azure") ? "(AZURE)" : `(${t("endpointForm.optional")})`}
                </Label>
                <Input value={form.region} onChange={(e) => update("region", e.target.value)}
                  placeholder="eastus" />
              </div>
            </div>
          )}

          {form.auth_mode === "aad" && (
            <div className="grid grid-cols-2 gap-4 mt-2">
              <div>
                <Label>Tenant ID</Label>
                <Input value={form.azure_tenant_id}
                  onChange={(e) => update("azure_tenant_id", e.target.value)}
                  placeholder={t("endpointForm.aadTenantHint")} />
              </div>
              <div>
                <Label>{t("endpointForm.clientIdOptional")}</Label>
                <Input value={form.azure_client_id}
                  onChange={(e) => update("azure_client_id", e.target.value)}
                  placeholder={t("endpointForm.clientIdHint")} />
              </div>
              <div className="col-span-2">
                <AadLoginButton endpointId={form.name || "new"} tenantId={form.azure_tenant_id} clientId={form.azure_client_id} />
              </div>
            </div>
          )}

          {activeProfile && activeProfile.default_api_version && (
            <div className="mt-4" style={{ maxWidth: "50%" }}>
              <Label>{t("endpointForm.apiVersion")}</Label>
              <Input value={form.api_version}
                onChange={(e) => update("api_version", e.target.value)}
                placeholder={activeProfile.default_api_version} />
            </div>
          )}

          <Separator className="my-4" />

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
          )}

          <div className="flex items-end gap-2">
            <div className="flex-1">
              <Input value={newModelId} onChange={(e) => setNewModelId(e.target.value)}
                placeholder={form.endpoint_type === "azure_open_ai"
                  ? t("endpointForm.deploymentPlaceholder") : t("endpointForm.modelIdPlaceholder")}
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
        <Button variant="secondary" size="sm" onClick={onClose}>
          <X size={14} /> {t("endpointForm.cancel")}
        </Button>
        <Button size="sm" onClick={handleSave} disabled={!canSave}>
          <Check size={14} /> {t("endpointForm.save")}
        </Button>
      </div>
    </GlassCard>
  );
}
