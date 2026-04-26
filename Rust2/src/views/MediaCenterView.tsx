import { useState, useCallback } from "react";
import { useTranslation } from "react-i18next";
import {
  Image, Video, Plus, Loader2, Download, Trash2,
  Maximize2, ChevronLeft, ChevronRight, Sparkles,
  RefreshCw, X,
} from "lucide-react";
import { cn } from "../lib/utils";
import {
  Button, Label, Select, Textarea, Badge,
  EmptyState, ScrollArea,
} from "../components/ui";
import { useAppStore } from "../stores/app-store";
import { api, type ImageGenResult } from "../lib/tauri-api";

/* ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
   媒体中心 — 画板式图片/视频批量生成
   对标 C# MediaCenterV2View
   ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━ */

const SIZE_OPTIONS = [
  { label: "1024×1024", w: 1024, h: 1024 },
  { label: "1024×1792", w: 1024, h: 1792 },
  { label: "1792×1024", w: 1792, h: 1024 },
  { label: "自定义", w: 0, h: 0 },
];

const QUALITY_OPTIONS = ["auto", "low", "medium", "high"];
const FORMAT_OPTIONS = ["png", "jpeg", "webp"];
const BG_OPTIONS = ["auto", "opaque", "transparent"];
const FIDELITY_OPTIONS = ["auto", "low", "high"];
const COUNT_OPTIONS = [1, 2, 3, 4, 5];

const ASPECT_RATIOS = ["1:1", "16:9", "9:16"];
const RESOLUTIONS = ["480p", "720p", "1080p"];
const DURATIONS = [5, 10, 15, 20];

type CanvasMode = "image" | "video";
type AssetStatus = "idle" | "generating" | "completed" | "failed";

interface WorkspaceTab {
  id: string;
  mode: CanvasMode;
  prompt: string;
  status: AssetStatus;
  results: GeneratedAsset[];
  currentIndex: number;
  referenceImages: string[]; // base64 list
  error?: string;
  elapsedMs?: number;
}

interface GeneratedAsset {
  id: string;
  base64?: string;
  url?: string;
  revisedPrompt?: string;
  thumbnailBase64?: string;
}

