import { useState, useRef, useEffect, useCallback } from "react";
import { useTranslation } from "react-i18next";
import {
  Mic, MicOff, RotateCcw, Copy, Download, Languages, AlertCircle,
  SplitSquareVertical, Lightbulb, Bookmark, Volume2, Subtitles,
} from "lucide-react";
import {
  Button, GlassCard, Select, Badge, FadeIn, EmptyState,
} from "../components/ui";
import { useAppStore } from "../stores/app-store";
import { api, type UnlistenFn, type AudioDeviceInfo, type SupportedLanguage } from "../lib/tauri-api";
import { cn } from "../lib/utils";

type ViewMode = "bilingual" | "source-only" | "translation-only";

export function LiveTranslationView() {
  const { t } = useTranslation();
  const {
    isTranslating, setTranslating,
    recognizedSegments, addSegment, clearSegments,
    config, sessionId, setSessionId, showInfoBar,
  } = useAppStore();

  const [sourceLang, setSourceLang] = useState("auto");
  const [targetLang, setTargetLang] = useState("en");
  // PR-2: 从后端动态获取语言列表（分 source/target）
  const [sourceLangs, setSourceLangs] = useState<SupportedLanguage[]>([]);
  const [targetLangs, setTargetLangs] = useState<SupportedLanguage[]>([]);
  const [currentProvider] = useState("azure_speech");
  useEffect(() => {
    api.liveListSupportedLanguages(currentProvider).then((list) => {
      if (list && list.length > 0) {
        setSourceLangs(list.filter(l => l.kind === "source" || l.kind === "both"));
        setTargetLangs(list.filter(l => l.kind === "target" || l.kind === "both"));
      }
    }).catch(() => {});
  }, [currentProvider]);
  const [selectedSpeechEp, setSelectedSpeechEp] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [partialText, setPartialText] = useState("");
  const [viewMode, setViewMode] = useState<ViewMode>("bilingual");
  const [audioLevel, setAudioLevel] = useState(0);
  // 音频设备枚举
  const [inputDevices, setInputDevices] = useState<AudioDeviceInfo[]>([]);
  const [selectedInputDevice, setSelectedInputDevice] = useState("");
  useEffect(() => {
    api.listAudioDevices().then((devices) => {
      const inputs = devices.filter(d => d.device_type === "Input");
      setInputDevices(inputs);
      const defaultDev = inputs.find(d => d.is_default);
      if (defaultDev) setSelectedInputDevice(defaultDev.id);
      else if (inputs.length > 0) setSelectedInputDevice(inputs[0].id);
    }).catch(() => {});
  }, []);
  // PR-2: 书签持久化到后端 DB（通过 segment_id 跟踪）
  const [bookmarkedIds, setBookmarkedIds] = useState<Set<string>>(new Set());
  // 同时保留 index-based 书签用于内存段落（还没持久化的 partial segments）
  const [bookmarkedIdx, setBookmarkedIdx] = useState<Set<number>>(new Set());
  const [showHistory, setShowHistory] = useState(false);
  // PR-3: 悬浮窗状态由后端管理，通过 floating-window-state-changed 事件同步
  const [isSubtitleOpen, setIsSubtitleOpen] = useState(false);
  const [isInsightOpen, setIsInsightOpen] = useState(false);
  const scrollRef = useRef<HTMLDivElement>(null);
  const unlistenRef = useRef<UnlistenFn | null>(null);
  // O-23: 自动重连状态
  const reconnectTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const reconnectCountRef = useRef(0);
  const MAX_RECONNECTS = 3;
  const intentionalStopRef = useRef(false);
  // N-06: 重连守卫 — 防止并发重连
  const isReconnectingRef = useRef(false);

  // PR-2: F5 刷新恢复 — 页面加载时检查是否有活跃会话
  useEffect(() => {
    (async () => {
      try {
        const activeSession = await api.liveGetActiveSession();
        if (activeSession) {
          setSessionId(activeSession.id);
          // 恢复最近段落
          const segments = await api.liveGetRecentSegments(activeSession.id, 200);
          for (const seg of segments) {
            addSegment({
              source: seg.original_text,
              translation: seg.translated_text || "",
              time: seg.started_at ? (seg.started_at.split("T")[1]?.split(".")[0] || seg.started_at) : "",
            });
            if (seg.is_bookmarked) {
              setBookmarkedIds(prev => new Set(prev).add(seg.id));
            }
          }
          showInfoBar(t("live.sessionRestored", { count: segments.length }), "info");
        }
      } catch { /* 首次启动无活跃会话，正常 */ }
    })();
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  // PR-2: 监听后端 segment-updated 事件
  useEffect(() => {
    const unlisten = api.onSegmentUpdated((seg) => {
      if (seg.is_bookmarked) {
        setBookmarkedIds(prev => new Set(prev).add(seg.segment_id));
      } else {
        setBookmarkedIds(prev => {
          const next = new Set(prev);
          next.delete(seg.segment_id);
          return next;
        });
      }
    });
    return () => { unlisten.then(fn => fn()); };
  }, []);

  // PR-3: 监听悬浮窗状态事件
  useEffect(() => {
    const unlisten = api.onFloatingWindowStateChanged((e) => {
      if (e.window === "subtitle") setIsSubtitleOpen(e.open);
      if (e.window === "insight") setIsInsightOpen(e.open);
    });
    return () => { unlisten.then(fn => fn()); };
  }, []);

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
      // O-23: 清理重连定时器
      if (reconnectTimerRef.current) {
        clearTimeout(reconnectTimerRef.current);
        reconnectTimerRef.current = null;
      }
    };
  }, []);

  // 基于语音识别活动的音频电平指示（非随机数）
  // 原理：收到 Recognizing 事件时 partialText 变化 → 表示有语音输入 → 电平升高
  //       无新识别结果时 → 逐渐衰减至零
  const lastPartialRef = useRef("");
  useEffect(() => {
    if (!isTranslating) { setAudioLevel(0); return; }
    if (partialText !== lastPartialRef.current) {
      // 有新的识别文字 → 语音活跃
      lastPartialRef.current = partialText;
      setAudioLevel(0.7); // 语音活跃时固定电平
    }
    // 定时衰减：若 300ms 内无新 partialText 变化，逐渐降低电平
    const decay = setInterval(() => {
      setAudioLevel((prev) => Math.max(0, prev - 0.08));
    }, 150);
    return () => clearInterval(decay);
  }, [isTranslating, partialText]);

  const toggleBookmark = useCallback((idx: number, segmentId?: string) => {
    // PR-2: 有 segment_id → 通过后端持久化
    if (segmentId) {
      const isCurrentlyBookmarked = bookmarkedIds.has(segmentId);
      const promise = isCurrentlyBookmarked
        ? api.liveUnbookmarkSegment(segmentId)
        : api.liveBookmarkSegment(segmentId);
      promise.catch(() => showInfoBar(t("live.bookmarkFailed"), "error"));
      return; // 后端会通过 segment-updated 事件更新 bookmarkedIds
    }
    // 无 segment_id → 内存 index 模式（兼容未持久化的段落）
    setBookmarkedIdx((prev) => {
      const next = new Set(prev);
      if (next.has(idx)) next.delete(idx); else next.add(idx);
      return next;
    });
  }, [bookmarkedIds, showInfoBar]);

  const handleCopyAll = useCallback(async () => {
    const text = recognizedSegments
      .map((s) => `[${s.time}] ${s.source}\n${s.translation}`)
      .join("\n\n");
    try {
      await navigator.clipboard.writeText(text);
      showInfoBar(t("live.copiedAll"), "success");
    } catch { showInfoBar(t("live.copyFailed"), "error"); }
  }, [recognizedSegments, showInfoBar]);

  const handleExport = useCallback(() => {
    const text = recognizedSegments
      .map((s) => `[${s.time}]\n${t("live.exportSource")}${s.source}\n${t("live.exportTranslation")}${s.translation}`)
      .join("\n---\n");
    const blob = new Blob([text], { type: "text/plain;charset=utf-8" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = `translation_${new Date().toISOString().slice(0, 10)}.txt`;
    a.click();
    URL.revokeObjectURL(url);
  }, [recognizedSegments]);

  // O-22: SRT 字幕文件导出
  const handleExportSrt = useCallback(() => {
    const lines = recognizedSegments.map((s, i) => {
      const start = timeToSrtTimestamp(s.time, 0);
      const end = timeToSrtTimestamp(s.time, 3); // 每段约 3 秒
      return `${i + 1}\n${start} --> ${end}\n${s.source}\n${s.translation}`;
    });
    const content = lines.join("\n\n");
    downloadFile(content, `translation_${new Date().toISOString().slice(0, 10)}.srt`, "text/srt;charset=utf-8");
  }, [recognizedSegments]);

  // O-22: VTT 字幕文件导出
  const handleExportVtt = useCallback(() => {
    const lines = recognizedSegments.map((s) => {
      const start = timeToVttTimestamp(s.time, 0);
      const end = timeToVttTimestamp(s.time, 3);
      return `${start} --> ${end}\n${s.source}\n${s.translation}`;
    });
    const content = "WEBVTT\n\n" + lines.join("\n\n");
    downloadFile(content, `translation_${new Date().toISOString().slice(0, 10)}.vtt`, "text/vtt;charset=utf-8");
  }, [recognizedSegments]);

  const handleToggle = useCallback(async () => {
    if (isTranslating) {
      // O-23: 标记为主动停止，不触发重连
      intentionalStopRef.current = true;
      if (reconnectTimerRef.current) { clearTimeout(reconnectTimerRef.current); reconnectTimerRef.current = null; }
      reconnectCountRef.current = 0;
      if (sessionId) {
        try { await api.stopRealtimeTranslation(sessionId); } catch (e) { console.error("stop failed:", e); }
      }
      if (unlistenRef.current) { unlistenRef.current(); unlistenRef.current = null; }
      setTranslating(false);
      setSessionId(null);
      setError(null);
    } else {
      if (!selectedSpeechEp) { setError(t("live.noSpeechEndpoint")); return; }
      setError(null);
      intentionalStopRef.current = false;
      reconnectCountRef.current = 0;
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
              const msg = (event.data as { message?: string }).message || t("live.unknownError");
              setError(msg);
              // O-23: 非主动停止时尝试自动重连
              if (!intentionalStopRef.current && reconnectCountRef.current < MAX_RECONNECTS && !isReconnectingRef.current) {
                isReconnectingRef.current = true;
                reconnectCountRef.current++;
                const delay = Math.min(2000 * reconnectCountRef.current, 10000);
                setError(t("live.reconnecting", { seconds: delay / 1000, count: reconnectCountRef.current, max: MAX_RECONNECTS }));
                reconnectTimerRef.current = setTimeout(async () => {
                  reconnectTimerRef.current = null;
                  // N-06: 先清理旧会话再重连，用守卫替代 double setTimeout
                  if (unlistenRef.current) { unlistenRef.current(); unlistenRef.current = null; }
                  setTranslating(false);
                  setSessionId(null);
                  isReconnectingRef.current = false;
                  handleToggle();
                }, delay);
              }
              break;
            }
            case "SessionStopped": {
              // O-23: 非主动停止 → 尝试重连
              if (!intentionalStopRef.current && reconnectCountRef.current < MAX_RECONNECTS && !isReconnectingRef.current) {
                isReconnectingRef.current = true;
                reconnectCountRef.current++;
                const delay = Math.min(2000 * reconnectCountRef.current, 10000);
                setError(t("live.sessionDisconnected", { seconds: delay / 1000, count: reconnectCountRef.current, max: MAX_RECONNECTS }));
                reconnectTimerRef.current = setTimeout(async () => {
                  reconnectTimerRef.current = null;
                  if (unlistenRef.current) { unlistenRef.current(); unlistenRef.current = null; }
                  setTranslating(false);
                  setSessionId(null);
                  isReconnectingRef.current = false;
                  handleToggle();
                }, delay);
              } else {
                setTranslating(false);
                setSessionId(null);
              }
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

  // O-39: 监听 F6 自定义事件来停止翻译
  useEffect(() => {
    const onStop = () => { if (isTranslating) handleToggle(); };
    window.addEventListener("tfp:stop-translation", onStop);
    return () => window.removeEventListener("tfp:stop-translation", onStop);
  }, [isTranslating, handleToggle]);

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
              <Badge variant="red" className="text-[10px]">{t("live.noSpeechBadge")}</Badge>
            )}

            {/* 音频电平指示器 */}
            {isTranslating && (
              <div className="flex items-center gap-0.5 ml-1">
                <Volume2 size={12} className="text-brand-400" />
                <div className="flex items-end gap-px h-4">
                  {Array.from({ length: 8 }).map((_, i) => (
                    <div key={i} className="w-1 rounded-sm transition-all duration-75"
                      style={{
                        height: `${Math.max(3, audioLevel * 16 * (0.4 + 0.6 * Math.sin(i * 1.2 + 0.5)))}px`,
                        backgroundColor: audioLevel > 0.6 ? "var(--brand-500)" : "var(--text-muted)",
                      }} />
                  ))}
                </div>
              </div>
            )}

            {/* 输入设备选择 */}
            {inputDevices.length > 0 && (
              <>
                <span className="text-[var(--text-muted)]">|</span>
                <Select value={selectedInputDevice} onChange={(e) => setSelectedInputDevice(e.target.value)} className="w-36">
                  {inputDevices.map((d) => <option key={d.id} value={d.id}>{d.is_default ? "🎙️ " : ""}{d.name}</option>)}
                </Select>
              </>
            )}

            <span className="text-[var(--text-muted)]">|</span>
            <Select value={sourceLang} onChange={(e) => setSourceLang(e.target.value)} className="w-32">
              {sourceLangs.map((l) => <option key={l.code} value={l.code}>{l.label}</option>)}
            </Select>
            <span className="text-[var(--text-muted)]">→</span>
            <Select value={targetLang} onChange={(e) => setTargetLang(e.target.value)} className="w-32">
              {targetLangs.map((l) => <option key={l.code} value={l.code}>{l.label}</option>)}
            </Select>
          </div>

          <div className="flex-1" />

          {/* 视图切换 + 操作按钮 */}
          <div className="flex items-center gap-1">
            <Button variant={viewMode === "bilingual" ? "secondary" : "ghost"} size="sm"
              onClick={() => setViewMode("bilingual")} title={t("live.bilingual")}>
              <SplitSquareVertical size={14} />
            </Button>
            <Button variant={viewMode === "source-only" ? "secondary" : "ghost"} size="sm"
              onClick={() => setViewMode("source-only")} title={t("live.sourceOnly")}>
              {t("live.source")}
            </Button>
            <Button variant={viewMode === "translation-only" ? "secondary" : "ghost"} size="sm"
              onClick={() => setViewMode("translation-only")} title={t("live.translationOnly")}>
              {t("live.translation")}
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
          <Button variant="ghost" size="sm" onClick={handleExport} disabled={recognizedSegments.length === 0} title={t("live.exportTxt")}>
            <Download size={14} />
          </Button>
          {/* O-22: SRT/VTT 字幕导出 */}
          <Button variant="ghost" size="sm" onClick={handleExportSrt} disabled={recognizedSegments.length === 0} title={t("live.exportSrt")}>
            <Subtitles size={12} /><span className="text-[10px]">SRT</span>
          </Button>
          <Button variant="ghost" size="sm" onClick={handleExportVtt} disabled={recognizedSegments.length === 0} title={t("live.exportVtt")}>
            <Subtitles size={12} /><span className="text-[10px]">VTT</span>
          </Button>
          <Button variant="ghost" size="sm" onClick={() => setShowHistory(!showHistory)}>
            <Lightbulb size={14} />
          </Button>
          <Button variant={isSubtitleOpen ? "secondary" : "ghost"} size="sm"
            onClick={() => api.liveToggleFloatingSubtitle()} title={t("live.floatingSubtitle")}>
            <Subtitles size={14} />
          </Button>
          <Button variant={isInsightOpen ? "secondary" : "ghost"} size="sm"
            onClick={() => { isInsightOpen ? api.liveHideFloatingInsight() : api.liveShowFloatingInsight(); }} title={t("live.floatingInsight")}>
            🧠
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
          ) : (() => {
            // O-21: 转录段落合并 + 说话人分色（6 色调色板）
            const SPEAKER_COLORS = [
              "border-l-blue-400", "border-l-emerald-400", "border-l-amber-400",
              "border-l-purple-400", "border-l-rose-400", "border-l-cyan-400",
            ];
            // 合并连续段落：相邻段落时间差 < 3 秒且源文本可拼接
            type MergedSeg = { source: string; translation: string; time: string; endTime: string; speakerIdx: number; originalIndices: number[] };
            const merged: MergedSeg[] = [];
            let currentSpeaker = 0;
            // S-2: 应用 max_history_items 截断，避免长时间运行后渲染性能下降
            const maxHistory = config?.recognition?.max_history_items ?? 500;
            const visibleSegments = recognizedSegments.slice(-maxHistory);
            for (let i = 0; i < visibleSegments.length; i++) {
              const seg = visibleSegments[i];
              const prev = merged[merged.length - 1];
              // 简易说话人检测: 时间间隔 > 5 秒视为换人
              const gap = prev ? timeGapSeconds(prev.endTime, seg.time) : 999;
              if (gap > 5) currentSpeaker = (currentSpeaker + 1) % SPEAKER_COLORS.length;
              // 合并：同一说话人、间隔 < 3 秒
              if (prev && gap < 3 && prev.speakerIdx === currentSpeaker) {
                prev.source += " " + seg.source;
                prev.translation += " " + seg.translation;
                prev.endTime = seg.time;
                prev.originalIndices.push(i);
              } else {
                merged.push({ source: seg.source, translation: seg.translation, time: seg.time, endTime: seg.time, speakerIdx: currentSpeaker, originalIndices: [i] });
              }
            }
            return (
            <div className="space-y-3 max-w-3xl mx-auto">
              {merged.map((seg, mi) => (
                <FadeIn key={mi}>
                  <GlassCard className={cn("py-3 group border-l-2", SPEAKER_COLORS[seg.speakerIdx])}>
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
                        <button onClick={() => toggleBookmark(seg.originalIndices[0])} className="p-1 hover:bg-white/10 rounded"
                          title={t("live.bookmark")}>
                          <Bookmark size={12} className={bookmarkedIdx.has(seg.originalIndices[0]) ? "text-amber-400 fill-amber-400" : "text-[var(--text-muted)]"} />
                        </button>
                        <button
                          onClick={() => {
                            navigator.clipboard.writeText(`${seg.source}\n${seg.translation}`);
                            showInfoBar(t("live.copied"), "success");
                          }}
                          className="p-1 hover:bg-white/10 rounded" title={t("live.copy")}>
                          <Copy size={12} className="text-[var(--text-muted)]" />
                        </button>
                      </div>
                    </div>
                  </GlassCard>
                </FadeIn>
              ))}
            </div>
            );
          })()}

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
            {t("live.results", { count: recognizedSegments.length })}
            {(bookmarkedIdx.size + bookmarkedIds.size) > 0 && ` · ${t("live.bookmarks", { count: bookmarkedIdx.size + bookmarkedIds.size })}`}
          </span>
          <Button
            variant={isTranslating ? "danger" : "primary"}
            size="lg"
            onClick={handleToggle}
            disabled={!isTranslating && speechEndpoints.length === 0}
            className="min-w-48 gap-3"
          >
            {isTranslating ? <><MicOff size={18} /> {t("live.stopTranslation")}</> : <><Mic size={18} /> {t("live.startTranslation")}</>}
          </Button>
        </div>
      </div>

      {/* 右侧历史/洞察面板 */}
      {showHistory && (
        <HistoryPanel
          recognizedSegments={recognizedSegments}
          bookmarkedIdx={bookmarkedIdx}
          onClose={() => setShowHistory(false)}
          showInfoBar={showInfoBar}
        />
      )}
    </div>
  );
}

