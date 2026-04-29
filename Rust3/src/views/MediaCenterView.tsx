import { useState, useCallback, useRef, useEffect, useMemo } from "react";
import { useTranslation } from "react-i18next";
import {
  Image, Video, Plus, Loader2, Download, Trash2,
  Maximize2, ChevronLeft, ChevronRight, Sparkles,
  RefreshCw, X, Search, Check, Copy,
  Undo2, Redo2, Play, ExternalLink, FolderOpen,
  Grid, List, Layers, AlertCircle,
} from "lucide-react";
import { cn } from "../lib/utils";
import {
  Button, Label, Select, Textarea, Badge,
  EmptyState, ScrollArea,
} from "../components/ui";
import { useAppStore } from "../stores/app-store";
import { api } from "../lib/api";
import type {
  CenterWorkspace, CenterWorkspaceBundle,
  CenterAssetDetail, CenterTaskEvent,
} from "../lib/types";
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
type ViewMode = "grid" | "list" | "grouped";

interface UndoEntry {
  type: "generate" | "delete_assets" | "change_prompt" | "change_params";
  roundId?: string;
  assetIds?: string[];
}

interface DeleteTarget {
  type: "workspace" | "assets";
  id: string;
  name?: string;
  count?: number;
}

const MAX_LOADED = 3;
const PAGE_SIZE = 20;

