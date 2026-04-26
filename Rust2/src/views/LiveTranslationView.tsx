import { useState, useRef, useEffect, useCallback } from "react";
import { useTranslation } from "react-i18next";
import {
  Mic, MicOff, RotateCcw, Copy, Download, Languages, AlertCircle,
  SplitSquareVertical, Lightbulb, Bookmark, Volume2,
} from "lucide-react";
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

type ViewMode = "bilingual" | "source-only" | "translation-only";

export function LiveTranslationView() {
  const { t } = useTranslation();
  const {
    isTranslating, setTranslating,
    recognizedSegments, addSegment, clearSegments,
    config, sessionId, setSessionId, showInfoBar,
  } = useAppStore();

  const [sourceLang, setSourceLang] = useState("zh-Hans");
  const [targetLang, setTargetLang] = useState("en");
  const [selectedSpeechEp, setSelectedSpeechEp] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [partialText, setPartialText] = useState("");
  const [viewMode, setViewMode] = useState<ViewMode>("bilingual");
  const [audioLevel, setAudioLevel] = useState(0);
  const [bookmarkedIdx, setBookmarkedIdx] = useState<Set<number>>(new Set());
  const [showHistory, setShowHistory] = useState(false);
  const scrollRef = useRef<HTMLDivElement>(null);
  const unlistenRef = useRef<UnlistenFn | null>(null);

  const speechEndpoints = (config?.endpoints ?? []).filter(
    (ep) => ep.endpoint_type === "azure_speech" && ep.enabled
  );

  useEffect(() => {
    if (!selectedSpeechEp && speechEndpoints.length > 0) {
      setSelectedSpeechEp(speechEndpoints[0].id);
    }
  }, [speechEndpoints, selectedSpeechEp]);

  useEffect(() => {
    if (scrollRef.current) scrollRef.current.scrollTop = scrollRef.current.scrollHeight;
  }, [recognizedSegments]);

  useEffect(() => {
    return () => {
      if (unlistenRef.current) {
        unlistenRef.current();
        unlistenRef.current = null;
      }
    };
  }, []);

  // 模拟音频电平（真实实现需要 cpal）
  useEffect(() => {
    if (!isTranslating) { setAudioLevel(0); return; }
    const iv = setInterval(() => setAudioLevel(Math.random() * 0.8 + 0.1), 120);
    return () => clearInterval(iv);
  }, [isTranslating]);

  const toggleBookmark = useCallback((idx: number) => {
    setBookmarkedIdx((prev) => {
      const next = new Set(prev);
      if (next.has(idx)) next.delete(idx); else next.add(idx);
      return next;
    });
  }, []);

  const handleCopyAll = useCallback(async () => {
    const text = recognizedSegments
      .map((s) => `[${s.time}] ${s.source}\n${s.translation}`)
      .join("\n\n");
    try {
      await navigator.clipboard.writeText(text);
      showInfoBar("已复制全部翻译内容", "success");
    } catch { showInfoBar("复制失败", "error"); }
  }, [recognizedSegments, showInfoBar]);

  const handleExport = useCallback(() => {
    const text = recognizedSegments
      .map((s) => `[${s.time}]\n原文: ${s.source}\n翻译: ${s.translation}`)
      .join("\n---\n");
    const blob = new Blob([text], { type: "text/plain;charset=utf-8" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = `translation_${new Date().toISOString().slice(0, 10)}.txt`;
    a.click();
    URL.revokeObjectURL(url);
  }, [recognizedSegments]);

  const handleToggle = useCallback(async () => {
    if (isTranslating) {
      if (sessionId) {
        try { await api.stopRealtimeTranslation(sessionId); } catch (e) { console.error("停止失败:", e); }
      }
      if (unlistenRef.current) { unlistenRef.current(); unlistenRef.current = null; }
      setTranslating(false);
      setSessionId(null);
      setError(null);
    } else {
      if (!selectedSpeechEp) { setError("请先在设置中添加 Azure Speech 端点"); return; }
      setError(null);
      try {
        const unlisten = await api.onRealtimeEvent((event) => {
          switch (event.type) {
            case "Recognized":
              break;
            case "Translated": {
              const data = event.data as { source_text?: string; translations?: Record<string, string> };
              const source = data.source_text || "";
              const translations = data.translations || {};
              const translation = Object.values(translations)[0] || "";
              if (source.trim()) {
                addSegment({ source, translation, time: new Date().toLocaleTimeString() });
                setPartialText("");
              }
              break;
            }
            case "Recognizing": {
              const data = event.data as { text?: string };
              setPartialText(data.text || "");
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
        if (unlistenRef.current) { unlistenRef.current(); unlistenRef.current = null; }
      }
    }
  }, [isTranslating, sessionId, selectedSpeechEp, sourceLang, targetLang, addSegment, setTranslating, setSessionId]);

  return (
    <div className="flex h-full">
      <div className="flex flex-col flex-1">
        {/* 顶部工具栏 */}
        <div className="flex items-center gap-3 px-6 py-3 border-b border-[var(--border-subtle)]"
          style={{ backgroundColor: "var(--toolbar-bg)" }}>
          <h1 className="text-base font-semibold text-[var(--text-primary)] mr-4">{t("live.title")}</h1>

          {/* Speech 端点选择 + 音频电平 */}
          <div className="flex items-center gap-2">
            {speechEndpoints.length > 0 ? (
              <Select value={selectedSpeechEp} onChange={(e) => setSelectedSpeechEp(e.target.value)} className="w-32">
                {speechEndpoints.map((ep) => <option key={ep.id} value={ep.id}>🎤 {ep.name}</option>)}
              </Select>
            ) : (
              <Badge variant="red" className="text-[10px]">未配置 Speech 端点</Badge>
            )}

            {/* 音频电平指示器 */}
            {isTranslating && (
              <div className="flex items-center gap-0.5 ml-1">
                <Volume2 size={12} className="text-brand-400" />
                <div className="flex items-end gap-px h-4">
                  {Array.from({ length: 8 }).map((_, i) => (
                    <div key={i} className="w-1 rounded-sm transition-all duration-75"
                      style={{
                        height: `${Math.max(3, audioLevel * 16 * (0.5 + Math.random() * 0.5))}px`,
                        backgroundColor: audioLevel > 0.6 ? "var(--brand-500)" : "var(--text-muted)",
                      }} />
                  ))}
                </div>
              </div>
            )}

            <span className="text-[var(--text-muted)]">|</span>
            <Select value={sourceLang} onChange={(e) => setSourceLang(e.target.value)} className="w-32">
              {LANGUAGES.map((l) => <option key={l.code} value={l.code}>{l.name}</option>)}
            </Select>
            <span className="text-[var(--text-muted)]">→</span>
            <Select value={targetLang} onChange={(e) => setTargetLang(e.target.value)} className="w-32">
              {LANGUAGES.map((l) => <option key={l.code} value={l.code}>{l.name}</option>)}
            </Select>
          </div>

          <div className="flex-1" />

          {/* 视图切换 + 操作按钮 */}
          <div className="flex items-center gap-1">
            <Button variant={viewMode === "bilingual" ? "secondary" : "ghost"} size="sm"
              onClick={() => setViewMode("bilingual")} title="双语对照">
              <SplitSquareVertical size={14} />
            </Button>
            <Button variant={viewMode === "source-only" ? "secondary" : "ghost"} size="sm"
              onClick={() => setViewMode("source-only")} title="仅原文">
              原
            </Button>
            <Button variant={viewMode === "translation-only" ? "secondary" : "ghost"} size="sm"
              onClick={() => setViewMode("translation-only")} title="仅译文">
              译
            </Button>
          </div>

          <span className="text-[var(--border-subtle)]">|</span>
          <Badge variant={isTranslating ? "green" : "gray"}>
            {isTranslating ? t("live.translating") : t("status.ready")}
          </Badge>
          <Button variant="ghost" size="sm" onClick={clearSegments} disabled={recognizedSegments.length === 0}>
            <RotateCcw size={14} />
          </Button>
          <Button variant="ghost" size="sm" onClick={handleCopyAll} disabled={recognizedSegments.length === 0}>
            <Copy size={14} />
          </Button>
          <Button variant="ghost" size="sm" onClick={handleExport} disabled={recognizedSegments.length === 0}>
            <Download size={14} />
          </Button>
          <Button variant="ghost" size="sm" onClick={() => setShowHistory(!showHistory)}>
            <Lightbulb size={14} />
          </Button>
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
                  <GlassCard className="py-3 group">
                    <div className="flex items-start gap-3">
                      <span className="text-[10px] text-[var(--text-placeholder)] pt-1 shrink-0 font-mono">{seg.time}</span>
                      <div className="flex-1 min-w-0 space-y-1">
                        {(viewMode === "bilingual" || viewMode === "source-only") && (
                          <p className="text-sm text-[var(--text-primary)]">{seg.source}</p>
                        )}
                        {(viewMode === "bilingual" || viewMode === "translation-only") && (
                          <p className="text-sm text-[var(--active-text)]">{seg.translation}</p>
                        )}
                      </div>
                      {/* 行操作按钮 */}
                      <div className="flex items-center gap-1 opacity-0 group-hover:opacity-100 transition-opacity shrink-0">
                        <button onClick={() => toggleBookmark(i)} className="p-1 hover:bg-white/10 rounded"
                          title="书签">
                          <Bookmark size={12} className={bookmarkedIdx.has(i) ? "text-amber-400 fill-amber-400" : "text-[var(--text-muted)]"} />
                        </button>
                        <button
                          onClick={() => {
                            navigator.clipboard.writeText(`${seg.source}\n${seg.translation}`);
                            showInfoBar("已复制", "success");
                          }}
                          className="p-1 hover:bg-white/10 rounded" title="复制">
                          <Copy size={12} className="text-[var(--text-muted)]" />
                        </button>
                      </div>
                    </div>
                  </GlassCard>
                </FadeIn>
              ))}
            </div>
          )}

          {/* 实时部分识别 */}
          {partialText && isTranslating && (
            <div className="max-w-3xl mx-auto mt-3">
              <GlassCard className="py-3 border-brand-500/30 border-dashed">
                <div className="flex items-center gap-2 px-3">
                  <Mic size={14} className="text-brand-400 animate-pulse shrink-0" />
                  <p className="text-sm text-[var(--text-muted)] italic">{partialText}</p>
                </div>
              </GlassCard>
            </div>
          )}
        </div>

        {/* 底部控制 */}
        <div className="border-t border-[var(--border-subtle)] p-4 flex items-center justify-center gap-4"
          style={{ backgroundColor: "var(--toolbar-bg)" }}>
          <span className="text-xs text-[var(--text-muted)]">
            {recognizedSegments.length} 条结果
            {bookmarkedIdx.size > 0 && ` · ${bookmarkedIdx.size} 条书签`}
          </span>
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

      {/* 右侧历史/洞察面板 */}
      {showHistory && (
        <div className="w-72 border-l border-[var(--border-subtle)] flex flex-col bg-[var(--surface-0)]">
          <div className="px-4 py-3 border-b border-[var(--border-subtle)] flex items-center justify-between">
            <span className="text-xs font-medium text-[var(--text-primary)]">书签 & 洞察</span>
            <Button variant="ghost" size="sm" onClick={() => setShowHistory(false)}>×</Button>
          </div>
          <div className="flex-1 overflow-y-auto p-3 space-y-2">
            {bookmarkedIdx.size === 0 ? (
              <p className="text-xs text-[var(--text-muted)] text-center py-8">点击 <Bookmark size={10} className="inline" /> 标记重要内容</p>
            ) : (
              [...bookmarkedIdx].sort().map((idx) => {
                const seg = recognizedSegments[idx];
                if (!seg) return null;
                return (
                  <div key={idx} className="p-2 rounded-lg bg-[var(--surface-1)] text-xs space-y-1">
                    <div className="flex items-center gap-1">
                      <Bookmark size={10} className="text-amber-400 fill-amber-400" />
                      <span className="text-[var(--text-muted)] font-mono">{seg.time}</span>
                    </div>
                    <p className="text-[var(--text-primary)]">{seg.source}</p>
                    <p className="text-[var(--active-text)]">{seg.translation}</p>
                  </div>
                );
              })
            )}
          </div>
        </div>
      )}
    </div>
  );
}