// O-21: 计算两个时间字符串之间的秒数差（格式如 "10:30:05" 或 "10:30"）
function timeGapSeconds(t1: string, t2: string): number {
  const parse = (t: string) => {
    const parts = t.split(":").map(Number);
    if (parts.length === 3) return parts[0] * 3600 + parts[1] * 60 + parts[2];
    if (parts.length === 2) return parts[0] * 60 + parts[1];
    return 0;
  };
  return Math.abs(parse(t2) - parse(t1));
}

// O-22: SRT/VTT 时间戳工具
function timeToSrtTimestamp(timeStr: string, offsetSec: number): string {
  const parts = timeStr.split(":").map(Number);
  let totalSec = 0;
  if (parts.length === 3) totalSec = parts[0] * 3600 + parts[1] * 60 + parts[2];
  else if (parts.length === 2) totalSec = parts[0] * 60 + parts[1];
  totalSec += offsetSec;
  const h = Math.floor(totalSec / 3600);
  const m = Math.floor((totalSec % 3600) / 60);
  const s = Math.floor(totalSec % 60);
  return `${String(h).padStart(2, "0")}:${String(m).padStart(2, "0")}:${String(s).padStart(2, "0")},000`;
}

function timeToVttTimestamp(timeStr: string, offsetSec: number): string {
  return timeToSrtTimestamp(timeStr, offsetSec).replace(",", ".");
}

