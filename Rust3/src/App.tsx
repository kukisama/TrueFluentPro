import { useTranslation } from "react-i18next";
import { useThemeStore } from "./stores/theme-store";
import { useAppStore } from "./stores/app-store";
import { cn } from "./lib/utils";

function App() {
  const { t } = useTranslation();
  const resolved = useThemeStore((s) => s.resolved);
  const activeView = useAppStore((s) => s.activeView);

  return (
    <div
      className={cn(
        "min-h-screen",
        resolved === "dark"
          ? "dark bg-neutral-900 text-white"
          : "bg-white text-neutral-900",
      )}
    >
      <p>
        {t("app.name")} &mdash; {activeView}
      </p>
    </div>
  );
}

export default App;
