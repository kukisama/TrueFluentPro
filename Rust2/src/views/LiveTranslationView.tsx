import { useState, useRef, useEffect } from "react";
import { useTranslation } from "react-i18next";
import { Mic, MicOff, RotateCcw, Copy, Download, ChevronDown, Languages } from "lucide-react";
import {
  Button, GlassCard, Select, Badge, FadeIn, EmptyState,
} from "../components/ui";
import { useAppStore } from "../stores/app-store";

const LANGUAGES = [
  { code: "zh-Hans", name: "中文（简体）" },
  { code: "en", name: "English" },
  { code: "ja", name: "日本語" },
  { code: "ko", name: "한국어" },
  { code: "fr", name: "Français" },
  { code: "de", name: "Deutsch" },
  { code: "es", name: "Español" },
  { code: "ru", name: "Русский" },
];

export function LiveTranslationView() {
  const { t } = useTranslation();
  const {
    isTranslating, setTranslating,
    recognizedSegments, addSegment, clearSegments,
    config,
  } = useAppStore();

  const [sourceLang, setSourceLang] = useState("zh-Hans");
  const [targetLang, setTargetLang] = useState("en");
  const scrollRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (scrollRef.current) {
      scrollRef.current.scrollTop = scrollRef.current.scrollHeight;
    }
  }, [recognizedSegments]);

  const handleToggle = () => {
    if (isTranslating) {
      setTranslating(false);
    } else {
      if (!config?.endpoints.length) {
        useAppStore.getState().setError("请先在设置中配置翻译端点");
        return;
      }
      setTranslating(true);
      // TODO: 接 Speech SDK 实时翻译
      // 模拟演示数据
      const samples = [
        { source: "大家好，欢迎参加今天的会议。", translation: "Hello everyone, welcome to today's meeting." },
        { source: "今天我们讨论一下产品路线图。", translation: "Today we'll discuss the product roadmap." },
        { source: "首先看一下上个季度的完成情况。", translation: "Let's first look at last quarter's progress." },
      ];
      let i = 0;
      const timer = setInterval(() => {
        if (i >= samples.length || !useAppStore.getState().isTranslating) {
          clearInterval(timer);
          return;
        }
        addSegment({ ...samples[i], time: new Date().toLocaleTimeString() });
        i++;
      }, 2000);
    }
  };

  return (
    <div className="flex flex-col h-full">
      {/* 顶部工具栏 */}
      <div className="flex items-center gap-3 px-6 py-3 border-b border-white/[0.06] bg-white/[0.02]">
        <h1 className="text-base font-semibold text-slate-100 mr-4">{t("live.title")}</h1>

        <div className="flex items-center gap-2">
          <div className="relative">
            <Select value={sourceLang} onChange={(e) => setSourceLang(e.target.value)} className="w-36">
              {LANGUAGES.map((l) => <option key={l.code} value={l.code}>{l.name}</option>)}
            </Select>
            <ChevronDown size={14} className="absolute right-2 top-1/2 -translate-y-1/2 text-slate-500 pointer-events-none" />
          </div>
          <span className="text-slate-500">→</span>
          <div className="relative">
            <Select value={targetLang} onChange={(e) => setTargetLang(e.target.value)} className="w-36">
              {LANGUAGES.map((l) => <option key={l.code} value={l.code}>{l.name}</option>)}
            </Select>
            <ChevronDown size={14} className="absolute right-2 top-1/2 -translate-y-1/2 text-slate-500 pointer-events-none" />
          </div>
        </div>

        <div className="flex-1" />

        <Badge variant={isTranslating ? "green" : "gray"}>
          {isTranslating ? t("live.translating") : t("status.ready")}
        </Badge>

        <Button variant="ghost" size="sm" onClick={clearSegments} disabled={recognizedSegments.length === 0}>
          <RotateCcw size={14} /> {t("live.clear")}
        </Button>
        <Button variant="ghost" size="sm"><Copy size={14} /> {t("live.copyAll")}</Button>
        <Button variant="ghost" size="sm"><Download size={14} /> {t("live.export")}</Button>
      </div>

      {/* 翻译结果区 */}
      <div ref={scrollRef} className="flex-1 overflow-y-auto p-6">
        {recognizedSegments.length === 0 ? (
          <EmptyState
            icon={<Languages size={48} />}
            title={t("live.startHint")}
            description={t("live.startSubHint")}
          />
        ) : (
          <div className="space-y-3 max-w-3xl mx-auto">
            {recognizedSegments.map((seg, i) => (
              <FadeIn key={i}>
                <GlassCard className="py-3">
                  <div className="flex items-start gap-3">
                    <span className="text-[10px] text-slate-600 pt-1 shrink-0 font-mono">{seg.time}</span>
                    <div className="flex-1 min-w-0 space-y-1">
                      <p className="text-sm text-slate-200">{seg.source}</p>
                      <p className="text-sm text-brand-300/80">{seg.translation}</p>
                    </div>
                  </div>
                </GlassCard>
              </FadeIn>
            ))}
          </div>
        )}
      </div>

      {/* 底部控制 */}
      <div className="border-t border-white/[0.06] bg-white/[0.02] p-4 flex justify-center">
        <Button
          variant={isTranslating ? "danger" : "primary"}
          size="lg"
          onClick={handleToggle}
          className="min-w-48 gap-3"
        >
          {isTranslating ? (
            <><MicOff size={18} /> 停止翻译</>
          ) : (
            <><Mic size={18} /> 开始翻译</>
          )}
        </Button>
      </div>
    </div>
  );
}