function downloadFile(content: string, fileName: string, mimeType: string) {
  const blob = new Blob([content], { type: mimeType });
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = fileName;
  a.click();
  URL.revokeObjectURL(url);
}

// PR-4: 历史面板组件
import type { TranslationSession, TranslationSegment } from "../lib/tauri-api";

function HistoryPanel({
  recognizedSegments,
  bookmarkedIdx,
  onClose,
  showInfoBar,
}: {
  recognizedSegments: Array<{ source: string; translation: string; time: string }>;
  bookmarkedIdx: Set<number>;
  onClose: () => void;
  showInfoBar: (msg: string, type: "success" | "error" | "info") => void;
}) {
  const { t } = useTranslation();
  const [tab, setTab] = useState<"bookmarks" | "history">("bookmarks");
  const [sessions, setSessions] = useState<TranslationSession[]>([]);
  const [selectedSession, setSelectedSession] = useState<string | null>(null);
  const [historySegments, setHistorySegments] = useState<TranslationSegment[]>([]);

  useEffect(() => {
    if (tab === "history") {
      api.liveListSessions(20, 0).then(setSessions).catch(() => {});
    }
  }, [tab]);

  useEffect(() => {
    if (selectedSession) {
      api.liveGetSessionSegments(selectedSession).then(setHistorySegments).catch(() => {});
    }
  }, [selectedSession]);

  return (
    <div className="w-80 border-l border-[var(--border-subtle)] flex flex-col bg-[var(--surface-0)]">
      <div className="px-4 py-3 border-b border-[var(--border-subtle)] flex items-center justify-between">
        <div className="flex gap-2">
          <button
            className={`text-xs font-medium px-2 py-1 rounded ${tab === "bookmarks" ? "bg-[var(--brand-500)]/20 text-[var(--brand-400)]" : "text-[var(--text-muted)]"}`}
            onClick={() => { setTab("bookmarks"); setSelectedSession(null); }}
          >
            {t("live.bookmarksTab")}
          </button>
          <button
            className={`text-xs font-medium px-2 py-1 rounded ${tab === "history" ? "bg-[var(--brand-500)]/20 text-[var(--brand-400)]" : "text-[var(--text-muted)]"}`}
            onClick={() => setTab("history")}
          >
            {t("live.historyTab")}
          </button>
        </div>
        <Button variant="ghost" size="sm" onClick={onClose}>×</Button>
      </div>
      <div className="flex-1 overflow-y-auto p-3 space-y-2">
        {tab === "bookmarks" && (
          bookmarkedIdx.size === 0 ? (
            <p className="text-xs text-[var(--text-muted)] text-center py-8">{t("live.bookmarkHint")}</p>
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
          )
        )}
        {tab === "history" && !selectedSession && (
          sessions.length === 0 ? (
            <p className="text-xs text-[var(--text-muted)] text-center py-8">{t("live.noHistory")}</p>
          ) : (
            sessions.map((s) => (
              <div
                key={s.id}
                className="p-2 rounded-lg bg-[var(--surface-1)] text-xs space-y-1 cursor-pointer hover:bg-[var(--surface-2)]"
                onClick={() => setSelectedSession(s.id)}
              >
                <div className="flex items-center justify-between">
                  <span className="text-[var(--text-muted)] font-mono">{s.started_at}</span>
                  <span className={`px-1.5 py-0.5 rounded text-[10px] ${s.status === "active" ? "bg-green-500/20 text-green-400" : "bg-gray-500/20 text-gray-400"}`}>
                    {s.status}
                  </span>
                </div>
                <p className="text-[var(--text-primary)]">{s.source_lang} → {s.target_langs}</p>
              </div>
            ))
          )
        )}
        {tab === "history" && selectedSession && (
          <>
            <div className="flex items-center gap-2 mb-2">
              <button
                className="text-xs text-[var(--brand-400)] hover:underline"
                onClick={() => { setSelectedSession(null); setHistorySegments([]); }}
              >
                {t("live.backToList")}
              </button>
              <div className="flex-1" />
              <button
                className="text-xs text-red-400 hover:underline"
                onClick={async () => {
                  if (confirm(t("live.confirmClearSession"))) {
                    await api.liveClearSessionSegments(selectedSession);
                    setHistorySegments([]);
                    showInfoBar(t("live.clearedSession"), "success");
                  }
                }}
              >
                {t("live.clear")}
              </button>
            </div>
            {historySegments.length === 0 ? (
              <p className="text-xs text-[var(--text-muted)] text-center py-4">{t("live.noSegments")}</p>
            ) : (
              historySegments.map((seg) => (
                <div key={seg.id} className="p-2 rounded-lg bg-[var(--surface-1)] text-xs space-y-1">
                  <div className="flex items-center gap-1">
                    <span className="text-[var(--text-muted)] font-mono text-[10px]">#{seg.sequence}</span>
                    {seg.is_bookmarked && <Bookmark size={10} className="text-amber-400 fill-amber-400" />}
                  </div>
                  <p className="text-[var(--text-primary)]">{seg.original_text}</p>
                  {seg.translated_text && <p className="text-[var(--active-text)]">{seg.translated_text}</p>}
                </div>
              ))
            )}
          </>
        )}
      </div>
    </div>
  );
}
