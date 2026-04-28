import { useState, useEffect, useCallback } from "react";
import { listen } from "@tauri-apps/api/event";
import { getCurrentWindow } from "@tauri-apps/api/window";

/**
 * 浮动字幕子窗口 — 1:1 对照 C# FloatingSubtitleWindow.axaml
 *
 * 窗口属性：transparent, no decorations, always_on_top, skip_taskbar
 * 由 Rust 后端 WebviewWindowBuilder 创建。
 *
 * 通信路径：
 *   Rust 后端 emit "subtitle-update" → 本窗口 listen
 *   不与主窗口 webview 直连。
 */

interface SubtitlePayload {
  source_text: string;
  translated_text: string;
  source_label: string;
}

const FONT_SIZES = [24, 28, 32, 36, 40, 44, 48, 50];

export function FloatingSubtitleApp() {
  const [sourceText, setSourceText] = useState("等待字幕内容...");
  const [translatedText, setTranslatedText] = useState("");
  const [sourceLabel, setSourceLabel] = useState("🔊 全部字幕");
  const [fontSize, setFontSize] = useState(36);
  const [bgMode, setBgMode] = useState(0); // 0=半透明黑, 1=纯黑, 2=透明

  // 订阅后端 subtitle-update 事件
  useEffect(() => {
    const unlisten = listen<SubtitlePayload>("subtitle-update", (event) => {
      const p = event.payload;
      if (p.source_text) setSourceText(p.source_text);
      if (p.translated_text !== undefined) setTranslatedText(p.translated_text);
      if (p.source_label) setSourceLabel(p.source_label);
    });
    return () => { unlisten.then(fn => fn()); };
  }, []);

  // 窗口拖动
  const handleMouseDown = useCallback(async (e: React.MouseEvent) => {
    if ((e.target as HTMLElement).closest("[data-no-drag]")) return;
    try {
      await getCurrentWindow().startDragging();
    } catch { /* 非主线程或不支持 */ }
  }, []);

  // 关闭子窗口
  const handleClose = useCallback(async () => {
    try {
      await getCurrentWindow().close();
    } catch { /* best-effort */ }
  }, []);

  const bgStyles: Record<number, string> = {
    0: "rgba(0,0,0,0.75)",
    1: "rgba(0,0,0,0.95)",
    2: "transparent",
  };

  const borderColor = "rgba(255,255,255,0.25)";

  return (
    <div
      style={{
        width: "100%",
        height: "100%",
        background: bgStyles[bgMode] || bgStyles[0],
        borderRadius: 8,
        border: `2px solid ${borderColor}`,
        overflow: "hidden",
        cursor: "grab",
        display: "flex",
        flexDirection: "column",
      }}
      onMouseDown={handleMouseDown}
    >
      {/* 顶部控制栏 */}
      <div
        data-no-drag
        style={{
          display: "flex",
          alignItems: "center",
          gap: 6,
          padding: "4px 10px",
          background: "rgba(0,0,0,0.3)",
          cursor: "default",
          flexShrink: 0,
        }}
      >
        <span style={{ fontSize: 11, color: "rgba(255,255,255,0.6)", marginRight: 8 }}>
          {sourceLabel}
        </span>

        {/* 字号选择 */}
        <select
          data-no-drag
          value={fontSize}
          onChange={(e) => setFontSize(Number(e.target.value))}
          style={{
            background: "rgba(255,255,255,0.1)",
            color: "white",
            border: "1px solid rgba(255,255,255,0.2)",
            borderRadius: 4,
            fontSize: 11,
            padding: "1px 4px",
            cursor: "pointer",
          }}
        >
          {FONT_SIZES.map(s => <option key={s} value={s}>{s}px</option>)}
        </select>

        {/* 背景切换 */}
        <button
          data-no-drag
          onClick={() => setBgMode((bgMode + 1) % 3)}
          style={{
            background: "rgba(255,255,255,0.1)",
            color: "white",
            border: "1px solid rgba(255,255,255,0.2)",
            borderRadius: 4,
            fontSize: 11,
            padding: "1px 6px",
            cursor: "pointer",
          }}
          title="切换背景"
        >
          🎨
        </button>

        <div style={{ flex: 1 }} />

        {/* 关闭按钮 */}
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
          }}
          title="关闭字幕窗"
        >
          ×
        </button>
      </div>

      {/* 字幕内容区 */}
      <div
        style={{
          flex: 1,
          display: "flex",
          flexDirection: "column",
          justifyContent: "center",
          alignItems: "center",
          padding: "4px 16px",
          overflow: "hidden",
        }}
      >
        <div
          style={{
            fontSize,
            fontWeight: "bold",
            color: "white",
            textAlign: "center",
            whiteSpace: "nowrap",
            overflow: "hidden",
            textOverflow: "ellipsis",
            maxWidth: "100%",
            lineHeight: 1.2,
          }}
        >
          {translatedText || sourceText}
        </div>
        {translatedText && (
          <div
            style={{
              fontSize: Math.max(14, fontSize * 0.45),
              color: "rgba(255,255,255,0.5)",
              textAlign: "center",
              whiteSpace: "nowrap",
              overflow: "hidden",
              textOverflow: "ellipsis",
              maxWidth: "100%",
              marginTop: 2,
            }}
          >
            {sourceText}
          </div>
        )}
      </div>
    </div>
  );
}
