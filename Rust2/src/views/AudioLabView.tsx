import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Mic, Square, Volume2 } from "lucide-react";

import {
  Button, GlassCard, Select, Switch,
  Tabs, TabsList, TabsTrigger, TabsContent,
  FadeIn, Separator,
} from "../components/ui";

export function AudioLabView() {
  const { t } = useTranslation();
  const [isRecording, setIsRecording] = useState(false);
  const [recordingTime] = useState(0);

  return (
    <div className="flex flex-col h-full">
      <Tabs defaultValue="record" className="flex flex-col h-full">
        <div className="flex items-center gap-4 px-6 py-2.5 border-b border-white/[0.06] bg-white/[0.02]">
          <h1 className="text-base font-semibold text-slate-100 mr-2">{t("audioLab.title")}</h1>
          <TabsList>
            <TabsTrigger value="record">{t("audioLab.record")}</TabsTrigger>
            <TabsTrigger value="process">{t("audioLab.process")}</TabsTrigger>
            <TabsTrigger value="transcribe">{t("audioLab.transcribe")}</TabsTrigger>
            <TabsTrigger value="review">{t("audioLab.review")}</TabsTrigger>
          </TabsList>
        </div>

        <TabsContent value="record" className="flex-1 overflow-hidden">
          <RecordPanel isRecording={isRecording} setIsRecording={setIsRecording} time={recordingTime} />
        </TabsContent>
        <TabsContent value="process" className="flex-1 overflow-hidden">
          <ProcessPanel />
        </TabsContent>
        <TabsContent value="transcribe" className="flex-1 overflow-hidden">
          <TranscribePanel />
        </TabsContent>
        <TabsContent value="review" className="flex-1 overflow-hidden">
          <ReviewPanel />
        </TabsContent>
      </Tabs>
    </div>
  );
}

function RecordPanel({
  isRecording, setIsRecording, time,
}: {
  isRecording: boolean;
  setIsRecording: (v: boolean) => void;
  time: number;
}) {
  const { t } = useTranslation();

  return (
    <div className="flex flex-col items-center justify-center h-full gap-8">
      {/* 波形占位 */}
      <GlassCard className="w-full max-w-xl h-32 flex items-center justify-center">
        {isRecording ? (
          <div className="flex items-center gap-1">
            {Array.from({ length: 32 }).map((_, i) => (
              <div
                key={i}
                className="w-0.5 bg-brand-500 rounded-full animate-pulse"
                style={{
                  height: `${20 + Math.random() * 60}%`,
                  animationDelay: `${i * 50}ms`,
                  animationDuration: `${600 + Math.random() * 400}ms`,
                }}
              />
            ))}
          </div>
        ) : (
          <span className="text-slate-600 text-sm">{t("audioLab.waitingRecord")}</span>
        )}
      </GlassCard>

      {/* 时间 */}
      <div className="text-4xl font-mono text-slate-200 tabular-nums">
        {formatTime(time)}
      </div>

      {/* 设备选择 */}
      <div className="flex items-center gap-6 text-sm">
        <div className="flex items-center gap-2 text-slate-400">
          <Mic size={14} />
          <Select className="w-44 text-xs">
            <option>{t("audioLab.defaultMic")}</option>
          </Select>
        </div>
        <div className="flex items-center gap-2 text-slate-400">
          <Volume2 size={14} />
          <Select className="w-44 text-xs">
            <option>{t("audioLab.defaultLoopback")}</option>
          </Select>
        </div>
      </div>

      {/* 控制按钮 */}
      <div className="flex gap-4">
        <Button
          variant={isRecording ? "danger" : "primary"}
          size="lg"
          onClick={() => setIsRecording(!isRecording)}
          className="min-w-40"
        >
          {isRecording ? <><Square size={18} /> 停止</> : <><Mic size={18} /> 开始录音</>}
        </Button>
      </div>
    </div>
  );
}

function ProcessPanel() {
  const { t } = useTranslation();
  return (
    <div className="p-6 max-w-xl mx-auto space-y-6">
      <FadeIn>
        <h2 className="text-base font-semibold text-slate-200 mb-4">{t("audioLab.audioProcessing")}</h2>
        <GlassCard className="space-y-4">
          <SettingRow label={t("audioLab.noiseReduction")} description={t("audioLab.noiseReductionDesc")}>
            <Switch defaultChecked />
          </SettingRow>
          <SettingRow label={t("audioLab.echoCancellation")} description={t("audioLab.echoCancellationDesc")}>
            <Switch defaultChecked />
          </SettingRow>
          <SettingRow label={t("audioLab.gainControl")} description={t("audioLab.gainControlDesc")}>
            <Switch />
          </SettingRow>
          <Separator />
          <Button className="w-full">{t("audioLab.startProcessing")}</Button>
        </GlassCard>
      </FadeIn>
    </div>
  );
}

function TranscribePanel() {
  const { t } = useTranslation();
  return (
    <div className="p-6 max-w-xl mx-auto space-y-6">
      <FadeIn>
        <h2 className="text-base font-semibold text-slate-200 mb-4">{t("audioLab.speechToText")}</h2>
        <GlassCard className="min-h-[200px] flex items-center justify-center">
          <p className="text-sm text-slate-500">{t("audioLab.transcribeHint")}</p>
        </GlassCard>
        <Button className="w-full mt-4">{t("audioLab.startTranscribe")}</Button>
      </FadeIn>
    </div>
  );
}

function ReviewPanel() {
  const { t } = useTranslation();
  return (
    <div className="p-6 max-w-xl mx-auto">
      <FadeIn>
        <h2 className="text-base font-semibold text-slate-200 mb-4">{t("audioLab.transcribeReview")}</h2>
        <GlassCard className="min-h-[200px] flex items-center justify-center">
          <p className="text-sm text-slate-500">{t("audioLab.reviewHint")}</p>
        </GlassCard>
      </FadeIn>
    </div>
  );
}

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

function formatTime(seconds: number): string {
  const m = Math.floor(seconds / 60).toString().padStart(2, "0");
  const s = (seconds % 60).toString().padStart(2, "0");
  return `${m}:${s}`;
}