export function MediaCenterView() {
  const { t } = useTranslation();
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

  // T-002: Canvas preview state
  const [canvasAsset, setCanvasAsset] = useState<CenterAssetDetail | null>(null);
  const [canvasElapsed, setCanvasElapsed] = useState(0);

  // T-003: View mode
  const [viewMode, setViewMode] = useState<ViewMode>("grid");
  const [collapsedGroups, setCollapsedGroups] = useState<Set<string>>(new Set());

  // T-004: Pagination
  const [wsOffset, setWsOffset] = useState(0);
  const [hasMoreWs, setHasMoreWs] = useState(true);
  const [loadingMore, setLoadingMore] = useState(false);
  const sentinelRef = useRef<HTMLDivElement>(null);

  // T-005: Delete confirmation dialog
  const [deleteTarget, setDeleteTarget] = useState<DeleteTarget | null>(null);

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
    api.centerListWorkspaces(PAGE_SIZE, 0).then((ws) => {
      setWorkspaces(ws);
      setHasMoreWs(ws.length >= PAGE_SIZE);
      setWsOffset(ws.length);
    }).catch(console.error);
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
        api.centerListWorkspaces(PAGE_SIZE, 0).then((ws) => {
          setWorkspaces(ws);
          setHasMoreWs(ws.length >= PAGE_SIZE);
          setWsOffset(ws.length);
        }).catch(console.error);
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
    const name = mode === "canvas_image" ? `${t("mediaCenter.imageCanvas")} ${workspaces.length + 1}` : `${t("mediaCenter.videoCanvas")} ${workspaces.length + 1}`;
    const ws = await api.centerCreateWorkspace(mode, name);
    setWorkspaces((prev) => [ws, ...prev]);
    await openTab(ws.id);
  }, [workspaces.length, openTab]);

  const deleteWorkspace = useCallback(async (id: string) => {
    await api.centerSoftDeleteWorkspace(id);
    setWorkspaces((prev) => prev.filter((w) => w.id !== id));
    closeTab(id);
    setDeleteTarget(null);
  }, [closeTab]);

  // T-005: Confirm delete handler
  const confirmDelete = useCallback(async () => {
    if (!deleteTarget) return;
    if (deleteTarget.type === "workspace") {
      await deleteWorkspace(deleteTarget.id);
    } else {
      const ids = deleteTarget.id.split(",");
      if (activeTabId) pushUndo(activeTabId, { type: "delete_assets", assetIds: ids });
      await api.centerDeleteAssets(ids);
      setSelectedAssets(new Set());
      if (activeTabId) {
        const b = await api.centerGetWorkspaceBundle(activeTabId);
        setLoadedBundles((prev) => new Map(prev).set(activeTabId!, b));
      }
    }
    setDeleteTarget(null);
  }, [deleteTarget, activeTabId]);

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
      api.centerListWorkspaces(PAGE_SIZE, 0).then((ws) => {
        setWorkspaces(ws);
        setHasMoreWs(ws.length >= PAGE_SIZE);
        setWsOffset(ws.length);
      });
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
    alert(t("mediaCenter.exported", { copied: result.copied }) + (result.failed > 0 ? t("mediaCenter.exportFailed", { failed: result.failed }) : ""));
  }, [selectedAssets]);

  const handleDeleteSelected = useCallback(async () => {
    if (selectedAssets.size === 0) return;
    setDeleteTarget({ type: "assets", id: [...selectedAssets].join(","), count: selectedAssets.size });
  }, [selectedAssets]);

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
      // T-002/T-008: Arrow key navigation for canvas asset
      else if (e.key === "ArrowLeft") { e.preventDefault(); navigateCanvasAsset("prev"); }
      else if (e.key === "ArrowRight") { e.preventDefault(); navigateCanvasAsset("next"); }
    };
    const el = canvasRef.current;
    if (el) el.addEventListener("keydown", handler);
    return () => { if (el) el.removeEventListener("keydown", handler); };
  }, [handleDeleteSelected, handleUndo, handleRedo, selectAll, deselectAll, handleImageGenerate, handleVideoGenerate, currentMode, canvasAsset, currentAssets]);

  useEffect(() => { const h = () => setContextMenu(null); window.addEventListener("click", h); return () => window.removeEventListener("click", h); }, []);

  // T-002: Navigate canvas asset (← / →)
  const navigateCanvasAsset = useCallback((direction: "prev" | "next") => {
    if (!canvasAsset || currentAssets.length === 0) return;
    const idx = currentAssets.findIndex((a) => a.asset_id === canvasAsset.asset_id);
    const newIdx = direction === "prev" ? idx - 1 : idx + 1;
    if (newIdx >= 0 && newIdx < currentAssets.length) {
      setCanvasAsset(currentAssets[newIdx]);
    }
  }, [canvasAsset, currentAssets]);

  // T-002: Elapsed timer for pending state
  useEffect(() => {
    if (!activeBundle || activeBundle.running_tasks.length === 0) {
      setCanvasElapsed(0);
      return;
    }
    const start = Date.now();
    const interval = setInterval(() => setCanvasElapsed(Math.floor((Date.now() - start) / 1000)), 1000);
    return () => clearInterval(interval);
  }, [activeBundle?.running_tasks.length]);

  // T-004: Load more workspaces (infinite scroll)
  const loadMoreWorkspaces = useCallback(async () => {
    if (!hasMoreWs || loadingMore) return;
    setLoadingMore(true);
    try {
      const more = await api.centerListWorkspaces(PAGE_SIZE, wsOffset);
      setWorkspaces((prev) => [...prev, ...more]);
      setWsOffset((prev) => prev + more.length);
      setHasMoreWs(more.length >= PAGE_SIZE);
    } catch (e) {
      console.error(e);
    } finally {
      setLoadingMore(false);
    }
  }, [hasMoreWs, loadingMore, wsOffset]);

  // T-004: IntersectionObserver for infinite scroll
  useEffect(() => {
    if (!sentinelRef.current || !hasMoreWs) return;
    const observer = new IntersectionObserver(
      (entries) => { if (entries[0]?.isIntersecting) loadMoreWorkspaces(); },
      { rootMargin: "200px" },
    );
    observer.observe(sentinelRef.current);
    return () => observer.disconnect();
  }, [hasMoreWs, loadMoreWorkspaces]);

  // T-008: Refresh workspace list + active bundle
  const handleRefresh = useCallback(async () => {
    const ws = await api.centerListWorkspaces(PAGE_SIZE, 0);
    setWorkspaces(ws);
    setHasMoreWs(ws.length >= PAGE_SIZE);
    setWsOffset(ws.length);
    if (activeTabId) {
      const b = await api.centerGetWorkspaceBundle(activeTabId);
      setLoadedBundles((prev) => new Map(prev).set(activeTabId!, b));
    }
  }, [activeTabId]);

  // T-006: Export entire workspace
  const handleExportWorkspace = useCallback(async () => {
    if (!activeTabId) return;
    const dir = await dialogOpen({ directory: true });
    if (!dir) return;
    const result = await api.centerExportWorkspace(activeTabId, dir as string, true);
    alert(t("mediaCenter.exported", { copied: result.copied }) + (result.failed > 0 ? t("mediaCenter.exportFailed", { failed: result.failed }) : ""));
  }, [activeTabId, t]);

  // T-003: Toggle group collapse
  const toggleGroupCollapse = useCallback((roundId: string) => {
    setCollapsedGroups((prev) => {
      const n = new Set(prev);
      if (n.has(roundId)) n.delete(roundId); else n.add(roundId);
      return n;
    });
  }, []);

  const promoteToReference = useCallback(async (assetId: string) => {
    if (!activeTabId || !activeBundle) return;
    const asset = currentAssets.find((a) => a.asset_id === assetId);
    if (!asset) return;
    // T-005/T-008: Validate reference count client-side
    const maxRefs = currentMode === "canvas_video" ? 1 : 8;
    if ((activeBundle.reference_images?.length ?? 0) >= maxRefs) {
      return; // silently refuse — limit reached
    }
    await api.studioAddReferenceImage(activeTabId, asset.file_path);
    const b = await api.centerGetWorkspaceBundle(activeTabId);
    setLoadedBundles((prev) => new Map(prev).set(activeTabId!, b));
  }, [activeTabId, activeBundle, currentAssets, currentMode]);

  // T-008: Derive workspace from asset (edit flow)
  const deriveWorkspaceFromAsset = useCallback(async (assetId: string) => {
    if (!activeTabId || !activeWs) return;
    const asset = currentAssets.find((a) => a.asset_id === assetId);
    if (!asset) return;
    const kind = asset.kind === "video" ? "video" : "image";
    const name = `${t("mediaCenter.editOf")} ${activeWs.name}`;
    const newWs = await api.centerDeriveWorkspace(activeTabId, assetId, kind, name, asset.file_path);
    const updated = await api.centerListWorkspaces(PAGE_SIZE, 0);
    setWorkspaces(updated);
    setHasMoreWs(updated.length >= PAGE_SIZE);
    setWsOffset(updated.length);
    // Open the new workspace tab
    setOpenTabs((prev) => prev.includes(newWs.id) ? prev : [...prev, newWs.id]);
    setActiveTabId(newWs.id);
    const bundle = await api.centerGetWorkspaceBundle(newWs.id);
    setLoadedBundles((prev) => new Map(prev).set(newWs.id, bundle));
  }, [activeTabId, activeWs, currentAssets, t]);

  // T-008: Load all assets for workspace (result rail)
  const [allAssets, setAllAssets] = useState<CenterAssetDetail[]>([]);
  const [showAllAssets, setShowAllAssets] = useState(false);
  const loadAllAssets = useCallback(async () => {
    if (!activeTabId) return;
    const assets = await api.centerGetAllAssets(activeTabId, 200);
    setAllAssets(assets);
    setShowAllAssets(true);
  }, [activeTabId]);

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
              <input type="text" placeholder={t("mediaCenter.search")} value={searchQuery} onChange={(e) => setSearchQuery(e.target.value)}
                className="w-full pl-8 pr-2 py-1.5 text-xs rounded-md border border-[var(--border-subtle)] bg-[var(--surface-1)] text-[var(--text-primary)] focus:outline-none focus:ring-1 focus:ring-brand-500/50" />
            </div>
            <button onClick={() => createWorkspace("canvas_image")} className="p-1.5 rounded-md hover:bg-[var(--hover-bg)] text-[var(--text-muted)]" title={t("mediaCenter.imageCanvas")}><Plus size={16} /></button>
          </div>
          <div className="flex gap-1 text-[10px]">
            {(["all", "canvas_image", "canvas_video"] as FilterType[]).map((ft) => (
              <button key={ft} onClick={() => setFilterType(ft)} className={cn("px-2 py-0.5 rounded", filterType === ft ? "bg-brand-600/15 text-[var(--active-text)]" : "text-[var(--text-muted)] hover:bg-[var(--hover-bg)]")}>
                {ft === "all" ? t("mediaCenter.all") : ft === "canvas_image" ? t("mediaCenter.image") : t("mediaCenter.video")}
              </button>
            ))}
            <span className="mx-1 text-[var(--border-medium)]">|</span>
            {(["all", "today", "7days", "30days"] as FilterTime[]).map((ft) => (
              <button key={ft} onClick={() => setFilterTime(ft)} className={cn("px-2 py-0.5 rounded", filterTime === ft ? "bg-brand-600/15 text-[var(--active-text)]" : "text-[var(--text-muted)] hover:bg-[var(--hover-bg)]")}>
                {ft === "all" ? t("mediaCenter.all") : ft === "today" ? t("mediaCenter.today") : ft === "7days" ? t("mediaCenter.days7") : t("mediaCenter.days30")}
              </button>
            ))}
          </div>
        </div>
        <ScrollArea className="flex-1">
          <div className="p-2 space-y-1">
            {filteredWorkspaces.length === 0 && (
              <div className="p-6 text-center">
                <p className="text-xs text-[var(--text-muted)]">{t("mediaCenter.noWorkspaces")}</p>
                <div className="flex flex-col gap-2 mt-4">
                  <Button size="sm" onClick={() => createWorkspace("canvas_image")}><Image size={12} /> {t("mediaCenter.imageCanvas")}</Button>
                  <Button size="sm" variant="secondary" onClick={() => createWorkspace("canvas_video")}><Video size={12} /> {t("mediaCenter.videoCanvas")}</Button>
                </div>
              </div>
            )}
            {filteredWorkspaces.map((ws) => (
              <button key={ws.id} onClick={() => openTab(ws.id)}
                className={cn("w-full flex items-center gap-2 px-3 py-2 rounded-lg text-left transition-all group", activeTabId === ws.id ? "bg-brand-600/10 text-[var(--active-text)]" : "text-[var(--text-secondary)] hover:bg-[var(--hover-bg)]")}>
                <Badge variant={ws.session_type === "canvas_image" ? "blue" : "amber"} className="text-[9px] px-1.5 py-0 shrink-0">{ws.session_type === "canvas_image" ? t("mediaCenter.imgBadge") : t("mediaCenter.vidBadge")}</Badge>
                <span className="text-xs truncate flex-1">{ws.name}</span>
                {ws.has_running_task && <div className="w-2 h-2 rounded-full bg-blue-500 animate-pulse shrink-0" />}
                <button onClick={(e) => { e.stopPropagation(); setDeleteTarget({ type: "workspace", id: ws.id, name: ws.name }); }} className="opacity-0 group-hover:opacity-100 p-0.5 hover:text-red-400 transition-opacity"><X size={12} /></button>
              </button>
            ))}
            {/* T-004: Infinite scroll sentinel */}
            {hasMoreWs && <div ref={sentinelRef} className="h-8 flex items-center justify-center">{loadingMore && <Loader2 size={14} className="animate-spin text-[var(--text-muted)]" />}</div>}
          </div>
        </ScrollArea>
        {/* T-008: Export workspace button at sidebar bottom */}
        {activeTabId && (
          <div className="p-2 border-t border-[var(--border-subtle)]">
            <button onClick={handleExportWorkspace} className="w-full flex items-center gap-2 px-3 py-1.5 text-xs text-[var(--text-muted)] hover:bg-[var(--hover-bg)] rounded">
              <Download size={12} /> {t("mediaCenter.exportWorkspace")}
            </button>
          </div>
        )}
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
                  <Badge variant={ws?.session_type === "canvas_image" ? "blue" : "amber"} className="text-[8px] px-1 py-0">{ws?.session_type === "canvas_image" ? t("mediaCenter.imgBadge") : t("mediaCenter.vidBadge")}</Badge>
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
                {/* Canvas mode badge */}
                {activeWs?.canvas_mode && (
                  <Badge variant={activeWs.canvas_mode === "edit" ? "amber" : "blue"} className="text-[8px] px-1 py-0">
                    {activeWs.canvas_mode === "edit" ? "Edit" : "Draw"}
                  </Badge>
                )}
                {/* Lineage info */}
                {activeWs?.source_session_name && (
                  <span className="text-[10px] text-[var(--text-muted)] italic truncate max-w-[160px]">
                    ← {activeWs.source_session_name}
                  </span>
                )}
                {totalRounds > 0 && (
                  <div className="flex items-center gap-1 text-xs">
                    <button onClick={() => switchRound("prev")} disabled={activeRoundIdx <= 0} className="p-1 rounded hover:bg-[var(--hover-bg)] disabled:opacity-30"><ChevronLeft size={14} /></button>
                    <span className="text-[var(--text-secondary)]">{t("mediaCenter.round", { current: activeRoundIdx + 1, total: totalRounds })}</span>
                    <button onClick={() => switchRound("next")} disabled={activeRoundIdx >= totalRounds - 1} className="p-1 rounded hover:bg-[var(--hover-bg)] disabled:opacity-30"><ChevronRight size={14} /></button>
                  </div>
                )}
                {activeRound && (
                  <button onClick={() => setPrompt(activeRound.prompt)} className="text-[10px] px-2 py-0.5 rounded bg-[var(--surface-1)] text-[var(--text-muted)] hover:bg-[var(--hover-bg)]">
                    <RefreshCw size={10} className="inline mr-1" />{t("mediaCenter.reuse")}
                  </button>
                )}
                {/* All assets button (result rail) */}
                {activeBundle.all_asset_count > 0 && (
                  <button onClick={loadAllAssets} className="text-[10px] px-2 py-0.5 rounded bg-[var(--surface-1)] text-[var(--text-muted)] hover:bg-[var(--hover-bg)]">
                    {t("mediaCenter.allAssets")} ({activeBundle.all_asset_count})
                  </button>
                )}
              </div>
              <div className="flex items-center gap-1">
                {selectedAssets.size > 0 && (
                  <div className="flex items-center gap-2 mr-2 text-xs text-[var(--text-secondary)]">
                    <span>{t("mediaCenter.selected", { count: selectedAssets.size })}</span>
                    <button onClick={handleExport} className="px-2 py-0.5 rounded bg-brand-600/15 text-brand-500 text-[10px]"><Download size={10} className="inline mr-0.5" />{t("mediaCenter.export")}</button>
                    <button onClick={handleDeleteSelected} className="px-2 py-0.5 rounded bg-red-500/10 text-red-400 text-[10px]"><Trash2 size={10} className="inline mr-0.5" />{t("mediaCenter.delete")}</button>
                    <button onClick={deselectAll} className="px-1 py-0.5 text-[var(--text-muted)]"><X size={12} /></button>
                  </div>
                )}
                {/* T-003/T-008: View mode toggle */}
                <div className="flex items-center border border-[var(--border-subtle)] rounded overflow-hidden mr-1">
                  <button onClick={() => setViewMode("grid")} className={cn("p-1", viewMode === "grid" ? "bg-brand-600/15 text-[var(--active-text)]" : "text-[var(--text-muted)] hover:bg-[var(--hover-bg)]")} title="Grid"><Grid size={12} /></button>
                  <button onClick={() => setViewMode("list")} className={cn("p-1", viewMode === "list" ? "bg-brand-600/15 text-[var(--active-text)]" : "text-[var(--text-muted)] hover:bg-[var(--hover-bg)]")} title="List"><List size={12} /></button>
                  <button onClick={() => setViewMode("grouped")} className={cn("p-1", viewMode === "grouped" ? "bg-brand-600/15 text-[var(--active-text)]" : "text-[var(--text-muted)] hover:bg-[var(--hover-bg)]")} title="Grouped"><Layers size={12} /></button>
                </div>
                {/* T-008: Refresh button */}
                <button onClick={handleRefresh} className="p-1.5 rounded hover:bg-[var(--hover-bg)] text-[var(--text-muted)]" title={t("mediaCenter.refresh")}><RefreshCw size={14} /></button>
                <button onClick={handleUndo} className="p-1.5 rounded hover:bg-[var(--hover-bg)] text-[var(--text-muted)]" title={t("mediaCenter.undo")}><Undo2 size={14} /></button>
                <button onClick={handleRedo} className="p-1.5 rounded hover:bg-[var(--hover-bg)] text-[var(--text-muted)]" title={t("mediaCenter.redo")}><Redo2 size={14} /></button>
              </div>
            </div>

            <div className="flex-1 flex min-h-0">
              {/* Params panel */}
              <div className="w-[240px] border-r border-[var(--border-subtle)] overflow-y-auto p-3 space-y-3 shrink-0" style={{ backgroundColor: "var(--surface-0)" }}>
                {currentMode === "canvas_image" ? (
                  <>
                    <div><Label className="text-[10px]">{t("mediaCenter.model")}</Label><p className="text-[10px] text-[var(--text-secondary)] mt-0.5 truncate">{imageEndpoint ? `${imageEndpoint.name} / ${imageModelId}` : t("mediaCenter.notConfigured")}</p></div>
                    <div><Label className="text-[10px]">{t("mediaCenter.size")}</Label><Select className="w-full mt-0.5 text-xs" value={sizeIdx.toString()} onChange={(e) => setSizeIdx(Number(e.target.value))}>{SIZE_OPTIONS.map((s, i) => <option key={i} value={i}>{s.label}</option>)}</Select></div>
                    <div><Label className="text-[10px]">{t("mediaCenter.quality")}</Label><Select className="w-full mt-0.5 text-xs" value={quality} onChange={(e) => setQuality(e.target.value)}>{QUALITY_OPTIONS.map((q) => <option key={q} value={q}>{q}</option>)}</Select></div>
                    <div><Label className="text-[10px]">{t("mediaCenter.format")}</Label><Select className="w-full mt-0.5 text-xs" value={format} onChange={(e) => setFormat(e.target.value)}>{FORMAT_OPTIONS.map((f) => <option key={f} value={f}>{f}</option>)}</Select></div>
                    <div><Label className="text-[10px]">{t("mediaCenter.count")}</Label><Select className="w-full mt-0.5 text-xs" value={count.toString()} onChange={(e) => setCount(Number(e.target.value))}>{COUNT_OPTIONS.map((n) => <option key={n} value={n}>{n}</option>)}</Select></div>
                    <div><Label className="text-[10px]">{t("mediaCenter.background")}</Label><Select className="w-full mt-0.5 text-xs" value={background} onChange={(e) => setBackground(e.target.value)}>{BG_OPTIONS.map((b) => <option key={b} value={b}>{b}</option>)}</Select></div>
                    <div><Label className="text-[10px]">{t("mediaCenter.fidelity")}</Label><Select className="w-full mt-0.5 text-xs" value={fidelity} onChange={(e) => setFidelity(e.target.value)}>{FIDELITY_OPTIONS.map((f) => <option key={f} value={f}>{f}</option>)}</Select></div>
                  </>
                ) : (
                  <>
                    <div><Label className="text-[10px]">{t("mediaCenter.aspect")}</Label><Select className="w-full mt-0.5 text-xs" value={aspectRatio} onChange={(e) => setAspectRatio(e.target.value)}>{ASPECT_RATIOS.map((a) => <option key={a} value={a}>{a}</option>)}</Select></div>
                    <div><Label className="text-[10px]">{t("mediaCenter.resolution")}</Label><Select className="w-full mt-0.5 text-xs" value={resolution} onChange={(e) => setResolution(e.target.value)}>{RESOLUTIONS.map((r) => <option key={r} value={r}>{r}</option>)}</Select></div>
                    <div><Label className="text-[10px]">{t("mediaCenter.duration")}</Label><Select className="w-full mt-0.5 text-xs" value={duration.toString()} onChange={(e) => setDuration(Number(e.target.value))}>{DURATIONS.map((d) => <option key={d} value={d}>{d}s</option>)}</Select></div>
                    <div><Label className="text-[10px]">{t("mediaCenter.count")}</Label><Select className="w-full mt-0.5 text-xs" value={videoCount.toString()} onChange={(e) => setVideoCount(Number(e.target.value))}>{[1, 2, 3, 4].map((n) => <option key={n} value={n}>{n}</option>)}</Select></div>
                    {videoRefWarning && <p className="text-[10px] text-red-400">{videoRefWarning}</p>}
                  </>
                )}
                <div>
                  <Label className="text-[10px]">{t("mediaCenter.references")}</Label>
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
                        if (currentMode === "canvas_video" && activeBundle.reference_images.length >= 1) { setVideoRefWarning(t("mediaCenter.soraRefLimit")); return; }
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
                {/* T-002: Canvas preview area */}
                {(canvasAsset || activeBundle.running_tasks.length > 0) && (
                  <div className="relative h-[240px] border-b border-[var(--border-subtle)] bg-[var(--surface-2)] flex items-center justify-center shrink-0">
                    {activeBundle.running_tasks.length > 0 && !canvasAsset ? (
                      /* Pending overlay */
                      <div className="flex flex-col items-center gap-3">
                        <div className="w-16 h-16 rounded-full border-4 border-brand-400/30 border-t-brand-500 animate-spin" />
                        <p className="text-sm text-[var(--text-muted)]">{t("mediaCenter.generating")}</p>
                        <p className="text-[10px] text-[var(--text-muted)]">{canvasElapsed}s</p>
                      </div>
                    ) : canvasAsset && activeRound?.status === "failed" ? (
                      /* Failed overlay */
                      <div className="flex flex-col items-center gap-2">
                        <AlertCircle size={32} className="text-red-400" />
                        <p className="text-sm text-red-400">{t("mediaCenter.generationFailed")}</p>
                      </div>
                    ) : canvasAsset ? (
                      /* Preview the selected asset */
                      <>
                        {canvasAsset.kind === "image" ? (
                          <img src={convertFileSrc(canvasAsset.file_path)} alt="" className="max-h-full max-w-full object-contain" />
                        ) : (
                          <video src={convertFileSrc(canvasAsset.file_path)} controls className="max-h-full max-w-full object-contain" />
                        )}
                        {/* Navigation arrows */}
                        {currentAssets.length > 1 && (() => {
                          const idx = currentAssets.findIndex((a) => a.asset_id === canvasAsset.asset_id);
                          return (<>
                            {idx > 0 && <button onClick={() => setCanvasAsset(currentAssets[idx - 1])} className="absolute left-2 top-1/2 -translate-y-1/2 p-2 bg-black/50 rounded-full text-white hover:bg-black/70"><ChevronLeft size={16} /></button>}
                            {idx < currentAssets.length - 1 && <button onClick={() => setCanvasAsset(currentAssets[idx + 1])} className="absolute right-2 top-1/2 -translate-y-1/2 p-2 bg-black/50 rounded-full text-white hover:bg-black/70"><ChevronRight size={16} /></button>}
                          </>);
                        })()}
                        {/* Asset counter */}
                        <div className="absolute bottom-2 left-1/2 -translate-x-1/2 px-2 py-0.5 bg-black/60 rounded text-[10px] text-white">
                          {currentAssets.findIndex((a) => a.asset_id === canvasAsset.asset_id) + 1} / {currentAssets.length}
                        </div>
                      </>
                    ) : null}
                  </div>
                )}
                {/* Empty state when no canvas asset and no tasks */}
                {!canvasAsset && activeBundle.running_tasks.length === 0 && currentAssets.length === 0 && (
                  <div className="h-[240px] border-b border-[var(--border-subtle)] bg-[var(--surface-2)] flex items-center justify-center shrink-0">
                    <EmptyState icon={<Image size={36} />} title={t("mediaCenter.startPrompt")} />
                  </div>
                )}

                <ScrollArea className="flex-1">
                  <div className="p-4">
                    {viewMode === "grouped" ? (
                      /* T-003: Grouped view */
                      <div className="space-y-4">
                        {activeBundle.round_prompts.map((rps) => (
                          <div key={rps.round_id} className="border border-[var(--border-subtle)] rounded-lg overflow-hidden">
                            <button onClick={() => toggleGroupCollapse(rps.round_id)}
                              className="w-full flex items-center gap-2 px-3 py-2 bg-[var(--surface-1)] hover:bg-[var(--hover-bg)] text-left">
                              <Badge variant={activeWs?.canvas_mode === "edit" ? "amber" : "blue"} className="text-[8px] px-1 py-0 shrink-0">
                                {activeWs?.canvas_mode === "edit" ? "Edit" : "Draw"}
                              </Badge>
                              <span className="text-xs text-[var(--text-primary)] truncate flex-1">{rps.prompt_preview}</span>
                              <Badge variant={rps.status === "completed" ? "green" : rps.status === "failed" ? "red" : "blue"} className="text-[8px] px-1.5 py-0">
                                {rps.status}
                              </Badge>
                              <span className="text-[10px] text-[var(--text-muted)]">{rps.asset_count} {t("mediaCenter.assets")}</span>
                              <span className="text-[10px] text-[var(--text-muted)]">{new Date(rps.created_at).toLocaleDateString()}</span>
                              <ChevronRight size={12} className={cn("text-[var(--text-muted)] transition-transform", !collapsedGroups.has(rps.round_id) && "rotate-90")} />
                            </button>
                            {!collapsedGroups.has(rps.round_id) && rps.asset_count > 0 && (
                              <GroupedRoundAssets roundId={rps.round_id} onSelect={(a) => { setCanvasAsset(a); }} onContextMenu={(e, assetId) => { e.preventDefault(); setContextMenu({ x: e.clientX, y: e.clientY, assetId }); }} />
                            )}
                          </div>
                        ))}
                        {activeBundle.round_prompts.length === 0 && (
                          <EmptyState icon={<Layers size={36} />} title={t("mediaCenter.noRounds")} />
                        )}
                      </div>
                    ) : viewMode === "list" ? (
                      /* List view */
                      <div className="space-y-1">
                        {currentAssets.map((asset) => (
                          <div key={asset.id}
                            className={cn("flex items-center gap-3 px-3 py-2 rounded-lg border cursor-pointer", selectedAssets.has(asset.asset_id) ? "border-brand-500 bg-brand-600/5" : "border-transparent hover:bg-[var(--hover-bg)]")}
                            onClick={() => { toggleSelect(asset.asset_id); setCanvasAsset(asset); }}
                            onContextMenu={(e) => { e.preventDefault(); setContextMenu({ x: e.clientX, y: e.clientY, assetId: asset.asset_id }); }}>
                            <div className="w-10 h-10 rounded overflow-hidden bg-[var(--surface-2)] shrink-0">
                              {asset.kind === "image" ? (
                                <img src={convertFileSrc(asset.preview_path || asset.file_path)} alt="" className="w-full h-full object-cover" />
                              ) : (
                                <div className="w-full h-full flex items-center justify-center"><Play size={14} className="text-[var(--text-muted)]" /></div>
                              )}
                            </div>
                            <div className="flex-1 min-w-0">
                              <p className="text-xs text-[var(--text-primary)] truncate">{asset.file_path.split(/[/\\]/).pop()}</p>
                              <p className="text-[10px] text-[var(--text-muted)]">{asset.width}×{asset.height} · {asset.kind}</p>
                            </div>
                            <span className="text-[10px] text-[var(--text-muted)]">{new Date(asset.created_at).toLocaleTimeString()}</span>
                          </div>
                        ))}
                        {currentAssets.length === 0 && activeBundle.running_tasks.length === 0 && (
                          <EmptyState icon={<Image size={36} />} title={t("mediaCenter.startPrompt")} />
                        )}
                      </div>
                    ) : (
                      /* Grid view (default) */
                      currentAssets.length > 0 ? (
                        <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-4 gap-3">
                          {currentAssets.map((asset) => (
                            <div key={asset.id}
                              className={cn("relative rounded-lg border overflow-hidden group cursor-pointer transition-all", selectedAssets.has(asset.asset_id) ? "border-brand-500 ring-2 ring-brand-500/30" : "border-[var(--border-subtle)] hover:border-[var(--border-medium)]")}
                              onClick={() => { toggleSelect(asset.asset_id); setCanvasAsset(asset); }}
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
                      ) : activeBundle.running_tasks.length > 0 ? null : null
                      /* Empty state handled by canvas area above */
                    )}
                  </div>
                </ScrollArea>
                <div className="border-t border-[var(--border-subtle)] p-3" style={{ backgroundColor: "var(--toolbar-bg)" }}>
                  <div className="flex gap-2 max-w-3xl mx-auto">
                    <Textarea value={prompt} onChange={(e) => setPrompt(e.target.value)} placeholder={t("mediaCenter.enterPrompt")} className="flex-1 min-h-[36px] max-h-[100px] text-sm"
                      onKeyDown={(e) => { if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); if (currentMode === "canvas_image") handleImageGenerate(); else handleVideoGenerate(); } }} />
                    <Button onClick={currentMode === "canvas_image" ? handleImageGenerate : handleVideoGenerate} disabled={!prompt.trim() || (currentMode === "canvas_image" && !imageEndpoint)} className="self-end" size="sm">
                      <Sparkles size={12} /> {t("mediaCenter.generate")}
                    </Button>
                  </div>
                </div>
              </div>
            </div>
          </>
        ) : (
          <div className="flex-1 flex items-center justify-center">
            <EmptyState icon={<Image size={48} />} title={t("mediaCenter.selectOrCreate")} description={t("mediaCenter.selectOrCreateDesc")}
              action={<div className="flex gap-2"><Button onClick={() => createWorkspace("canvas_image")} size="sm"><Image size={12} /> {t("mediaCenter.image")}</Button><Button onClick={() => createWorkspace("canvas_video")} variant="secondary" size="sm"><Video size={12} /> {t("mediaCenter.video")}</Button></div>} />
          </div>
        )}
      </div>

      {/* Context menu */}
      {contextMenu && (
        <div className="fixed z-50 bg-[var(--surface-1)] border border-[var(--border-subtle)] rounded-lg shadow-xl py-1 min-w-[160px]" style={{ left: contextMenu.x, top: contextMenu.y }} onClick={() => setContextMenu(null)}>
          {contextMenu.assetId && (
            <>
              <button className="w-full px-3 py-1.5 text-xs text-left hover:bg-[var(--hover-bg)] flex items-center gap-2" onClick={() => { const a = currentAssets.find((x) => x.asset_id === contextMenu.assetId); if (a) setPreviewAsset(a); }}><Maximize2 size={12} /> {t("mediaCenter.preview")}</button>
              {/* T-007: Open file */}
              <button className="w-full px-3 py-1.5 text-xs text-left hover:bg-[var(--hover-bg)] flex items-center gap-2" onClick={() => { const a = currentAssets.find((x) => x.asset_id === contextMenu.assetId); if (a) api.centerOpenFile(a.file_path).catch(console.error); }}><ExternalLink size={12} /> {t("mediaCenter.openFile")}</button>
              {/* T-007: Reveal in explorer */}
              <button className="w-full px-3 py-1.5 text-xs text-left hover:bg-[var(--hover-bg)] flex items-center gap-2" onClick={() => { const a = currentAssets.find((x) => x.asset_id === contextMenu.assetId); if (a) api.centerRevealInExplorer(a.file_path).catch(console.error); }}><FolderOpen size={12} /> {t("mediaCenter.revealInExplorer")}</button>
              <button className="w-full px-3 py-1.5 text-xs text-left hover:bg-[var(--hover-bg)] flex items-center gap-2" onClick={async () => { const dir = await dialogOpen({ directory: true }); if (dir && contextMenu.assetId) await api.centerExportAssets([contextMenu.assetId], dir as string); }}><Download size={12} /> {t("mediaCenter.download")}</button>
              <button className="w-full px-3 py-1.5 text-xs text-left hover:bg-[var(--hover-bg)] flex items-center gap-2 text-red-400" onClick={() => { if (contextMenu.assetId) setDeleteTarget({ type: "assets", id: contextMenu.assetId, count: 1 }); }}><Trash2 size={12} /> {t("mediaCenter.delete")}</button>
              <button className="w-full px-3 py-1.5 text-xs text-left hover:bg-[var(--hover-bg)] flex items-center gap-2" onClick={() => { const a = currentAssets.find((x) => x.asset_id === contextMenu.assetId); if (a) navigator.clipboard.writeText(a.file_path); }}><Copy size={12} /> {t("mediaCenter.copyPath")}</button>
              <button className="w-full px-3 py-1.5 text-xs text-left hover:bg-[var(--hover-bg)] flex items-center gap-2" onClick={() => { if (contextMenu.assetId) promoteToReference(contextMenu.assetId); }}><Image size={12} /> {t("mediaCenter.setAsRef")}</button>
              <button className="w-full px-3 py-1.5 text-xs text-left hover:bg-[var(--hover-bg)] flex items-center gap-2" onClick={() => { if (contextMenu.assetId) deriveWorkspaceFromAsset(contextMenu.assetId); }}><Sparkles size={12} /> {t("mediaCenter.editThisImage")}</button>
            </>
          )}
          {!contextMenu.assetId && activeRound && (
            <>
              <button className="w-full px-3 py-1.5 text-xs text-left hover:bg-[var(--hover-bg)] flex items-center gap-2" onClick={() => { if (activeRound) navigator.clipboard.writeText(activeRound.prompt); }}><Copy size={12} /> {t("mediaCenter.copyPrompt")}</button>
              <button className="w-full px-3 py-1.5 text-xs text-left hover:bg-[var(--hover-bg)] flex items-center gap-2" onClick={() => { if (activeRound) setPrompt(activeRound.prompt); }}><RefreshCw size={12} /> {t("mediaCenter.reuseParams")}</button>
            </>
          )}
        </div>
      )}

      {/* All assets panel (result rail) */}
      {showAllAssets && (
        <div className="fixed inset-y-0 right-0 w-[320px] z-40 bg-[var(--surface-1)] border-l border-[var(--border-subtle)] shadow-xl flex flex-col">
          <div className="flex items-center justify-between px-3 py-2 border-b border-[var(--border-subtle)]">
            <span className="text-xs font-medium text-[var(--text-primary)]">{t("mediaCenter.allAssetsTitle")} ({allAssets.length})</span>
            <button onClick={() => setShowAllAssets(false)} className="p-1 hover:bg-[var(--hover-bg)] rounded"><X size={14} /></button>
          </div>
          <ScrollArea className="flex-1 p-2">
            <div className="grid grid-cols-3 gap-1">
              {allAssets.map((asset) => (
                <div key={asset.id} className="aspect-square rounded overflow-hidden cursor-pointer hover:ring-1 hover:ring-brand-500" onClick={() => setPreviewAsset(asset)}>
                  {asset.kind === "image" ? (
                    <img src={convertFileSrc(asset.preview_path || asset.file_path)} alt="" className="w-full h-full object-cover" />
                  ) : (
                    <div className="w-full h-full bg-[var(--surface-2)] flex items-center justify-center"><Play size={16} className="text-[var(--text-muted)]" /></div>
                  )}
                </div>
              ))}
            </div>
          </ScrollArea>
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
              <Button size="sm" variant="secondary" onClick={async () => { const dir = await dialogOpen({ directory: true }); if (dir) await api.centerExportAssets([previewAsset.asset_id], dir as string); }}><Download size={12} /> {t("mediaCenter.download")}</Button>
              <Button size="sm" variant="secondary" className="text-red-400" onClick={async () => { await api.centerDeleteAssets([previewAsset.asset_id]); setPreviewAsset(null); if (activeTabId) { const b = await api.centerGetWorkspaceBundle(activeTabId); setLoadedBundles((prev) => new Map(prev).set(activeTabId!, b)); } }}><Trash2 size={12} /> {t("mediaCenter.delete")}</Button>
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

      {/* T-005: Delete confirmation dialog */}
      {deleteTarget && (
        <div className="fixed inset-0 z-50 bg-black/50 flex items-center justify-center" onClick={() => setDeleteTarget(null)}>
          <div className="bg-[var(--surface-1)] border border-[var(--border-subtle)] rounded-xl shadow-2xl p-6 max-w-sm w-full mx-4" onClick={(e) => e.stopPropagation()}>
            <h3 className="text-sm font-medium text-[var(--text-primary)] mb-2">
              {deleteTarget.type === "workspace" ? t("mediaCenter.deleteWorkspaceTitle") : t("mediaCenter.deleteAssetsTitle")}
            </h3>
            <p className="text-xs text-[var(--text-secondary)] mb-4">
              {deleteTarget.type === "workspace"
                ? t("mediaCenter.deleteWorkspaceDesc", { name: deleteTarget.name })
                : t("mediaCenter.deleteAssetsDesc", { count: deleteTarget.count })}
            </p>
            <div className="flex justify-end gap-2">
              <Button size="sm" variant="secondary" onClick={() => setDeleteTarget(null)}>{t("mediaCenter.cancel")}</Button>
              <Button size="sm" variant="danger" onClick={confirmDelete}>{t("mediaCenter.confirmDelete")}</Button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

// ── T-003: Helper component for grouped view (lazy-loads round assets) ──

function GroupedRoundAssets({ roundId, onSelect, onContextMenu }: {
  roundId: string;
  onSelect: (asset: CenterAssetDetail) => void;
  onContextMenu: (e: React.MouseEvent, assetId: string) => void;
}) {
  const [assets, setAssets] = useState<CenterAssetDetail[]>([]);
  const [loaded, setLoaded] = useState(false);

  useEffect(() => {
    if (!loaded) {
      api.centerGetRoundAssets(roundId).then((a) => { setAssets(a); setLoaded(true); }).catch(console.error);
    }
  }, [roundId, loaded]);

  if (!loaded) return <div className="p-2 flex items-center justify-center"><Loader2 size={14} className="animate-spin text-[var(--text-muted)]" /></div>;

  return (
    <div className="p-2 overflow-x-auto">
      <div className="flex gap-2">
        {assets.map((asset) => (
          <div key={asset.id} className="w-16 h-16 rounded overflow-hidden shrink-0 cursor-pointer hover:ring-2 hover:ring-brand-500 transition-all"
            onClick={() => onSelect(asset)}
            onContextMenu={(e) => onContextMenu(e, asset.asset_id)}>
            {asset.kind === "image" ? (
              <img src={convertFileSrc(asset.preview_path || asset.file_path)} alt="" className="w-full h-full object-cover" />
            ) : (
              <div className="w-full h-full bg-[var(--surface-2)] flex items-center justify-center"><Play size={14} className="text-[var(--text-muted)]" /></div>
            )}
          </div>
        ))}
        {assets.length === 0 && <p className="text-[10px] text-[var(--text-muted)] p-2">No assets</p>}
      </div>
    </div>
  );
}
