import { useEffect } from "react";
import "./lib/i18n";
import { TooltipProvider } from "./components/ui";
import { AppLayout } from "./components/AppLayout";
import { useAppStore } from "./stores/app-store";
import { api } from "./lib/tauri-api";

export default function App() {
  const setConfig = useAppStore((s) => s.setConfig);
  const setProviders = useAppStore((s) => s.setProviders);
  const setError = useAppStore((s) => s.setError);

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

  return (
    <TooltipProvider delayDuration={300}>
      <AppLayout />
    </TooltipProvider>
  );
}
