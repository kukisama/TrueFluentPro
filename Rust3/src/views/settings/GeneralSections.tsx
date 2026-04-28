import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Loader2, TestTube } from "lucide-react";
import { cn } from "../../lib/utils";
import {
  Button, GlassCard, Input, Select, Label, Badge, Switch,
  SectionHeader, SettingRow, Textarea,
} from "../../components/ui";
import { useAppStore } from "../../stores/app-store";
import { ModelRefSelector, useConfigUpdater } from "../SettingsView";

/* ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
   Recognition Section
   ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━ */

export function RecognitionSection() {
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

/* ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
   Storage Section
   ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━ */

export function StorageSection() {
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
      // TODO: wire validate_storage_connection Tauri command when available
      await new Promise((_, reject) => setTimeout(() => reject(new Error("Not implemented yet")), 200));
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

/* ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
   Audio Section
   ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━ */

export function AudioSection() {
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

/* ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
   Insight Section
   ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━ */

export function InsightSection() {
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

/* ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
   Image Section
   ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━ */

export function ImageSection() {
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

/* ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
   Video Section
   ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━ */

export function VideoSection() {
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
