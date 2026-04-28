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
import { api, type UnlistenFn } from "../lib/api";
import type {
  AudioDeviceInfo, SupportedLanguage, TranslationSession,
  TranslationSegment, SegmentUpdatePayload,
} from "../lib/types";
import { cn } from "../lib/utils";

/* ── Private data types for RealtimeEvent.data ── */
interface TranslatedData {
  source_text?: string;
  translations?: Record<string, string>;
}
interface RecognizingData { text?: string }
interface ErrorData { message?: string }

type ViewMode = "bilingual" | "source-only" | "translation-only";

const SPEAKER_COLORS = [
  "border-l-blue-400", "border-l-emerald-400", "border-l-amber-400",
  "border-l-purple-400", "border-l-rose-400", "border-l-cyan-400",
];
const MAX_RECONNECTS = 3;

export function LiveTranslationView() {
  const { t } = useTranslation();
  const {
    isTranslating, setTranslating,
    recognizedSegments, addSegment, clearSegments,
    config, sessionId, setSessionId, showInfoBar,
  } = useAppStore();

  const [sourceLang, setSourceLang] = useState("auto");
  const [targetLang, setTargetLang] = useState("en");
  const [sourceLangs, setSourceLangs] = useState<SupportedLanguage[]>([]);
  const [targetLangs, setTargetLangs] = useState<SupportedLanguage[]>([]);
  const [currentProvider] = useState("azure_speech");
  const [selectedSpeechEp, setSelectedSpeechEp] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [partialText, setPartialText] = useState("");
  const [viewMode, setViewMode] = useState<ViewMode>("bilingual");
  const [audioLevel, setAudioLevel] = useState(0);
  const [inputDevices, setInputDevices] = useState<AudioDeviceInfo[]>([]);
  const [selectedInputDevice, setSelectedInputDevice] = useState("");
  const [bookmarkedIds, setBookmarkedIds] = useState<Set<string>>(new Set());
  const [bookmarkedIdx, setBookmarkedIdx] = useState<Set<number>>(new Set());
  const [showHistory, setShowHistory] = useState(false);
  const [isSubtitleOpen, setIsSubtitleOpen] = useState(false);
  const [isInsightOpen, setIsInsightOpen] = useState(false);

  const scrollRef = useRef<HTMLDivElement>(null);
  const unlistenRef = useRef<UnlistenFn | null>(null);
  const reconnectTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const reconnectCountRef = useRef(0);
  const intentionalStopRef = useRef(false);
  const isReconnectingRef = useRef(false);
  const lastPartialRef = useRef("");
  const handleToggleRef = useRef<() => void>(() => {});

  // ── 1. Language list ──
  useEffect(() => {
    api.liveListSupportedLanguages(currentProvider).then((list) => {
      if (list?.length) {
        setSourceLangs(list.filter(l => l.kind === "source" || l.kind === "both"));
        setTargetLangs(list.filter(l => l.kind === "target" || l.kind === "both"));
      }
    }).catch(() => {});
  }, [currentProvider]);

  // ── 2. Audio device enumeration ──
  useEffect(() => {
    api.listAudioDevices().then((devices) => {
      const inputs = devices.filter(d => d.device_type === "Input");
      setInputDevices(inputs);
      const def = inputs.find(d => d.is_default);
      if (def) setSelectedInputDevice(def.id);
      else if (inputs.length > 0) setSelectedInputDevice(inputs[0].id);
    }).catch(() => {});
  }, []);

  // ── 3. Speech endpoint selection ──
  const speechEndpoints = (config?.endpoints ?? []).filter(
    (ep) => ep.endpoint_type === "azure_speech" && ep.enabled,
  );
  useEffect(() => {
    if (!selectedSpeechEp && speechEndpoints.length > 0) {
      setSelectedSpeechEp(speechEndpoints[0].id);
    }
  }, [speechEndpoints, selectedSpeechEp]);

  // ── 6. F5 session recovery ──
  useEffect(() => {
    (async () => {
      try {
        const activeSession = await api.liveGetActiveSession();
        if (activeSession) {
          setSessionId(activeSession.id);
          const segments = await api.liveGetRecentSegments(activeSession.id, 200);
          for (const seg of segments) {
            addSegment({
              source: seg.original_text, translation: seg.translated_text || "",
              time: seg.started_at ? (seg.started_at.split("T")[1]?.split(".")[0] || seg.started_at) : "",
            });
            if (seg.is_bookmarked) setBookmarkedIds(prev => new Set(prev).add(seg.id));
          }
          showInfoBar(t("live.sessionRestored", { count: segments.length }), "info");
        }
      } catch { /* first launch — no active session */ }
    })();
  }, []); // eslint-disable-line react-hooks/exhaustive-deps
  // ── 11. Bookmark event sync ──
  useEffect(() => {
    const unlisten = api.onSegmentUpdated((seg: SegmentUpdatePayload) => {
      if (seg.is_bookmarked) {
        setBookmarkedIds(prev => new Set(prev).add(seg.segment_id));
      } else {
        setBookmarkedIds(prev => { const next = new Set(prev); next.delete(seg.segment_id); return next; });
      }
    });
    return () => { unlisten.then(fn => fn()); };
  }, []);
  // ── 15. Floating window state sync ──
  useEffect(() => {
    const unlisten = api.onFloatingWindowStateChanged((e) => {
      if (e.window === "subtitle") setIsSubtitleOpen(e.open);
      if (e.window === "insight") setIsInsightOpen(e.open);
    });
    return () => { unlisten.then(fn => fn()); };
  }, []);
  useEffect(() => { // Auto-scroll
    if (scrollRef.current) scrollRef.current.scrollTop = scrollRef.current.scrollHeight;
  }, [recognizedSegments]);
  useEffect(() => { // Cleanup on unmount
    return () => {
      if (unlistenRef.current) { unlistenRef.current(); unlistenRef.current = null; }
      if (reconnectTimerRef.current) { clearTimeout(reconnectTimerRef.current); reconnectTimerRef.current = null; }
    };
  }, []);

  // ── 10. Audio level indicator ──
  useEffect(() => {
    if (!isTranslating) { setAudioLevel(0); return; }
    if (partialText !== lastPartialRef.current) {
      lastPartialRef.current = partialText;
      setAudioLevel(0.7);
    }
    const decay = setInterval(() => {
      setAudioLevel((prev) => Math.max(0, prev - 0.08));
    }, 150);
    return () => clearInterval(decay);
  }, [isTranslating, partialText]);

  // ── 11. Bookmark toggle ──
  const toggleBookmark = useCallback((idx: number, segmentId?: string) => {
    if (segmentId) {
      const isCurrentlyBookmarked = bookmarkedIds.has(segmentId);
      const promise = isCurrentlyBookmarked
        ? api.liveUnbookmarkSegment(segmentId)
        : api.liveBookmarkSegment(segmentId);
      promise.catch(() => showInfoBar(t("live.bookmarkFailed"), "error"));
      return;
    }
    setBookmarkedIdx((prev) => {
      const next = new Set(prev);
      if (next.has(idx)) next.delete(idx); else next.add(idx);
      return next;
    });
  }, [bookmarkedIds, showInfoBar, t]);

  // ── 13. Copy all ──
  const handleCopyAll = useCallback(async () => {
    const text = recognizedSegments
      .map((s) => `[${s.time}] ${s.source}\n${s.translation}`)
      .join("\n\n");
    try {
      await navigator.clipboard.writeText(text);
      showInfoBar(t("live.copiedAll"), "success");
    } catch { showInfoBar(t("live.copyFailed"), "error"); }
  }, [recognizedSegments, showInfoBar, t]);

  // ── 12. Export TXT ──
  const handleExport = useCallback(() => {
    const text = recognizedSegments
      .map((s) => `[${s.time}]\n${t("live.exportSource")}${s.source}\n${t("live.exportTranslation")}${s.translation}`)
      .join("\n---\n");
    downloadFile(text, `translation_${new Date().toISOString().slice(0, 10)}.txt`, "text/plain;charset=utf-8");
  }, [recognizedSegments, t]);

  // ── 12. Export SRT ──
  const handleExportSrt = useCallback(() => {
    const lines = recognizedSegments.map((s, i) => {
      const start = timeToSrtTimestamp(s.time, 0);
      const end = timeToSrtTimestamp(s.time, 3);
      return `${i + 1}\n${start} --> ${end}\n${s.source}\n${s.translation}`;
    });
    downloadFile(lines.join("\n\n"), `translation_${new Date().toISOString().slice(0, 10)}.srt`, "text/srt;charset=utf-8");
  }, [recognizedSegments]);

  // ── 12. Export VTT ──
  const handleExportVtt = useCallback(() => {
    const lines = recognizedSegments.map((s) => {
      const start = timeToVttTimestamp(s.time, 0);
      const end = timeToVttTimestamp(s.time, 3);
      return `${start} --> ${end}\n${s.source}\n${s.translation}`;
    });
    downloadFile("WEBVTT\n\n" + lines.join("\n\n"), `translation_${new Date().toISOString().slice(0, 10)}.vtt`, "text/vtt;charset=utf-8");
  }, [recognizedSegments]);
  // ── 4. Start/Stop toggle + 5. Auto-reconnect ──
  const tryReconnect = useCallback((msgKey: string) => {
    if (intentionalStopRef.current || reconnectCountRef.current >= MAX_RECONNECTS || isReconnectingRef.current) return false;
    isReconnectingRef.current = true;
    reconnectCountRef.current++;
    const delay = Math.min(2000 * reconnectCountRef.current, 10000);
    setError(t(msgKey, { seconds: delay / 1000, count: reconnectCountRef.current, max: MAX_RECONNECTS }));
    reconnectTimerRef.current = setTimeout(() => {
      reconnectTimerRef.current = null;
      if (unlistenRef.current) { unlistenRef.current(); unlistenRef.current = null; }
      setTranslating(false);
      setSessionId(null);
      isReconnectingRef.current = false;
      handleToggleRef.current();
    }, delay);
    return true;
  }, [t, setTranslating, setSessionId]);
  const handleToggle = useCallback(async () => {
    if (isTranslating) {
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
              const data = event.data as TranslatedData;
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
              const data = event.data as RecognizingData;
              setPartialText(data.text || "");
              break;
            }
            case "Error": {
              const msg = (event.data as ErrorData).message || t("live.unknownError");
              setError(msg);
              tryReconnect("live.reconnecting");
              break;
            }
            case "SessionStopped": {
              if (!tryReconnect("live.sessionDisconnected")) {
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
  }, [isTranslating, sessionId, selectedSpeechEp, sourceLang, targetLang, addSegment, setTranslating, setSessionId, t, tryReconnect]);
  handleToggleRef.current = handleToggle;

  // ── 7. F6 stop shortcut ──
  useEffect(() => {
    const onStop = () => { if (isTranslating) handleToggle(); };
    window.addEventListener("tfp:stop-translation", onStop);
    return () => window.removeEventListener("tfp:stop-translation", onStop);
  }, [isTranslating, handleToggle]);

  // ── 8+17. Segment merge + speaker coloring + max_history truncation ──
  type MergedSeg = {
    source: string; translation: string; time: string;
    endTime: string; speakerIdx: number; originalIndices: number[];
  };
  const maxHistory = config?.recognition?.max_history_items ?? 500;
  const visibleSegments = recognizedSegments.slice(-maxHistory);
  const merged: MergedSeg[] = [];
  let currentSpeaker = 0;
  for (let i = 0; i < visibleSegments.length; i++) {
    const seg = visibleSegments[i];
    const prev = merged[merged.length - 1];
    const gap = prev ? timeGapSeconds(prev.endTime, seg.time) : 999;
    if (gap > 5) currentSpeaker = (currentSpeaker + 1) % SPEAKER_COLORS.length;
    if (prev && gap < 3 && prev.speakerIdx === currentSpeaker) {
      prev.source += " " + seg.source;
      prev.translation += " " + seg.translation;
      prev.endTime = seg.time;
      prev.originalIndices.push(i);
    } else {
      merged.push({
        source: seg.source, translation: seg.translation,
        time: seg.time, endTime: seg.time,
        speakerIdx: currentSpeaker, originalIndices: [i],
      });
    }
  }

  return (
    <div className="flex h-full">
      <div className="flex flex-col flex-1">
        {/* Toolbar */}
        <div className="flex items-center gap-3 px-6 py-3 border-b border-[var(--border-subtle)]"
          style={{ backgroundColor: "var(--toolbar-bg)" }}>
          <h1 className="text-base font-semibold text-[var(--text-primary)] mr-4">{t("live.title")}</h1>

          {/* Speech endpoint + audio level */}
          <div className="flex items-center gap-2">
            {speechEndpoints.length > 0 ? (
              <Select value={selectedSpeechEp} onChange={(e) => setSelectedSpeechEp(e.target.value)} className="w-32">
                {speechEndpoints.map((ep) => <option key={ep.id} value={ep.id}>🎤 {ep.name}</option>)}
              </Select>
            ) : (
              <Badge variant="red" className="text-[10px]">{t("live.noSpeechBadge")}</Badge>
            )}

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

          {/* View mode + action buttons */}
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

        {/* Translation results */}
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
          )}

          {/* Realtime partial recognition */}
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

        {/* Bottom controls */}
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

      {/* History panel */}
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

/* ── Utility functions ── */

function parseTimeToSec(t: string): number {
  const parts = t.split(":").map(Number);
  if (parts.length === 3) return parts[0] * 3600 + parts[1] * 60 + parts[2];
  if (parts.length === 2) return parts[0] * 60 + parts[1];
  return 0;
}

function timeGapSeconds(t1: string, t2: string): number {
  return Math.abs(parseTimeToSec(t2) - parseTimeToSec(t1));
}

function timeToSrtTimestamp(timeStr: string, offsetSec: number): string {
  const totalSec = parseTimeToSec(timeStr) + offsetSec;
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
  a.href = url; a.download = fileName; a.click();
  URL.revokeObjectURL(url);
}

/* ── HistoryPanel private component ── */

function HistoryPanel({ recognizedSegments, bookmarkedIdx, onClose, showInfoBar }: {
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
