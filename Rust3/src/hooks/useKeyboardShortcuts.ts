import { useEffect } from "react";
import { useAppStore, type AppView } from "../stores/app-store";
import { api } from "../lib/api";

/**
 * Keyboard shortcut mapping (Ctrl+N → view navigation, F5/F6 → translation control).
 *
 * Mapping follows MainWindow.md § 键盘快捷键:
 *   Ctrl+1 = live-translation
 *   Ctrl+2 = audio-lab
 *   Ctrl+3 = batch-processing
 *   Ctrl+4 = media-studio
 *   Ctrl+5 / Ctrl+, = settings
 *   Ctrl+6 = media-center
 *   Ctrl+7 = task-monitor
 *   F5 = start realtime translation
 *   F6 = stop realtime translation
 */

const SHORTCUT_MAP: Record<string, AppView> = {
  "1": "live-translation",
  "2": "audio-lab",
  "3": "batch-processing",
  "4": "media-studio",
  "5": "settings",
  ",": "settings",
  "6": "media-center",
  "7": "task-monitor",
};

/** Returns true if event target is a text input element (prevents shortcut conflicts). */
function isInputFocused(): boolean {
  const el = document.activeElement;
  if (!el) return false;
  const tag = el.tagName.toLowerCase();
  if (tag === "input" || tag === "textarea" || tag === "select") return true;
  if ((el as HTMLElement).isContentEditable) return true;
  return false;
}

export function useKeyboardShortcuts() {
  const setActiveView = useAppStore((s) => s.setActiveView);
  const isTranslating = useAppStore((s) => s.isTranslating);
  const sessionId = useAppStore((s) => s.sessionId);

  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      // Skip when typing in input elements
      if (isInputFocused()) return;

      // Ctrl+N navigation shortcuts
      if (e.ctrlKey && !e.altKey && !e.shiftKey) {
        const view = SHORTCUT_MAP[e.key];
        if (view) {
          e.preventDefault();
          setActiveView(view);
          return;
        }
      }

      // F5: start realtime translation
      if (e.key === "F5" && !e.ctrlKey && !e.altKey && !e.shiftKey) {
        e.preventDefault();
        if (!isTranslating) {
          setActiveView("live-translation");
          // Dispatch custom event that LiveTranslationView can listen to
          window.dispatchEvent(new CustomEvent("tfp:start-translation"));
        }
        return;
      }

      // F6: stop realtime translation
      if (e.key === "F6" && !e.ctrlKey && !e.altKey && !e.shiftKey) {
        e.preventDefault();
        if (isTranslating && sessionId) {
          api.stopRealtimeTranslation(sessionId).catch(() => {});
          window.dispatchEvent(new CustomEvent("tfp:stop-translation"));
        }
        return;
      }
    };

    window.addEventListener("keydown", handler);
    return () => window.removeEventListener("keydown", handler);
  }, [setActiveView, isTranslating, sessionId]);
}
