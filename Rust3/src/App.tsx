import { useEffect } from "react";
import "./lib/i18n";
import { TooltipProvider } from "./components/ui";
import { AppLayout } from "./components/AppLayout";
import { useAppStore } from "./stores/app-store";
import { useKeyboardShortcuts } from "./hooks/useKeyboardShortcuts";
import { api } from "./lib/api";

export default function App() {
  const setConfig = useAppStore((s) => s.setConfig);
  const setProviders = useAppStore((s) => s.setProviders);
  const setError = useAppStore((s) => s.setError);
  const setActiveView = useAppStore((s) => s.setActiveView);

  // Load config and providers on startup; restore last active view
  useEffect(() => {
    (async () => {
      try {
        const [config, providers] = await Promise.all([
          api.getConfig(),
          api.listProviders(),
        ]);
        setConfig(config);
        setProviders(providers);
        // Restore last active view from persisted config
        if (config.ui?.last_active_view) {
          setActiveView(config.ui.last_active_view as any);
        }
      } catch (e) {
        setError(String(e));
      }
    })();
  }, [setConfig, setProviders, setError, setActiveView]);

  // Register keyboard shortcuts
  useKeyboardShortcuts();

  return (
    <TooltipProvider delayDuration={200}>
      <AppLayout />
    </TooltipProvider>
  );
}
