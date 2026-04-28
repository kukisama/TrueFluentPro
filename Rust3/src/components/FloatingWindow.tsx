import { useState, useEffect } from "react";
import { motion, AnimatePresence } from "framer-motion";
import { X, Pin, PinOff, Maximize2, Minimize2 } from "lucide-react";
import { Button } from "./ui";
import { useAppStore } from "../stores/app-store";

interface FloatingWindowProps {
  title: string;
  children: React.ReactNode;
  onClose: () => void;
  className?: string;
}

export function FloatingWindow({ title, children, onClose, className }: FloatingWindowProps) {
  const [pinned, setPinned] = useState(false);
  const [expanded, setExpanded] = useState(false);
  const [pos, setPos] = useState({ x: 100, y: 100 });
  const [dragging, setDragging] = useState(false);
  const [dragOffset, setDragOffset] = useState({ x: 0, y: 0 });

  useEffect(() => {
    if (!dragging) return;
    const onMove = (e: MouseEvent) => setPos({ x: e.clientX - dragOffset.x, y: e.clientY - dragOffset.y });
    const onUp = () => setDragging(false);
    window.addEventListener("mousemove", onMove);
    window.addEventListener("mouseup", onUp);
    return () => { window.removeEventListener("mousemove", onMove); window.removeEventListener("mouseup", onUp); };
  }, [dragging, dragOffset]);

  return (
    <motion.div
      initial={{ opacity: 0, scale: 0.9 }}
      animate={{ opacity: 1, scale: 1 }}
      exit={{ opacity: 0, scale: 0.9 }}
      className={`fixed z-50 shadow-2xl border border-[var(--border-subtle)] rounded-xl bg-[var(--surface-0)]/95 backdrop-blur-xl overflow-hidden ${className || ""}`}
      style={{
        left: pos.x,
        top: pos.y,
        width: expanded ? 600 : 380,
        maxHeight: expanded ? "70vh" : 300,
      }}
    >
      <div
        className="flex items-center gap-2 px-3 py-2 border-b border-[var(--border-subtle)] cursor-grab active:cursor-grabbing select-none"
        onMouseDown={(e) => {
          setDragging(true);
          setDragOffset({ x: e.clientX - pos.x, y: e.clientY - pos.y });
        }}
      >
        <span className="text-xs font-medium text-[var(--text-primary)] flex-1">{title}</span>
        <Button variant="ghost" size="icon" className="h-6 w-6" onClick={() => setPinned(!pinned)}>
          {pinned ? <PinOff size={12} /> : <Pin size={12} />}
        </Button>
        <Button variant="ghost" size="icon" className="h-6 w-6" onClick={() => setExpanded(!expanded)}>
          {expanded ? <Minimize2 size={12} /> : <Maximize2 size={12} />}
        </Button>
        <Button variant="ghost" size="icon" className="h-6 w-6" onClick={onClose}>
          <X size={12} />
        </Button>
      </div>
      <div className="overflow-y-auto" style={{ maxHeight: expanded ? "calc(70vh - 40px)" : 260 }}>
        {children}
      </div>
    </motion.div>
  );
}

export function FloatingSubtitle() {
  const { recognizedSegments, isTranslating } = useAppStore();
  const [visible, setVisible] = useState(false);

  useEffect(() => {
    if (isTranslating) setVisible(true);
  }, [isTranslating]);

  if (!visible) return null;

  const recent = recognizedSegments.slice(-5);

  return (
    <AnimatePresence>
      <FloatingWindow title="Live Subtitles" onClose={() => setVisible(false)}>
        <div className="p-3 space-y-2">
          {recent.length === 0 ? (
            <p className="text-xs text-[var(--text-muted)] text-center py-4">
              {isTranslating ? "Listening..." : "No subtitle content"}
            </p>
          ) : (
            recent.map((seg, i) => (
              <div key={i} className="space-y-0.5">
                <p className="text-xs text-[var(--text-primary)]">{seg.source}</p>
                <p className="text-xs text-[var(--active-text)]">{seg.translation}</p>
              </div>
            ))
          )}
        </div>
      </FloatingWindow>
    </AnimatePresence>
  );
}
