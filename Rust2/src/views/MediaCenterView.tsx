import { useState, useCallback, useRef, useEffect, useMemo } from "react";
import {
  Image, Video, Plus, Loader2, Download, Trash2,
  Maximize2, ChevronLeft, ChevronRight, Sparkles,
  RefreshCw, X, Search, Check, Copy,
  Undo2, Redo2, Play,
} from "lucide-react";
import { cn } from "../lib/utils";
import {
  Button, Label, Select, Textarea, Badge,
  EmptyState, ScrollArea,
} from "../components/ui";
import { useAppStore } from "../stores/app-store";
import {
  api, type CenterWorkspace, type CenterWorkspaceBundle,
  type CenterAssetDetail, type CenterTaskEvent,
} from "../lib/tauri-api";
import { open as dialogOpen } from "@tauri-apps/plugin-dialog";
import { convertFileSrc } from "@tauri-apps/api/core";

const SIZE_OPTIONS = [
  { label: "1024x1024", w: 1024, h: 1024 },
  { label: "1024x1792", w: 1024, h: 1792 },
  { label: "1792x1024", w: 1792, h: 1024 },
  { label: "Custom", w: 0, h: 0 },
];
const QUALITY_OPTIONS = ["auto", "low", "medium", "high"];
const FORMAT_OPTIONS = ["png", "jpeg", "webp"];
const BG_OPTIONS = ["auto", "opaque", "transparent"];
const FIDELITY_OPTIONS = ["auto", "low", "high"];
const COUNT_OPTIONS = [1, 2, 3, 4, 5];
const ASPECT_RATIOS = ["1:1", "16:9", "9:16"];
const RESOLUTIONS = ["480p", "720p", "1080p"];
const DURATIONS = [5, 10, 15, 20];

type CanvasMode = "canvas_image" | "canvas_video";
type FilterType = "all" | "canvas_image" | "canvas_video";
type FilterTime = "all" | "today" | "7days" | "30days";

interface UndoEntry {
  type: "generate" | "delete_assets" | "change_prompt" | "change_params";
  roundId?: string;
  assetIds?: string[];
}

const MAX_LOADED = 3;

