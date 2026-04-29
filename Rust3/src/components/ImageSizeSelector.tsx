import { useState, useCallback, useMemo } from "react";

interface ImageSizeSelectorProps {
  value: string;
  quality: string;
  onChange: (size: string) => void;
  onQualityChange?: (q: string) => void;
  className?: string;
}

const CANVAS_SIZE = 200;
const GRID = 16;
const MIN_PX = 256;
const MAX_PX = 4096;

function alignToGrid(v: number): number {
  return Math.max(MIN_PX, Math.min(MAX_PX, Math.round(v / GRID) * GRID));
}

function gcd(a: number, b: number): number {
  while (b) {
    [a, b] = [b, a % b];
  }
  return a;
}

function formatRatio(w: number, h: number): string {
  const g = gcd(w, h);
  return `${w / g}:${h / g}`;
}

function estimateTokens(w: number, h: number): number {
  return Math.ceil((w * h) / 1024);
}

function formatPixels(n: number): string {
  return n.toLocaleString();
}

/** Visual canvas size selector for AI image generation. */
export function ImageSizeSelector({
  value,
  quality,
  onChange,
  onQualityChange,
  className,
}: ImageSizeSelectorProps) {
  const [dragging, setDragging] = useState(false);

  const { width, height } = useMemo(() => {
    const parts = value.split("x");
    const w = parseInt(parts[0], 10) || 1024;
    const h = parseInt(parts[1], 10) || 1024;
    return { width: w, height: h };
  }, [value]);

  // Scale factor: map MAX_PX to CANVAS_SIZE
  const scale = CANVAS_SIZE / MAX_PX;
  const rectW = Math.max(8, width * scale);
  const rectH = Math.max(8, height * scale);

  const totalPx = width * height;
  const ratio = formatRatio(width, height);
  const tokens = estimateTokens(width, height);

  const handleCanvasClick = useCallback(
    (e: React.MouseEvent<HTMLDivElement>) => {
      const rect = e.currentTarget.getBoundingClientRect();
      const x = e.clientX - rect.left;
      const y = e.clientY - rect.top;
      const newW = alignToGrid(x / scale);
      const newH = alignToGrid(y / scale);
      onChange(`${newW}x${newH}`);
    },
    [onChange, scale],
  );

  const handleDragStart = useCallback(
    (e: React.MouseEvent) => {
      e.preventDefault();
      e.stopPropagation();
      setDragging(true);

      const canvas = (e.target as HTMLElement).closest("[data-canvas]") as HTMLElement;
      if (!canvas) return;

      const onMove = (ev: MouseEvent) => {
        const rect = canvas.getBoundingClientRect();
        const x = ev.clientX - rect.left;
        const y = ev.clientY - rect.top;
        const newW = alignToGrid(x / scale);
        const newH = alignToGrid(y / scale);
        onChange(`${newW}x${newH}`);
      };

      const onUp = () => {
        setDragging(false);
        window.removeEventListener("mousemove", onMove);
        window.removeEventListener("mouseup", onUp);
      };

      window.addEventListener("mousemove", onMove);
      window.addEventListener("mouseup", onUp);
    },
    [onChange, scale],
  );

  return (
    <div className={`space-y-3 ${className || ""}`}>
      {/* Canvas area */}
      <div
        data-canvas
        className="relative border border-[var(--border-medium)] rounded-lg bg-[var(--surface-1)] overflow-hidden"
        style={{ width: CANVAS_SIZE + 2, height: CANVAS_SIZE + 2, cursor: "crosshair" }}
        onClick={handleCanvasClick}
      >
        {/* Grid pattern */}
        <div
          className="absolute inset-0 opacity-10"
          style={{
            backgroundImage:
              "linear-gradient(var(--text-muted) 1px, transparent 1px), linear-gradient(90deg, var(--text-muted) 1px, transparent 1px)",
            backgroundSize: `${GRID * scale}px ${GRID * scale}px`,
          }}
        />

        {/* Size rectangle */}
        <div
          className="absolute top-0 left-0 bg-brand-500/20 border border-brand-500/60 rounded-sm transition-all"
          style={{
            width: rectW,
            height: rectH,
            transition: dragging ? "none" : "width 0.15s, height 0.15s",
          }}
        />

        {/* Drag handle */}
        <div
          className="absolute w-3 h-3 bg-brand-600 rounded-full border-2 border-white shadow-md hover:scale-125 transition-transform"
          style={{
            left: rectW - 6,
            top: rectH - 6,
            cursor: "nwse-resize",
            zIndex: 10,
          }}
          onMouseDown={handleDragStart}
        />

        {/* Dimension label inside rectangle */}
        <div
          className="absolute text-[10px] font-mono text-brand-600 pointer-events-none"
          style={{ left: 4, top: 4 }}
        >
          {width}×{height}
        </div>
      </div>

      {/* Info text */}
      <p className="text-xs text-[var(--text-muted)] font-mono">
        {formatPixels(totalPx)} px | {ratio} | ~{tokens.toLocaleString()} token
        {tokens > 2000 && <span className="text-amber-500 ml-1">⚠️ 2K+</span>}
      </p>

      {/* Quality selector */}
      {onQualityChange && (
        <div className="flex gap-1.5">
          {(["low", "medium", "high", "auto"] as const).map((q) => (
            <button
              key={q}
              className={`px-2.5 py-1 text-xs rounded-lg border transition-all ${
                quality === q
                  ? "bg-brand-600 text-white border-brand-600"
                  : "bg-[var(--surface-1)] text-[var(--text-secondary)] border-[var(--border-subtle)] hover:border-brand-500/40"
              }`}
              onClick={() => onQualityChange(q)}
            >
              {q}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
