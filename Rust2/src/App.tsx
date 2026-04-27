import { useEffect } from "react";
import "./lib/i18n";
import { TooltipProvider } from "./components/ui";
import { AppLayout } from "./components/AppLayout";
import { useAppStore, type AppView } from "./stores/app-store";
import { api } from "./lib/tauri-api";

const SHORTCUT_MAP: Record<string, AppView> = {
  "1": "live-translation",
  "2": "media-studio",
  "3": "media-center",
  "4": "audio-lab",
  "5": "task-monitor",
  ",": "settings",
};

export default function App() {
  const setConfig = useAppStore((s) => s.setConfig);
  const setProviders = useAppStore((s) => s.setProviders);
  const setError = useAppStore((s) => s.setError);
  const setActiveView = useAppStore((s) => s.setActiveView);

  useEffect(() => {
    (async () => {
      try {
        const [config, providers] = await Promise.all([
          api.getConfig(),
          api.listProviders(),
        ]);
        setConfig(config);
        setProviders(providers);
      } catch (e) {
        setError(String(e));
      }
    })();
  }, [setConfig, setProviders, setError]);

  // 全局快捷键: Ctrl+1~5 切换视图, Ctrl+, 打开设置
  // O-39: F5 开始翻译, F6 停止翻译
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if (e.ctrlKey && !e.altKey && !e.shiftKey) {
        const view = SHORTCUT_MAP[e.key];
        if (view) {
          e.preventDefault();
          setActiveView(view);
        }
      }
      // O-39: F5/F6 — 对齐 C# MainWindow F5=切换到翻译视图, F6=停止翻译
      // F5 切换到翻译视图（由视图组件管理实际开始逻辑）
      if (e.key === "F5") {
        e.preventDefault();
        setActiveView("live-translation");
      }
      // F6 派发自定义事件，由 LiveTranslationView 监听并停止
      if (e.key === "F6") {
        e.preventDefault();
        window.dispatchEvent(new CustomEvent("tfp:stop-translation"));
      }
    };
    window.addEventListener("keydown", handler);
    return () => window.removeEventListener("keydown", handler);
  }, [setActiveView]);

  return (
    <TooltipProvider delayDuration={100} skipDelayDuration={50}>
      <AppLayout />
    </TooltipProvider>
  );
}
