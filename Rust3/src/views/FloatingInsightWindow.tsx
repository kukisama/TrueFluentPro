import { useState, useEffect, useCallback } from "react";
import { listen } from "@tauri-apps/api/event";
import { getCurrentWindow } from "@tauri-apps/api/window";

/**
 * Floating Insight子窗口 — 1:1 对照 C# FloatingInsightWindow.axaml
 *
 * 窗口属性：transparent, no decorations, always_on_top, resizable
 * 由 Rust 后端 WebviewWindowBuilder 创建。
 *
 * 通信路径：
 *   Rust 后端 emit "insight-update" → 本窗口 listen
 */

interface InsightPayload {
  markdown: string;
  streaming: boolean;
}

export function FloatingInsightApp() {
  const [markdown, setMarkdown] = useState("Waiting for insights...");
  const [fontSize, setFontSize] = useState(14);
  const [isStreaming, setIsStreaming] = useState(false);

  // 订阅后端 insight-update 事件
  useEffect(() => {
    const unlisten = listen<InsightPayload>("insight-update", (event) => {
      const p = event.payload;
      if (p.streaming) {
        // 流式追加
        setMarkdown(prev => prev === "Waiting for insights..." ? p.markdown : prev + p.markdown);
        setIsStreaming(true);
      } else {
        // 完整替换
        setMarkdown(p.markdown);
        setIsStreaming(false);
      }
    });
    return () => { unlisten.then(fn => fn()); };
  }, []);

  const handleMouseDown = useCallback(async (e: React.MouseEvent) => {
    if ((e.target as HTMLElement).closest("[data-no-drag]")) return;
    try {
      await getCurrentWindow().startDragging();
    } catch { /* non-main or unsupported */ }
  }, []);

  const handleClose = useCallback(async () => {
    try {
      await getCurrentWindow().close();
    } catch { /* best-effort */ }
  }, []);

  return (
    <div
      style={{
        width: "100%",
        height: "100%",
        background: "rgba(15, 23, 42, 0.95)",
        borderRadius: 8,
        border: "1px solid rgba(255,255,255,0.12)",
        overflow: "hidden",
        display: "flex",
        flexDirection: "column",
        boxShadow: "0 8px 24px rgba(0,0,0,0.3)",
      }}
    >
      {/* 标题栏 — 可拖动 */}
      <div
        style={{
          display: "flex",
          alignItems: "center",
          gap: 6,
          padding: "6px 10px",
          cursor: "grab",
          flexShrink: 0,
          borderBottom: "1px solid rgba(255,255,255,0.08)",
        }}
        onMouseDown={handleMouseDown}
      >
        <span style={{ fontSize: 16 }}>🧠</span>
        <span style={{ fontSize: 13, fontWeight: 600, color: "rgba(255,255,255,0.9)", flex: 1 }}>
          Floating Insight
        </span>

        {/* 字号控制 */}
        <button
          data-no-drag
          onClick={() => setFontSize(s => Math.max(10, s - 2))}
          style={{
            background: "rgba(255,255,255,0.1)",
            color: "white",
            border: "1px solid rgba(255,255,255,0.15)",
            borderRadius: 4,
            fontSize: 11,
            padding: "1px 6px",
            cursor: "pointer",
          }}
          title="Decrease font size"
        >
          A-
        </button>
        <button
          data-no-drag
          onClick={() => setFontSize(s => Math.min(24, s + 2))}
          style={{
            background: "rgba(255,255,255,0.1)",
            color: "white",
            border: "1px solid rgba(255,255,255,0.15)",
            borderRadius: 4,
            fontSize: 11,
            padding: "1px 6px",
            cursor: "pointer",
          }}
          title="Increase font size"
        >
          A+
        </button>

        <button
          data-no-drag
          onClick={handleClose}
          style={{
            background: "rgba(255,255,255,0.1)",
            color: "white",
            border: "none",
            borderRadius: 4,
            fontSize: 14,
            padding: "0 6px",
            cursor: "pointer",
            lineHeight: "20px",
            marginLeft: 4,
          }}
          title="Close"
        >
          ✕
        </button>
      </div>

      {/* 内容区 — Markdown 渲染 */}
      <div
        data-no-drag
        style={{
          flex: 1,
          overflow: "auto",
          padding: "10px 16px 14px",
          cursor: "default",
        }}
      >
        <div
          style={{
            background: "rgba(255,255,255,0.03)",
            borderRadius: 6,
            padding: "16px 10px 14px",
            fontSize,
            color: "rgba(255,255,255,0.85)",
            lineHeight: 1.7,
            whiteSpace: "pre-wrap",
            wordBreak: "break-word",
          }}
        >
          {markdown}
          {isStreaming && <span style={{ opacity: 0.5, animation: "blink 1s infinite" }}>▊</span>}
        </div>
      </div>

      {/* 右下角 resize grip */}
      <div
        data-no-drag
        style={{
          width: 16,
          height: 16,
          alignSelf: "flex-end",
          cursor: "se-resize",
          opacity: 0.3,
          fontSize: 9,
          textAlign: "center",
          lineHeight: "16px",
          color: "rgba(255,255,255,0.5)",
          flexShrink: 0,
        }}
      >
        ⋮⋮
      </div>
    </div>
  );
}