export function MediaCenterView() {
  const config = useAppStore((s) => s.config);

  const [workspaces, setWorkspaces] = useState<CenterWorkspace[]>([]);
  const [openTabs, setOpenTabs] = useState<string[]>([]);
  const [activeTabId, setActiveTabId] = useState<string | null>(null);
  const [loadedBundles, setLoadedBundles] = useState<Map<string, CenterWorkspaceBundle>>(new Map());

  const [searchQuery, setSearchQuery] = useState("");
  const [filterType, setFilterType] = useState<FilterType>("all");
  const [filterTime, setFilterTime] = useState<FilterTime>("all");

  const [prompt, setPrompt] = useState("");
  const [sizeIdx, setSizeIdx] = useState(0);
  const [quality, setQuality] = useState("auto");
  const [format, setFormat] = useState("png");
  const [count, setCount] = useState(1);
  const [background, setBackground] = useState("auto");
  const [fidelity, setFidelity] = useState("auto");
  const [aspectRatio, setAspectRatio] = useState("16:9");
  const [resolution, setResolution] = useState("720p");
  const [duration, setDuration] = useState(5);
  const [videoCount, setVideoCount] = useState(1);

  const [selectedAssets, setSelectedAssets] = useState<Set<string>>(new Set());
  const [undoStacks, setUndoStacks] = useState<Map<string, UndoEntry[]>>(new Map());
  const [redoStacks, setRedoStacks] = useState<Map<string, UndoEntry[]>>(new Map());
  const [contextMenu, setContextMenu] = useState<{ x: number; y: number; assetId?: string } | null>(null);
  const [previewAsset, setPreviewAsset] = useState<CenterAssetDetail | null>(null);
  const [videoRefWarning, setVideoRefWarning] = useState("");

  const canvasRef = useRef<HTMLDivElement>(null);

  const activeBundle = activeTabId ? loadedBundles.get(activeTabId) : undefined;
  const activeWs = activeBundle?.workspace;
  const currentMode: CanvasMode = (activeWs?.session_type as CanvasMode) || "canvas_image";

  const activeRoundId = activeWs?.current_round_id || activeBundle?.rounds[activeBundle.rounds.length - 1]?.id;
  const activeRound = activeBundle?.rounds.find((r) => r.id === activeRoundId);
  const activeRoundIdx = activeBundle?.rounds.findIndex((r) => r.id === activeRoundId) ?? -1;
  const totalRounds = activeBundle?.rounds.length ?? 0;
  const currentAssets = activeBundle?.current_round_assets ?? [];

  const imageEndpoint = config?.endpoints.find(
    (ep) => ep.enabled && ep.endpoint_type !== "azure_speech" && ep.models.some((m) => m.capabilities.includes("image")),
  );
  const imageModelId = imageEndpoint?.models.find((m) => m.capabilities.includes("image"))?.model_id || "gpt-image-2";

  useEffect(() => {
    api.centerListWorkspaces(50, 0).then(setWorkspaces).catch(console.error);
  }, []);

  useEffect(() => {
    let unlisten: (() => void) | undefined;
    api.onCenterTaskUpdate((ev: CenterTaskEvent) => {
      if (ev.status === "completed" || ev.status === "failed") {
        if (ev.session_id && loadedBundles.has(ev.session_id)) {
          api.centerGetWorkspaceBundle(ev.session_id).then((bundle) => {
            setLoadedBundles((prev) => new Map(prev).set(ev.session_id, bundle));
          });
        }
        api.centerListWorkspaces(50, 0).then(setWorkspaces).catch(console.error);
      }
    }).then((fn) => { unlisten = fn; });
    return () => { unlisten?.(); };
  }, []);

  const loadWorkspace = useCallback(async (id: string) => {
    if (loadedBundles.has(id)) return;
    const bundle = await api.centerGetWorkspaceBundle(id);
    setLoadedBundles((prev) => {
      const next = new Map(prev);
      next.set(id, bundle);
      if (next.size > MAX_LOADED) {
        for (const k of [...next.keys()]) {
          if (k !== id && next.size > MAX_LOADED) next.delete(k);
        }
      }
      return next;
    });
  }, [loadedBundles]);

  const openTab = useCallback(async (id: string) => {
    if (!openTabs.includes(id)) setOpenTabs((prev) => [...prev, id]);
    setActiveTabId(id);
    await loadWorkspace(id);
    setSelectedAssets(new Set());
  }, [openTabs, loadWorkspace]);

  const closeTab = useCallback((id: string) => {
    setOpenTabs((prev) => prev.filter((t) => t !== id));
    if (activeTabId === id) {
      const remaining = openTabs.filter((t) => t !== id);
      setActiveTabId(remaining.length > 0 ? remaining[remaining.length - 1] : null);
    }
  }, [openTabs, activeTabId]);

  const createWorkspace = useCallback(async (mode: CanvasMode = "canvas_image") => {
    const name = mode === "canvas_image" ? `Image Canvas ${workspaces.length + 1}` : `Video Canvas ${workspaces.length + 1}`;
    const ws = await api.centerCreateWorkspace(mode, name);
    setWorkspaces((prev) => [ws, ...prev]);
    await openTab(ws.id);
  }, [workspaces.length, openTab]);

  const deleteWorkspace = useCallback(async (id: string) => {
    await api.centerSoftDeleteWorkspace(id);
    setWorkspaces((prev) => prev.filter((w) => w.id !== id));
    closeTab(id);
  }, [closeTab]);

  const switchRound = useCallback(async (direction: "prev" | "next") => {
    if (!activeTabId || !activeBundle) return;
    const rounds = activeBundle.rounds;
    const curIdx = rounds.findIndex((r) => r.id === activeRoundId);
    const newIdx = direction === "prev" ? curIdx - 1 : curIdx + 1;
    if (newIdx < 0 || newIdx >= rounds.length) return;
    await api.centerSetActiveRound(activeTabId, rounds[newIdx].id);
    const bundle = await api.centerGetWorkspaceBundle(activeTabId);
    setLoadedBundles((prev) => new Map(prev).set(activeTabId!, bundle));
    setSelectedAssets(new Set());
  }, [activeTabId, activeBundle, activeRoundId]);

  const handleImageGenerate = useCallback(async () => {
    if (!prompt.trim() || !imageEndpoint || !activeTabId) return;
    const size = SIZE_OPTIONS[sizeIdx];
    await api.centerStartImageRound({
      workspaceId: activeTabId,
      prompt: prompt.trim(),
      params: {
        endpoint_id: imageEndpoint.id, model: imageModelId,
        width: size.w || 1024, height: size.h || 1024,
        quality, n: count, output_format: format,
        background: background !== "auto" ? background : undefined,
      },
      referencePaths: [],
    });
    pushUndo(activeTabId, { type: "generate" });
    setPrompt("");
    setTimeout(async () => {
      const bundle = await api.centerGetWorkspaceBundle(activeTabId!);
      setLoadedBundles((prev) => new Map(prev).set(activeTabId!, bundle));
      api.centerListWorkspaces(50, 0).then(setWorkspaces);
    }, 500);
  }, [prompt, imageEndpoint, activeTabId, sizeIdx, quality, count, format, background, imageModelId]);

  const handleVideoGenerate = useCallback(async () => {
    if (!prompt.trim() || !activeTabId) return;
    const videoEndpoint = config?.endpoints.find(
      (ep) => ep.enabled && ep.endpoint_type !== "azure_speech" && ep.models.some((m) => m.capabilities.includes("video")),
    );
    if (!videoEndpoint) return;
    const videoModelId = videoEndpoint.models.find((m) => m.capabilities.includes("video"))?.model_id || "sora-2";
    const resMap: Record<string, number> = { "480p": 480, "720p": 720, "1080p": 1080 };
    const resH = resMap[resolution] || 720;
    let sizeStr: string;
    if (aspectRatio === "16:9") sizeStr = `${Math.round(resH * 16 / 9)}x${resH}`;
    else if (aspectRatio === "9:16") sizeStr = `${resH}x${Math.round(resH * 16 / 9)}`;
    else sizeStr = `${resH}x${resH}`;
    await api.centerStartVideoRound({
      workspaceId: activeTabId,
      prompt: prompt.trim(),
      params: { endpoint_id: videoEndpoint.id, model: videoModelId, size: sizeStr, duration_seconds: duration, n: videoCount },
    });
    pushUndo(activeTabId, { type: "generate" });
    setPrompt("");
  }, [prompt, activeTabId, config, aspectRatio, resolution, duration, videoCount]);

  const toggleSelect = useCallback((assetId: string) => {
    setSelectedAssets((prev) => { const n = new Set(prev); if (n.has(assetId)) n.delete(assetId); else n.add(assetId); return n; });
  }, []);
  const selectAll = useCallback(() => { setSelectedAssets(new Set(currentAssets.map((a) => a.asset_id))); }, [currentAssets]);
  const deselectAll = useCallback(() => { setSelectedAssets(new Set()); }, []);

  const handleExport = useCallback(async () => {
    if (selectedAssets.size === 0) return;
    const dir = await dialogOpen({ directory: true });
    if (!dir) return;
    const result = await api.centerExportAssets([...selectedAssets], dir as string);
    alert(`Exported ${result.copied} file(s)${result.failed > 0 ? `, failed ${result.failed}` : ""}`);
  }, [selectedAssets]);

  const handleDeleteSelected = useCallback(async () => {
    if (selectedAssets.size === 0) return;
    if (!confirm(`Delete ${selectedAssets.size} asset(s)?`)) return;
    const ids = [...selectedAssets];
    if (activeTabId) pushUndo(activeTabId, { type: "delete_assets", assetIds: ids });
    await api.centerDeleteAssets(ids);
    setSelectedAssets(new Set());
    if (activeTabId) { const b = await api.centerGetWorkspaceBundle(activeTabId); setLoadedBundles((prev) => new Map(prev).set(activeTabId!, b)); }
  }, [selectedAssets, activeTabId]);

  const pushUndo = (wsId: string, entry: UndoEntry) => {
    setUndoStacks((prev) => { const n = new Map(prev); n.set(wsId, [...(n.get(wsId) || []), entry]); return n; });
    setRedoStacks((prev) => { const n = new Map(prev); n.set(wsId, []); return n; });
  };

  const handleUndo = useCallback(async () => {
    if (!activeTabId) return;
    const stack = undoStacks.get(activeTabId) || [];
    if (stack.length === 0) return;
    const entry = stack[stack.length - 1];
    setUndoStacks((prev) => { const n = new Map(prev); n.set(activeTabId!, stack.slice(0, -1)); return n; });
    setRedoStacks((prev) => { const n = new Map(prev); n.set(activeTabId!, [...(n.get(activeTabId!) || []), entry]); return n; });
    const b = await api.centerGetWorkspaceBundle(activeTabId);
    setLoadedBundles((prev) => new Map(prev).set(activeTabId!, b));
  }, [activeTabId, undoStacks]);

  const handleRedo = useCallback(async () => {
    if (!activeTabId) return;
    const stack = redoStacks.get(activeTabId) || [];
    if (stack.length === 0) return;
    const entry = stack[stack.length - 1];
    setRedoStacks((prev) => { const n = new Map(prev); n.set(activeTabId!, stack.slice(0, -1)); return n; });
    setUndoStacks((prev) => { const n = new Map(prev); n.set(activeTabId!, [...(n.get(activeTabId!) || []), entry]); return n; });
    const b = await api.centerGetWorkspaceBundle(activeTabId);
    setLoadedBundles((prev) => new Map(prev).set(activeTabId!, b));
  }, [activeTabId, redoStacks]);

  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      const target = e.target as HTMLElement;
      if (target.tagName === "INPUT" || target.tagName === "TEXTAREA" || target.isContentEditable) return;
      if (e.key === "Delete") { e.preventDefault(); handleDeleteSelected(); }
      else if (e.ctrlKey && e.key === "z") { e.preventDefault(); handleUndo(); }
      else if (e.ctrlKey && e.key === "y") { e.preventDefault(); handleRedo(); }
      else if (e.ctrlKey && e.key === "a") { e.preventDefault(); selectAll(); }
      else if (e.ctrlKey && e.key === "d") { e.preventDefault(); deselectAll(); }
      else if (e.key === "Enter") { e.preventDefault(); if (currentMode === "canvas_image") handleImageGenerate(); else handleVideoGenerate(); }
    };
    const el = canvasRef.current;
    if (el) el.addEventListener("keydown", handler);
    return () => { if (el) el.removeEventListener("keydown", handler); };
  }, [handleDeleteSelected, handleUndo, handleRedo, selectAll, deselectAll, handleImageGenerate, handleVideoGenerate, currentMode]);

  useEffect(() => { const h = () => setContextMenu(null); window.addEventListener("click", h); return () => window.removeEventListener("click", h); }, []);

  const promoteToReference = useCallback(async (assetId: string) => {
    if (!activeTabId) return;
    const asset = currentAssets.find((a) => a.asset_id === assetId);
    if (!asset) return;
    await api.studioAddReferenceImage(activeTabId, asset.file_path);
    const b = await api.centerGetWorkspaceBundle(activeTabId);
    setLoadedBundles((prev) => new Map(prev).set(activeTabId!, b));
  }, [activeTabId, currentAssets]);

  const filteredWorkspaces = useMemo(() => {
    let list = workspaces;
    if (searchQuery) { const q = searchQuery.toLowerCase(); list = list.filter((w) => w.name.toLowerCase().includes(q)); }
    if (filterType !== "all") list = list.filter((w) => w.session_type === filterType);
    if (filterTime !== "all") {
      const now = Date.now();
      const cutoff = filterTime === "today" ? 86400000 : filterTime === "7days" ? 604800000 : 2592000000;
      list = list.filter((w) => now - new Date(w.created_at).getTime() < cutoff);
    }
    return list;
  }, [workspaces, searchQuery, filterType, filterTime]);

  return (
    <div className="flex h-full" ref={canvasRef} tabIndex={0}>
      {/* Left sidebar */}
      <div className="w-[280px] border-r border-[var(--border-subtle)] flex flex-col shrink-0" style={{ backgroundColor: "var(--sidebar-bg)" }}>
        <div className="p-3 border-b border-[var(--border-subtle)] space-y-2">
          <div className="flex gap-2">
            <div className="relative flex-1">
              <Search size={14} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-[var(--text-muted)]" />
              <input type="text" placeholder="Search..." value={searchQuery} onChange={(e) => setSearchQuery(e.target.value)}
                className="w-full pl-8 pr-2 py-1.5 text-xs rounded-md border border-[var(--border-subtle)] bg-[var(--surface-1)] text-[var(--text-primary)] focus:outline-none focus:ring-1 focus:ring-brand-500/50" />
            </div>
            <button onClick={() => createWorkspace("canvas_image")} className="p-1.5 rounded-md hover:bg-[var(--hover-bg)] text-[var(--text-muted)]" title="New image canvas"><Plus size={16} /></button>
          </div>
          <div className="flex gap-1 text-[10px]">
            {(["all", "canvas_image", "canvas_video"] as FilterType[]).map((ft) => (
              <button key={ft} onClick={() => setFilterType(ft)} className={cn("px-2 py-0.5 rounded", filterType === ft ? "bg-brand-600/15 text-[var(--active-text)]" : "text-[var(--text-muted)] hover:bg-[var(--hover-bg)]")}>
                {ft === "all" ? "All" : ft === "canvas_image" ? "Image" : "Video"}
              </button>
            ))}
            <span className="mx-1 text-[var(--border-medium)]">|</span>
            {(["all", "today", "7days", "30days"] as FilterTime[]).map((ft) => (
              <button key={ft} onClick={() => setFilterTime(ft)} className={cn("px-2 py-0.5 rounded", filterTime === ft ? "bg-brand-600/15 text-[var(--active-text)]" : "text-[var(--text-muted)] hover:bg-[var(--hover-bg)]")}>
                {ft === "all" ? "All" : ft === "today" ? "Today" : ft === "7days" ? "7d" : "30d"}
              </button>
            ))}
          </div>
        </div>
        <ScrollArea className="flex-1">
          <div className="p-2 space-y-1">
            {filteredWorkspaces.length === 0 && (
              <div className="p-6 text-center">
                <p className="text-xs text-[var(--text-muted)]">No workspaces</p>
                <div className="flex flex-col gap-2 mt-4">
                  <Button size="sm" onClick={() => createWorkspace("canvas_image")}><Image size={12} /> Image Canvas</Button>
                  <Button size="sm" variant="secondary" onClick={() => createWorkspace("canvas_video")}><Video size={12} /> Video Canvas</Button>
                </div>
              </div>
            )}
            {filteredWorkspaces.map((ws) => (
              <button key={ws.id} onClick={() => openTab(ws.id)}
                className={cn("w-full flex items-center gap-2 px-3 py-2 rounded-lg text-left transition-all group", activeTabId === ws.id ? "bg-brand-600/10 text-[var(--active-text)]" : "text-[var(--text-secondary)] hover:bg-[var(--hover-bg)]")}>
                <Badge variant={ws.session_type === "canvas_image" ? "blue" : "amber"} className="text-[9px] px-1.5 py-0 shrink-0">{ws.session_type === "canvas_image" ? "Img" : "Vid"}</Badge>
                <span className="text-xs truncate flex-1">{ws.name}</span>
                {ws.has_running_task && <div className="w-2 h-2 rounded-full bg-blue-500 animate-pulse shrink-0" />}
                <button onClick={(e) => { e.stopPropagation(); deleteWorkspace(ws.id); }} className="opacity-0 group-hover:opacity-100 p-0.5 hover:text-red-400 transition-opacity"><X size={12} /></button>
              </button>
            ))}
          </div>
        </ScrollArea>
      </div>

      {/* Main area */}
      <div className="flex-1 flex flex-col min-w-0">
        {openTabs.length > 0 && (
          <div className="flex items-center border-b border-[var(--border-subtle)] px-2 h-9 shrink-0 overflow-x-auto" style={{ backgroundColor: "var(--toolbar-bg)" }}>
            {openTabs.map((tabId) => {
              const ws = workspaces.find((w) => w.id === tabId);
              return (
                <div key={tabId} onClick={() => openTab(tabId)}
                  className={cn("flex items-center gap-1.5 px-3 py-1 text-xs cursor-pointer border-b-2 whitespace-nowrap", activeTabId === tabId ? "border-brand-500 text-[var(--active-text)]" : "border-transparent text-[var(--text-muted)] hover:text-[var(--text-secondary)]")}>
                  <Badge variant={ws?.session_type === "canvas_image" ? "blue" : "amber"} className="text-[8px] px-1 py-0">{ws?.session_type === "canvas_image" ? "Img" : "Vid"}</Badge>
                  <span className="max-w-[120px] truncate">{ws?.name || tabId.slice(0, 8)}</span>
                  <button onClick={(e) => { e.stopPropagation(); closeTab(tabId); }} className="ml-1 hover:text-red-400"><X size={10} /></button>
                </div>
              );
            })}
          </div>
        )}

        {activeBundle ? (
          <>
            <div className="flex items-center justify-between px-4 h-10 border-b border-[var(--border-subtle)] shrink-0" style={{ backgroundColor: "var(--toolbar-bg)" }}>
              <div className="flex items-center gap-2">
                {totalRounds > 0 && (
                  <div className="flex items-center gap-1 text-xs">
                    <button onClick={() => switchRound("prev")} disabled={activeRoundIdx <= 0} className="p-1 rounded hover:bg-[var(--hover-bg)] disabled:opacity-30"><ChevronLeft size={14} /></button>
                    <span className="text-[var(--text-secondary)]">Round {activeRoundIdx + 1}/{totalRounds}</span>
                    <button onClick={() => switchRound("next")} disabled={activeRoundIdx >= totalRounds - 1} className="p-1 rounded hover:bg-[var(--hover-bg)] disabled:opacity-30"><ChevronRight size={14} /></button>
                  </div>
                )}
                {activeRound && (
                  <button onClick={() => setPrompt(activeRound.prompt)} className="text-[10px] px-2 py-0.5 rounded bg-[var(--surface-1)] text-[var(--text-muted)] hover:bg-[var(--hover-bg)]">
                    <RefreshCw size={10} className="inline mr-1" />Reuse
                  </button>
                )}
              </div>
              <div className="flex items-center gap-1">
                {selectedAssets.size > 0 && (
                  <div className="flex items-center gap-2 mr-2 text-xs text-[var(--text-secondary)]">
                    <span>{selectedAssets.size} selected</span>
                    <button onClick={handleExport} className="px-2 py-0.5 rounded bg-brand-600/15 text-brand-500 text-[10px]"><Download size={10} className="inline mr-0.5" />Export</button>
                    <button onClick={handleDeleteSelected} className="px-2 py-0.5 rounded bg-red-500/10 text-red-400 text-[10px]"><Trash2 size={10} className="inline mr-0.5" />Delete</button>
                    <button onClick={deselectAll} className="px-1 py-0.5 text-[var(--text-muted)]"><X size={12} /></button>
                  </div>
                )}
                <button onClick={handleUndo} className="p-1.5 rounded hover:bg-[var(--hover-bg)] text-[var(--text-muted)]" title="Undo (Ctrl+Z)"><Undo2 size={14} /></button>
                <button onClick={handleRedo} className="p-1.5 rounded hover:bg-[var(--hover-bg)] text-[var(--text-muted)]" title="Redo (Ctrl+Y)"><Redo2 size={14} /></button>
              </div>
            </div>

            <div className="flex-1 flex min-h-0">
              {/* Params panel */}
              <div className="w-[240px] border-r border-[var(--border-subtle)] overflow-y-auto p-3 space-y-3 shrink-0" style={{ backgroundColor: "var(--surface-0)" }}>
                {currentMode === "canvas_image" ? (
                  <>
                    <div><Label className="text-[10px]">Model</Label><p className="text-[10px] text-[var(--text-secondary)] mt-0.5 truncate">{imageEndpoint ? `${imageEndpoint.name} / ${imageModelId}` : "Not configured"}</p></div>
                    <div><Label className="text-[10px]">Size</Label><Select className="w-full mt-0.5 text-xs" value={sizeIdx.toString()} onChange={(e) => setSizeIdx(Number(e.target.value))}>{SIZE_OPTIONS.map((s, i) => <option key={i} value={i}>{s.label}</option>)}</Select></div>
                    <div><Label className="text-[10px]">Quality</Label><Select className="w-full mt-0.5 text-xs" value={quality} onChange={(e) => setQuality(e.target.value)}>{QUALITY_OPTIONS.map((q) => <option key={q} value={q}>{q}</option>)}</Select></div>
                    <div><Label className="text-[10px]">Format</Label><Select className="w-full mt-0.5 text-xs" value={format} onChange={(e) => setFormat(e.target.value)}>{FORMAT_OPTIONS.map((f) => <option key={f} value={f}>{f}</option>)}</Select></div>
                    <div><Label className="text-[10px]">Count</Label><Select className="w-full mt-0.5 text-xs" value={count.toString()} onChange={(e) => setCount(Number(e.target.value))}>{COUNT_OPTIONS.map((n) => <option key={n} value={n}>{n}</option>)}</Select></div>
                    <div><Label className="text-[10px]">Background</Label><Select className="w-full mt-0.5 text-xs" value={background} onChange={(e) => setBackground(e.target.value)}>{BG_OPTIONS.map((b) => <option key={b} value={b}>{b}</option>)}</Select></div>
                    <div><Label className="text-[10px]">Fidelity</Label><Select className="w-full mt-0.5 text-xs" value={fidelity} onChange={(e) => setFidelity(e.target.value)}>{FIDELITY_OPTIONS.map((f) => <option key={f} value={f}>{f}</option>)}</Select></div>
                  </>
                ) : (
                  <>
                    <div><Label className="text-[10px]">Aspect</Label><Select className="w-full mt-0.5 text-xs" value={aspectRatio} onChange={(e) => setAspectRatio(e.target.value)}>{ASPECT_RATIOS.map((a) => <option key={a} value={a}>{a}</option>)}</Select></div>
                    <div><Label className="text-[10px]">Resolution</Label><Select className="w-full mt-0.5 text-xs" value={resolution} onChange={(e) => setResolution(e.target.value)}>{RESOLUTIONS.map((r) => <option key={r} value={r}>{r}</option>)}</Select></div>
                    <div><Label className="text-[10px]">Duration</Label><Select className="w-full mt-0.5 text-xs" value={duration.toString()} onChange={(e) => setDuration(Number(e.target.value))}>{DURATIONS.map((d) => <option key={d} value={d}>{d}s</option>)}</Select></div>
                    <div><Label className="text-[10px]">Count</Label><Select className="w-full mt-0.5 text-xs" value={videoCount.toString()} onChange={(e) => setVideoCount(Number(e.target.value))}>{[1, 2, 3, 4].map((n) => <option key={n} value={n}>{n}</option>)}</Select></div>
                    {videoRefWarning && <p className="text-[10px] text-red-400">{videoRefWarning}</p>}
                  </>
                )}
                <div>
                  <Label className="text-[10px]">References</Label>
                  <div className="flex gap-1.5 mt-1 flex-wrap">
                    {activeBundle.reference_images.map((img) => (
                      <div key={img.id} className="relative w-10 h-10 rounded border border-[var(--border-subtle)] overflow-hidden group">
                        <img src={convertFileSrc(img.file_path)} alt="" className="w-full h-full object-cover" />
                        <button className="absolute inset-0 bg-black/50 flex items-center justify-center opacity-0 group-hover:opacity-100 transition-opacity"
                          onClick={async () => { await api.studioDeleteReferenceImage(img.id); const b = await api.centerGetWorkspaceBundle(activeTabId!); setLoadedBundles((prev) => new Map(prev).set(activeTabId!, b)); }}>
                          <X size={10} className="text-white" />
                        </button>
                      </div>
                    ))}
                    <button className="w-10 h-10 rounded border border-dashed border-[var(--border-medium)] flex items-center justify-center text-[var(--text-muted)] hover:border-brand-500/50"
                      onClick={async () => {
                        if (!activeTabId) return;
                        if (currentMode === "canvas_video" && activeBundle.reference_images.length >= 1) { setVideoRefWarning("Sora only supports 1 reference"); return; }
                        setVideoRefWarning("");
                        const selected = await dialogOpen({ multiple: currentMode === "canvas_image", filters: [{ name: "Images", extensions: ["png", "jpg", "jpeg", "webp", "gif", "bmp"] }] });
                        if (!selected) return;
                        const paths = Array.isArray(selected) ? selected : [selected];
                        for (const p of paths) await api.studioAddReferenceImage(activeTabId, p);
                        const b = await api.centerGetWorkspaceBundle(activeTabId);
                        setLoadedBundles((prev) => new Map(prev).set(activeTabId!, b));
                      }}><Plus size={14} /></button>
                  </div>
                </div>
              </div>

              {/* Result grid */}
              <div className="flex-1 flex flex-col min-w-0">
                <ScrollArea className="flex-1">
                  <div className="p-4">
                    {currentAssets.length > 0 ? (
                      <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-4 gap-3">
                        {currentAssets.map((asset) => (
                          <div key={asset.id}
                            className={cn("relative rounded-lg border overflow-hidden group cursor-pointer transition-all", selectedAssets.has(asset.asset_id) ? "border-brand-500 ring-2 ring-brand-500/30" : "border-[var(--border-subtle)] hover:border-[var(--border-medium)]")}
                            onClick={() => toggleSelect(asset.asset_id)}
                            onDoubleClick={() => setPreviewAsset(asset)}
                            onContextMenu={(e) => { e.preventDefault(); setContextMenu({ x: e.clientX, y: e.clientY, assetId: asset.asset_id }); }}>
                            <div className="aspect-square bg-[var(--surface-2)]">
                              {asset.kind === "image" ? (
                                <img src={convertFileSrc(asset.file_path)} alt="" className="w-full h-full object-cover" loading="lazy" />
                              ) : (
                                <div className="w-full h-full flex items-center justify-center relative">
                                  {asset.preview_path ? <img src={convertFileSrc(asset.preview_path)} alt="" className="w-full h-full object-cover" loading="lazy" /> : <Video size={24} className="text-[var(--text-muted)]" />}
                                  <div className="absolute inset-0 flex items-center justify-center"><Play size={32} className="text-white drop-shadow-lg" /></div>
                                </div>
                              )}
                            </div>
                            <div className={cn("absolute top-2 left-2 w-5 h-5 rounded border flex items-center justify-center transition-all", selectedAssets.has(asset.asset_id) ? "bg-brand-500 border-brand-500 text-white" : "border-white/60 bg-black/30 opacity-0 group-hover:opacity-100")}>
                              {selectedAssets.has(asset.asset_id) && <Check size={12} />}
                            </div>
                            <div className="absolute top-2 right-2 flex gap-1 opacity-0 group-hover:opacity-100 transition-opacity">
                              <button onClick={(e) => { e.stopPropagation(); setPreviewAsset(asset); }} className="p-1 rounded bg-black/50 text-white hover:bg-black/70"><Maximize2 size={10} /></button>
                              <button onClick={async (e) => { e.stopPropagation(); const dir = await dialogOpen({ directory: true }); if (dir) await api.centerExportAssets([asset.asset_id], dir as string); }} className="p-1 rounded bg-black/50 text-white hover:bg-black/70"><Download size={10} /></button>
                            </div>
                          </div>
                        ))}
                      </div>
                    ) : activeBundle.running_tasks.length > 0 ? (
                      <div className="flex flex-col items-center justify-center h-48 gap-3"><Loader2 size={32} className="text-brand-400 animate-spin" /><p className="text-sm text-[var(--text-muted)]">Generating...</p></div>
                    ) : (
                      <EmptyState icon={<Image size={48} />} title="Enter a prompt to start" />
                    )}
                  </div>
                </ScrollArea>
                <div className="border-t border-[var(--border-subtle)] p-3" style={{ backgroundColor: "var(--toolbar-bg)" }}>
                  <div className="flex gap-2 max-w-3xl mx-auto">
                    <Textarea value={prompt} onChange={(e) => setPrompt(e.target.value)} placeholder="Enter prompt..." className="flex-1 min-h-[36px] max-h-[100px] text-sm"
                      onKeyDown={(e) => { if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); if (currentMode === "canvas_image") handleImageGenerate(); else handleVideoGenerate(); } }} />
                    <Button onClick={currentMode === "canvas_image" ? handleImageGenerate : handleVideoGenerate} disabled={!prompt.trim() || (currentMode === "canvas_image" && !imageEndpoint)} className="self-end" size="sm">
                      <Sparkles size={12} /> Generate
                    </Button>
                  </div>
                </div>
              </div>
            </div>
          </>
        ) : (
          <div className="flex-1 flex items-center justify-center">
            <EmptyState icon={<Image size={48} />} title="Select or create a workspace" description="Choose from the sidebar or create a new canvas"
              action={<div className="flex gap-2"><Button onClick={() => createWorkspace("canvas_image")} size="sm"><Image size={12} /> Image</Button><Button onClick={() => createWorkspace("canvas_video")} variant="secondary" size="sm"><Video size={12} /> Video</Button></div>} />
          </div>
        )}
      </div>

      {/* Context menu */}
      {contextMenu && (
        <div className="fixed z-50 bg-[var(--surface-1)] border border-[var(--border-subtle)] rounded-lg shadow-xl py-1 min-w-[160px]" style={{ left: contextMenu.x, top: contextMenu.y }} onClick={() => setContextMenu(null)}>
          {contextMenu.assetId && (
            <>
              <button className="w-full px-3 py-1.5 text-xs text-left hover:bg-[var(--hover-bg)] flex items-center gap-2" onClick={() => { const a = currentAssets.find((x) => x.asset_id === contextMenu.assetId); if (a) setPreviewAsset(a); }}><Maximize2 size={12} /> Preview</button>
              <button className="w-full px-3 py-1.5 text-xs text-left hover:bg-[var(--hover-bg)] flex items-center gap-2" onClick={async () => { const dir = await dialogOpen({ directory: true }); if (dir && contextMenu.assetId) await api.centerExportAssets([contextMenu.assetId], dir as string); }}><Download size={12} /> Download</button>
              <button className="w-full px-3 py-1.5 text-xs text-left hover:bg-[var(--hover-bg)] flex items-center gap-2 text-red-400" onClick={async () => { if (contextMenu.assetId) { await api.centerDeleteAssets([contextMenu.assetId]); if (activeTabId) { const b = await api.centerGetWorkspaceBundle(activeTabId); setLoadedBundles((prev) => new Map(prev).set(activeTabId!, b)); } } }}><Trash2 size={12} /> Delete</button>
              <button className="w-full px-3 py-1.5 text-xs text-left hover:bg-[var(--hover-bg)] flex items-center gap-2" onClick={() => { const a = currentAssets.find((x) => x.asset_id === contextMenu.assetId); if (a) navigator.clipboard.writeText(a.file_path); }}><Copy size={12} /> Copy path</button>
              <button className="w-full px-3 py-1.5 text-xs text-left hover:bg-[var(--hover-bg)] flex items-center gap-2" onClick={() => { if (contextMenu.assetId) promoteToReference(contextMenu.assetId); }}><Image size={12} /> Set as reference</button>
            </>
          )}
          {!contextMenu.assetId && activeRound && (
            <>
              <button className="w-full px-3 py-1.5 text-xs text-left hover:bg-[var(--hover-bg)] flex items-center gap-2" onClick={() => { if (activeRound) navigator.clipboard.writeText(activeRound.prompt); }}><Copy size={12} /> Copy prompt</button>
              <button className="w-full px-3 py-1.5 text-xs text-left hover:bg-[var(--hover-bg)] flex items-center gap-2" onClick={() => { if (activeRound) setPrompt(activeRound.prompt); }}><RefreshCw size={12} /> Reuse params</button>
            </>
          )}
        </div>
      )}

      {/* Preview overlay */}
      {previewAsset && (
        <div className="fixed inset-0 z-50 bg-black/80 flex items-center justify-center" onClick={() => setPreviewAsset(null)}>
          <div className="relative max-w-[90vw] max-h-[90vh]" onClick={(e) => e.stopPropagation()}>
            <button onClick={() => setPreviewAsset(null)} className="absolute -top-10 right-0 text-white p-2 hover:bg-white/10 rounded"><X size={20} /></button>
            {previewAsset.kind === "image" ? (
              <img src={convertFileSrc(previewAsset.file_path)} alt="" className="max-w-full max-h-[85vh] rounded-lg" />
            ) : (
              <video src={convertFileSrc(previewAsset.file_path)} controls autoPlay className="max-w-full max-h-[85vh] rounded-lg" />
            )}
            <div className="flex justify-center gap-2 mt-3">
              <Button size="sm" variant="secondary" onClick={async () => { const dir = await dialogOpen({ directory: true }); if (dir) await api.centerExportAssets([previewAsset.asset_id], dir as string); }}><Download size={12} /> Download</Button>
              <Button size="sm" variant="secondary" className="text-red-400" onClick={async () => { await api.centerDeleteAssets([previewAsset.asset_id]); setPreviewAsset(null); if (activeTabId) { const b = await api.centerGetWorkspaceBundle(activeTabId); setLoadedBundles((prev) => new Map(prev).set(activeTabId!, b)); } }}><Trash2 size={12} /> Delete</Button>
            </div>
            {currentAssets.length > 1 && (() => {
              const curIdx = currentAssets.findIndex((a) => a.asset_id === previewAsset.asset_id);
              return (<>
                {curIdx > 0 && <button onClick={() => setPreviewAsset(currentAssets[curIdx - 1])} className="absolute left-2 top-1/2 -translate-y-1/2 p-2 bg-black/50 rounded-full text-white hover:bg-black/70"><ChevronLeft size={20} /></button>}
                {curIdx < currentAssets.length - 1 && <button onClick={() => setPreviewAsset(currentAssets[curIdx + 1])} className="absolute right-2 top-1/2 -translate-y-1/2 p-2 bg-black/50 rounded-full text-white hover:bg-black/70"><ChevronRight size={20} /></button>}
              </>);
            })()}
          </div>
        </div>
      )}
    </div>
  );
}
