import { useState } from "react";
import { useTranslation } from "react-i18next";
import { save as dialogSave, open as dialogOpen } from "@tauri-apps/plugin-dialog";
import {
  Shield, Zap, Sun, Moon, Monitor,
  Download, Upload, Copy, Clipboard,
} from "lucide-react";
import { cn } from "../../lib/utils";
import {
  Button, GlassCard, Input, Select, Label, Badge, Switch,
  SectionHeader, SettingRow,
} from "../../components/ui";
import { useAppStore } from "../../stores/app-store";
import { useThemeStore, type ThemeMode } from "../../stores/theme-store";
import { api } from "../../lib/api";
import { useConfigUpdater } from "../SettingsView";

/* ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
   Search Section
   ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━ */

export function SearchSection() {
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

/* ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
   Cloud Section
   ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━ */

export function CloudSection() {
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

/* ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
   Transfer Section
   ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━ */

export function TransferSection() {
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
      const partial = JSON.parse(json) as Record<string, unknown>;
      const currentJson = await api.exportConfig();
      const current = JSON.parse(currentJson) as Record<string, unknown>;
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
        const partial = JSON.parse(json) as Record<string, unknown>;
        const currentJson = await api.exportConfig();
        const current = JSON.parse(currentJson) as Record<string, unknown>;
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

/* ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
   UI Section
   ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━ */

export function UiSection() {
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

/* ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
   About Section
   ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━ */

export function AboutSection() {
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