export function MediaCenterView() {
  const { t } = useTranslation();
  const config = useAppStore((s) => s.config);

  // State
  const [canvasMode, setCanvasMode] = useState<CanvasMode>("image");
  const [workspaces, setWorkspaces] = useState<WorkspaceTab[]>([]);
  const [activeWsId, setActiveWsId] = useState<string | null>(null);
  const [prompt, setPrompt] = useState("");

  // Image params
  const [sizeIdx, setSizeIdx] = useState(0);
  const [quality, setQuality] = useState(config?.media?.image_quality || "auto");
  const [format, setFormat] = useState(config?.media?.image_format || "png");
  const [count, setCount] = useState(config?.media?.image_count || 1);
  const [background, setBackground] = useState(config?.media?.image_background || "auto");
  const [fidelity, setFidelity] = useState("auto");

  // Video params
  const [aspectRatio, setAspectRatio] = useState(config?.media?.video_aspect_ratio || "16:9");
  const [resolution, setResolution] = useState(config?.media?.video_resolution || "720p");
  const [duration, setDuration] = useState(config?.media?.video_seconds || 5);
  const [videoCount, setVideoCount] = useState(config?.media?.video_variants || 1);

  const activeWs = workspaces.find((ws) => ws.id === activeWsId);

  // 从配置中获取模型
  const imageModelRef = config?.media?.image_model;
  const imageEndpoint = config?.endpoints.find(
    (ep) => ep.enabled && (
      imageModelRef?.endpoint_id
        ? ep.id === imageModelRef.endpoint_id
        : ep.endpoint_type !== "azure_speech" && ep.models.some((m) => m.capabilities.includes("image"))
    ),
  );
  const imageModelId = imageModelRef?.model_id || imageEndpoint?.models.find((m) => m.capabilities.includes("image"))?.model_id || "gpt-image-2";

  const createWorkspace = useCallback(() => {
    const id = Date.now().toString(36) + Math.random().toString(36).slice(2, 6);
    const ws: WorkspaceTab = {
      id,
      mode: canvasMode,
      prompt: "",
      status: "idle",
      results: [],
      currentIndex: 0,
      referenceImages: [],
    };
    setWorkspaces((prev) => [...prev, ws]);
    setActiveWsId(id);
  }, [canvasMode]);

  const handleSubmit = useCallback(async () => {
    if (!prompt.trim() || !imageEndpoint) return;

    // Create workspace if none
    let wsId = activeWsId;
    if (!wsId) {
      const id = Date.now().toString(36) + Math.random().toString(36).slice(2, 6);
      const ws: WorkspaceTab = {
        id,
        mode: canvasMode,
        prompt: prompt,
        status: "generating",
        results: [],
        currentIndex: 0,
        referenceImages: [],
      };
      setWorkspaces((prev) => [...prev, ws]);
      setActiveWsId(id);
      wsId = id;
    } else {
      setWorkspaces((prev) =>
        prev.map((ws) => ws.id === wsId ? { ...ws, prompt, status: "generating" as AssetStatus, error: undefined } : ws)
      );
    }

    const startTime = Date.now();

    if (canvasMode === "image") {
      try {
        const size = SIZE_OPTIONS[sizeIdx];
        const results: ImageGenResult[] = await api.generateImage({
          prompt,
          width: size.w || 1024,
          height: size.h || 1024,
          model: imageModelId,
          quality,
          endpoint_id: imageEndpoint.id,
        });
        const assets: GeneratedAsset[] = results.map((r, i) => ({
          id: `${wsId}-${i}`,
          base64: r.base64,
          url: r.url,
          revisedPrompt: r.revised_prompt,
        }));
        setWorkspaces((prev) =>
          prev.map((ws) => ws.id === wsId
            ? { ...ws, status: "completed" as AssetStatus, results: assets, elapsedMs: Date.now() - startTime }
            : ws)
        );
      } catch (err) {
        setWorkspaces((prev) =>
          prev.map((ws) => ws.id === wsId
            ? { ...ws, status: "failed" as AssetStatus, error: String(err), elapsedMs: Date.now() - startTime }
            : ws)
        );
      }
    } else {
      // Video placeholder
      setWorkspaces((prev) =>
        prev.map((ws) => ws.id === wsId
          ? { ...ws, status: "failed" as AssetStatus, error: "视频生成即将支持" }
          : ws)
      );
    }
    setPrompt("");
  }, [prompt, activeWsId, canvasMode, imageEndpoint, imageModelId, quality, sizeIdx]);

  const removeWorkspace = (id: string) => {
    setWorkspaces((prev) => prev.filter((ws) => ws.id !== id));
    if (activeWsId === id) {
      setActiveWsId(workspaces.length > 1 ? workspaces[0].id : null);
    }
  };

  return (
    <div className="flex h-full">
      {/* ── 左侧参数面板 ── */}
      <div className="w-[264px] border-r border-[var(--border-subtle)] flex flex-col shrink-0 overflow-y-auto"
        style={{ backgroundColor: "var(--sidebar-bg)" }}>
        <div className="p-4 space-y-4">
          <h1 className="text-base font-semibold text-[var(--text-primary)]">{t("mediaCenter.title")}</h1>

          {/* 模式切换 */}
          <div className="flex gap-2">
            <button onClick={() => setCanvasMode("image")}
              className={cn("flex-1 flex items-center justify-center gap-1.5 py-2 rounded-lg text-xs font-medium transition-all",
                canvasMode === "image" ? "bg-brand-600/15 text-[var(--active-text)]" : "text-[var(--text-muted)] hover:bg-[var(--hover-bg)]")}>
              <Image size={14} /> 图片
            </button>
            <button onClick={() => setCanvasMode("video")}
              className={cn("flex-1 flex items-center justify-center gap-1.5 py-2 rounded-lg text-xs font-medium transition-all",
                canvasMode === "video" ? "bg-brand-600/15 text-[var(--active-text)]" : "text-[var(--text-muted)] hover:bg-[var(--hover-bg)]")}>
              <Video size={14} /> 视频
            </button>
          </div>

          {canvasMode === "image" ? (
            <>
              {/* 图片模型 */}
              <div>
                <Label>{t("mediaCenter.imageModel")}</Label>
                <p className="text-xs text-[var(--text-secondary)] px-2 py-1.5 bg-[var(--surface-1)] rounded mt-1">
                  {imageEndpoint ? `${imageEndpoint.name} / ${imageModelId}` : "未配置"}
                </p>
              </div>

              {/* 尺寸 */}
              <div>
                <Label>{t("mediaCenter.size")}</Label>
                <Select className="w-full mt-1" value={sizeIdx.toString()} onChange={(e) => setSizeIdx(Number(e.target.value))}>
                  {SIZE_OPTIONS.map((s, i) => <option key={i} value={i}>{s.label}</option>)}
                </Select>
              </div>

              {/* 质量 */}
              <div>
                <Label>{t("mediaCenter.quality")}</Label>
                <Select className="w-full mt-1" value={quality} onChange={(e) => setQuality(e.target.value)}>
                  {QUALITY_OPTIONS.map((q) => <option key={q} value={q}>{q}</option>)}
                </Select>
              </div>

              {/* 格式 */}
              <div>
                <Label>{t("mediaCenter.format")}</Label>
                <Select className="w-full mt-1" value={format} onChange={(e) => setFormat(e.target.value)}>
                  {FORMAT_OPTIONS.map((f) => <option key={f} value={f}>{f}</option>)}
                </Select>
              </div>

              {/* 数量 */}
              <div>
                <Label>{t("mediaCenter.count")}</Label>
                <Select className="w-full mt-1" value={count.toString()} onChange={(e) => setCount(Number(e.target.value))}>
                  {COUNT_OPTIONS.map((n) => <option key={n} value={n}>{n}</option>)}
                </Select>
              </div>

              {/* 背景 */}
              <div>
                <Label>{t("mediaCenter.background")}</Label>
                <Select className="w-full mt-1" value={background} onChange={(e) => setBackground(e.target.value)}>
                  {BG_OPTIONS.map((b) => <option key={b} value={b}>{b}</option>)}
                </Select>
              </div>

              {/* 保真度 */}
              <div>
                <Label>{t("mediaCenter.fidelity")}</Label>
                <Select className="w-full mt-1" value={fidelity} onChange={(e) => setFidelity(e.target.value)}>
                  {FIDELITY_OPTIONS.map((f) => <option key={f} value={f}>{f}</option>)}
                </Select>
              </div>
            </>
          ) : (
            <>
              {/* 视频参数 */}
              <div>
                <Label>{t("mediaCenter.aspectRatio")}</Label>
                <Select className="w-full mt-1" value={aspectRatio} onChange={(e) => setAspectRatio(e.target.value)}>
                  {ASPECT_RATIOS.map((a) => <option key={a} value={a}>{a}</option>)}
                </Select>
              </div>
              <div>
                <Label>{t("mediaCenter.resolution")}</Label>
                <Select className="w-full mt-1" value={resolution} onChange={(e) => setResolution(e.target.value)}>
                  {RESOLUTIONS.map((r) => <option key={r} value={r}>{r}</option>)}
                </Select>
              </div>
              <div>
                <Label>{t("mediaCenter.duration")}</Label>
                <Select className="w-full mt-1" value={duration.toString()} onChange={(e) => setDuration(Number(e.target.value))}>
                  {DURATIONS.map((d) => <option key={d} value={d}>{d}秒</option>)}
                </Select>
              </div>
              <div>
                <Label>{t("mediaCenter.variants")}</Label>
                <Select className="w-full mt-1" value={videoCount.toString()} onChange={(e) => setVideoCount(Number(e.target.value))}>
                  {[1, 2, 3, 4].map((n) => <option key={n} value={n}>{n}</option>)}
                </Select>
              </div>
            </>
          )}

          {/* 参考素材 */}
          <div>
            <Label>{t("mediaCenter.reference")}</Label>
            <div className="flex gap-2 mt-1 flex-wrap">
              {activeWs?.referenceImages.map((_, i) => (
                <div key={i} className="w-12 h-12 rounded bg-[var(--surface-2)] border border-[var(--border-subtle)]" />
              ))}
              <button className="w-12 h-12 rounded border border-dashed border-[var(--border-medium)] flex items-center justify-center text-[var(--text-muted)] hover:border-brand-500/50 transition-colors">
                <Plus size={16} />
              </button>
            </div>
          </div>
        </div>
      </div>

      {/* ── 中央画布 ── */}
      <div className="flex-1 flex flex-col min-w-0">
        {activeWs ? (
          <>
            {/* 画布主区 */}
            <div className="flex-1 flex items-center justify-center p-6 relative">
              {activeWs.status === "generating" ? (
                <div className="flex flex-col items-center gap-4">
                  <Loader2 size={48} className="text-brand-400 animate-spin" />
                  <p className="text-sm text-[var(--text-muted)]">{t("mediaCenter.generating")}</p>
                  <p className="text-xs text-[var(--text-placeholder)]">{t("mediaCenter.elapsed")}: {((activeWs.elapsedMs || 0) / 1000).toFixed(1)}s</p>
                </div>
              ) : activeWs.status === "failed" ? (
                <div className="flex flex-col items-center gap-3 text-center">
                  <X size={48} className="text-red-400" />
                  <p className="text-sm text-red-400">{activeWs.error}</p>
                </div>
              ) : activeWs.results.length > 0 ? (
                <div className="relative max-w-2xl max-h-full">
                  {activeWs.results[activeWs.currentIndex]?.base64 && (
                    <img
                      src={`data:image/png;base64,${activeWs.results[activeWs.currentIndex].base64}`}
                      alt={activeWs.prompt}
                      className="max-w-full max-h-[calc(100vh-300px)] rounded-xl border border-[var(--border-subtle)] shadow-2xl"
                    />
                  )}
                  {activeWs.results.length > 1 && (
                    <div className="absolute inset-x-0 bottom-4 flex items-center justify-center gap-4">
                      <Button variant="secondary" size="icon" className="h-8 w-8 backdrop-blur-md"
                        onClick={() => setWorkspaces((prev) => prev.map((ws) =>
                          ws.id === activeWsId ? { ...ws, currentIndex: Math.max(0, ws.currentIndex - 1) } : ws))}>
                        <ChevronLeft size={16} />
                      </Button>
                      <span className="text-xs text-white bg-black/50 px-2 py-1 rounded-full backdrop-blur-md">
                        {activeWs.currentIndex + 1} / {activeWs.results.length}
                      </span>
                      <Button variant="secondary" size="icon" className="h-8 w-8 backdrop-blur-md"
                        onClick={() => setWorkspaces((prev) => prev.map((ws) =>
                          ws.id === activeWsId ? { ...ws, currentIndex: Math.min(ws.results.length - 1, ws.currentIndex + 1) } : ws))}>
                        <ChevronRight size={16} />
                      </Button>
                    </div>
                  )}
                  {/* Hover actions */}
                  <div className="absolute top-3 right-3 flex gap-1.5">
                    <Button variant="secondary" size="icon" className="h-7 w-7 backdrop-blur-md"><Maximize2 size={12} /></Button>
                    <Button variant="secondary" size="icon" className="h-7 w-7 backdrop-blur-md"><Download size={12} /></Button>
                  </div>
                </div>
              ) : (
                <EmptyState icon={<Image size={48} />} title="输入提示词生成图片" />
              )}
            </div>

            {/* 底部输入区 */}
            <div className="border-t border-[var(--border-subtle)] p-4" style={{ backgroundColor: "var(--toolbar-bg)" }}>
              <div className="flex gap-2 max-w-3xl mx-auto">
                <Textarea
                  value={prompt}
                  onChange={(e) => setPrompt(e.target.value)}
                  placeholder="输入提示词..."
                  className="flex-1 min-h-[40px] max-h-[120px]"
                  onKeyDown={(e) => { if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); handleSubmit(); } }}
                />
                <Button onClick={handleSubmit} disabled={!prompt.trim() || !imageEndpoint || activeWs?.status === "generating"}
                  className="self-end">
                  <Sparkles size={14} /> {t("mediaCenter.submit")}
                </Button>
                <Button variant="ghost" size="icon" className="self-end h-9 w-9" title={t("mediaCenter.refresh")}>
                  <RefreshCw size={14} />
                </Button>
              </div>
            </div>
          </>
        ) : (
          <div className="flex-1 flex items-center justify-center">
            <EmptyState
              icon={<Image size={48} />}
              title={t("mediaCenter.noWorkspace")}
              description={t("mediaCenter.noWorkspaceHint")}
              action={
                <Button onClick={createWorkspace}>
                  <Plus size={14} /> {t("mediaCenter.newWorkspace")}
                </Button>
              }
            />
          </div>
        )}
      </div>

      {/* ── 右侧工作区缩略图 ── */}
      <div className="w-[120px] border-l border-[var(--border-subtle)] flex flex-col shrink-0"
        style={{ backgroundColor: "var(--sidebar-bg)" }}>
        <div className="p-2 border-b border-[var(--border-subtle)]">
          <Button variant="ghost" size="sm" className="w-full" onClick={createWorkspace}>
            <Plus size={14} />
          </Button>
        </div>
        <ScrollArea className="flex-1">
          <div className="p-2 space-y-2">
            {workspaces.map((ws) => (
              <button
                key={ws.id}
                onClick={() => setActiveWsId(ws.id)}
                className={cn(
                  "w-full aspect-square rounded-lg border overflow-hidden relative group transition-all",
                  activeWsId === ws.id
                    ? "border-brand-500 ring-2 ring-brand-500/30"
                    : "border-[var(--border-subtle)] hover:border-[var(--border-medium)]",
                )}
              >
                {/* Status badge */}
                <div className="absolute top-1 right-1 z-10">
                  {ws.status === "generating" && <div className="w-2 h-2 rounded-full bg-blue-500 animate-pulse" />}
                  {ws.status === "completed" && <div className="w-2 h-2 rounded-full bg-emerald-500" />}
                  {ws.status === "failed" && <div className="w-2 h-2 rounded-full bg-red-500" />}
                </div>
                {/* Mode badge */}
                <div className="absolute top-1 left-1 z-10">
                  <Badge variant={ws.mode === "image" ? "blue" : "amber"} className="text-[8px] px-1 py-0">
                    {ws.mode === "image" ? "图" : "视"}
                  </Badge>
                </div>
                {/* Thumbnail */}
                <div className="w-full h-full bg-[var(--surface-2)] flex items-center justify-center">
                  {ws.results[0]?.base64 ? (
                    <img src={`data:image/png;base64,${ws.results[0].base64}`} alt="" className="w-full h-full object-cover" />
                  ) : ws.status === "generating" ? (
                    <Loader2 size={16} className="text-[var(--text-muted)] animate-spin" />
                  ) : (
                    <Image size={16} className="text-[var(--text-muted)]" />
                  )}
                </div>
                {/* Remove button on hover */}
                <button
                  onClick={(e) => { e.stopPropagation(); removeWorkspace(ws.id); }}
                  className="absolute bottom-1 right-1 opacity-0 group-hover:opacity-100 transition-opacity bg-black/60 rounded p-0.5"
                >
                  <Trash2 size={10} className="text-white" />
                </button>
              </button>
            ))}
          </div>
        </ScrollArea>
      </div>
    </div>
  );
}
