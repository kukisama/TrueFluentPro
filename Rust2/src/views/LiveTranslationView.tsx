import { useState, useRef, useEffect, useCallback } from "react";
import { useTranslation } from "react-i18next";
import { Mic, MicOff, RotateCcw, Copy, Download, ChevronDown, Languages, AlertCircle } from "lucide-react";
import {
  Button, GlassCard, Select, Badge, FadeIn, EmptyState,
} from "../components/ui";
import { useAppStore } from "../stores/app-store";
import { api, type UnlistenFn } from "../lib/tauri-api";

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
    config, sessionId, setSessionId,
  } = useAppStore();

  const [sourceLang, setSourceLang] = useState("zh-Hans");
  const [targetLang, setTargetLang] = useState("en");
  const [selectedSpeechEp, setSelectedSpeechEp] = useState("");
  const [error, setError] = useState<string | null>(null);
  const scrollRef = useRef<HTMLDivElement>(null);
  const unlistenRef = useRef<UnlistenFn | null>(null);

  // 获取所有 Speech 端点
  const speechEndpoints = (config?.endpoints ?? []).filter(
    (ep) => ep.endpoint_type === "azure_speech" && ep.enabled
  );

  // 自动选择第一个 Speech 端点
  useEffect(() => {
    if (!selectedSpeechEp && speechEndpoints.length > 0) {
      setSelectedSpeechEp(speechEndpoints[0].id);
    }
  }, [speechEndpoints, selectedSpeechEp]);

  useEffect(() => {
    if (scrollRef.current) scrollRef.current.scrollTop = scrollRef.current.scrollHeight;
  }, [recognizedSegments]);

  // 清理：组件卸载时停止翻译
  useEffect(() => {
    return () => {
      if (unlistenRef.current) {
        unlistenRef.current();
        unlistenRef.current = null;
      }
    };
  }, []);

  const handleToggle = useCallback(async () => {
    if (isTranslating) {
      // 停止
      if (sessionId) {
        try {
          await api.stopRealtimeTranslation(sessionId);
        } catch (e) {
          console.error("停止失败:", e);
        }
      }
      if (unlistenRef.current) {
        unlistenRef.current();
        unlistenRef.current = null;
      }
      setTranslating(false);
      setSessionId(null);
      setError(null);
    } else {
      // 开始
      if (!selectedSpeechEp) {
        setError("请先在设置中添加 Azure Speech 端点");
        return;
      }
      setError(null);
      try {
        // 注册事件监听
        const unlisten = await api.onRealtimeEvent((event) => {
          switch (event.type) {
            case "Recognized": {
              const text = (event.data as { text?: string }).text || "";
              if (text.trim()) {
                // Recognized 后续会收到 Translated 带翻译结果
              }
              break;
            }
            case "Translated": {
              const data = event.data as { source_text?: string; translations?: Record<string, string> };
              const source = data.source_text || "";
              const translations = data.translations || {};
              const translation = Object.values(translations)[0] || "";
              if (source.trim()) {
                addSegment({
                  source,
                  translation,
                  time: new Date().toLocaleTimeString(),
                });
              }
              break;
            }
            case "Recognizing": {
              // 部分识别结果，可用于实时显示
              break;
            }
            case "Error": {
              const msg = (event.data as { message?: string }).message || "未知错误";
              setError(msg);
              break;
            }
            case "SessionStopped": {
              setTranslating(false);
              setSessionId(null);
              break;
            }
          }
        });
        unlistenRef.current = unlisten;

        // 启动翻译会话
        const sid = await api.startRealtimeTranslation({
          source_lang: sourceLang,
          target_langs: [targetLang],
          endpoint_id: selectedSpeechEp,
          enable_partial: true,
          profanity_filter: false,
        });
        setSessionId(sid);
        setTranslating(true);
      } catch (e) {
        setError(String(e));
        if (unlistenRef.current) {
          unlistenRef.current();
          unlistenRef.current = null;
        }
      }
    }
  }, [isTranslating, sessionId, selectedSpeechEp, sourceLang, targetLang, addSegment, setTranslating, setSessionId]);

  return (
    <div className="flex flex-col h-full">
      {/* 顶部工具栏 */}
      <div className="flex items-center gap-3 px-6 py-3 border-b border-[var(--border-subtle)]"
        style={{ backgroundColor: "var(--toolbar-bg)" }}>
        <h1 className="text-base font-semibold text-[var(--text-primary)] mr-4">{t("live.title")}</h1>
        <div className="flex items-center gap-2">
          {/* Speech 端点选择 */}
          {speechEndpoints.length > 0 ? (
            <div className="relative">
              <Select value={selectedSpeechEp} onChange={(e) => setSelectedSpeechEp(e.target.value)} className="w-32">
                {speechEndpoints.map((ep) => <option key={ep.id} value={ep.id}>🎤 {ep.name}</option>)}
              </Select>
            </div>
          ) : (
            <Badge variant="red" className="text-[10px]">未配置 Speech 端点</Badge>
          )}
          <span className="text-[var(--text-muted)]">|</span>
          <div className="relative">
            <Select value={sourceLang} onChange={(e) => setSourceLang(e.target.value)} className="w-36">
              {LANGUAGES.map((l) => <option key={l.code} value={l.code}>{l.name}</option>)}
            </Select>
            <ChevronDown size={14} className="absolute right-2 top-1/2 -translate-y-1/2 text-[var(--text-muted)] pointer-events-none" />
          </div>
          <span className="text-[var(--text-muted)]">→</span>
          <div className="relative">
            <Select value={targetLang} onChange={(e) => setTargetLang(e.target.value)} className="w-36">
              {LANGUAGES.map((l) => <option key={l.code} value={l.code}>{l.name}</option>)}
            </Select>
            <ChevronDown size={14} className="absolute right-2 top-1/2 -translate-y-1/2 text-[var(--text-muted)] pointer-events-none" />
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
        {error && (
          <div className="mb-4 max-w-3xl mx-auto rounded-lg bg-red-500/10 border border-red-500/20 p-3 flex items-start gap-2">
            <AlertCircle size={16} className="text-red-400 shrink-0 mt-0.5" />
            <p className="text-xs text-red-300">{error}</p>
          </div>
        )}
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
                    <span className="text-[10px] text-[var(--text-placeholder)] pt-1 shrink-0 font-mono">{seg.time}</span>
                    <div className="flex-1 min-w-0 space-y-1">
                      <p className="text-sm text-[var(--text-primary)]">{seg.source}</p>
                      <p className="text-sm text-[var(--active-text)]">{seg.translation}</p>
                    </div>
                  </div>
                </GlassCard>
              </FadeIn>
            ))}
          </div>
        )}
      </div>

      {/* 底部控制 */}
      <div className="border-t border-[var(--border-subtle)] p-4 flex justify-center"
        style={{ backgroundColor: "var(--toolbar-bg)" }}>
        <Button
          variant={isTranslating ? "danger" : "primary"}
          size="lg"
          onClick={handleToggle}
          disabled={!isTranslating && speechEndpoints.length === 0}
          className="min-w-48 gap-3"
        >
          {isTranslating ? <><MicOff size={18} /> 停止翻译</> : <><Mic size={18} /> 开始翻译</>}
        </Button>
      </div>
    </div>
  );
}
