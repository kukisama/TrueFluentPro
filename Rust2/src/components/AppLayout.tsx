import { useTranslation } from "react-i18next";
import {
  Languages,
  Layers,
  Mic,
  Palette,
  ListChecks,
  Settings,
  Info,
  PanelLeftClose,
  PanelLeftOpen,
  LogIn,
  X,
} from "lucide-react";
import { AnimatePresence, motion } from "framer-motion";
import { cn } from "../lib/utils";
import { Button, Separator, Tooltip, TooltipTrigger, TooltipContent } from "./ui";
import { useAppStore, type AppView } from "../stores/app-store";

import { LiveTranslationView } from "../views/LiveTranslationView";
import { BatchProcessingView } from "../views/BatchProcessingView";
import { AudioLabView } from "../views/AudioLabView";
import { MediaStudioView } from "../views/MediaStudioView";
import { TaskMonitorView } from "../views/TaskMonitorView";
import { SettingsView } from "../views/SettingsView";
import { AboutView } from "../views/AboutView";
import { AuthView } from "../views/AuthView";

interface NavItem {
  id: AppView;
  labelKey: string;
  icon: React.ReactNode;
  section?: string;
}

const NAV_ITEMS: NavItem[] = [
  { id: "live-translation", labelKey: "nav.liveTranslation", icon: <Languages size={18} />, section: "core" },
  { id: "batch-processing", labelKey: "nav.batchProcessing", icon: <Layers size={18} />, section: "core" },
  { id: "audio-lab", labelKey: "nav.audioLab", icon: <Mic size={18} />, section: "core" },
  { id: "media-studio", labelKey: "nav.mediaStudio", icon: <Palette size={18} />, section: "core" },
  { id: "task-monitor", labelKey: "nav.taskMonitor", icon: <ListChecks size={18} />, section: "manage" },
  { id: "settings", labelKey: "nav.settings", icon: <Settings size={18} />, section: "system" },
  { id: "about", labelKey: "nav.about", icon: <Info size={18} />, section: "system" },
];

const VIEW_MAP: Record<AppView, React.ReactNode> = {
  "live-translation": <LiveTranslationView />,
  "batch-processing": <BatchProcessingView />,
  "audio-lab": <AudioLabView />,
  "media-studio": <MediaStudioView />,
  "task-monitor": <TaskMonitorView />,
  settings: <SettingsView />,
  about: <AboutView />,
  help: <AboutView />,
  auth: <AuthView />,
};

export function AppLayout() {
  const { t } = useTranslation();
  const activeView = useAppStore((s) => s.activeView);
  const setActiveView = useAppStore((s) => s.setActiveView);
  const collapsed = useAppStore((s) => s.sidebarCollapsed);
  const toggleSidebar = useAppStore((s) => s.toggleSidebar);
  const isTranslating = useAppStore((s) => s.isTranslating);
  const error = useAppStore((s) => s.error);
  const setError = useAppStore((s) => s.setError);

  return (
    <div className="flex h-screen w-screen overflow-hidden bg-surface-0">
      {/* ── Sidebar ── */}
      <aside
        className={cn(
          "flex flex-col border-r border-white/[0.06] bg-white/[0.02] backdrop-blur-md transition-all duration-300 ease-out",
          collapsed ? "w-[56px]" : "w-[200px]",
        )}
      >
        {/* Logo / 折叠 */}
        <div className="flex items-center h-12 px-3 border-b border-white/[0.06]">
          {!collapsed && (
            <span className="text-sm font-bold text-gradient whitespace-nowrap mr-auto tracking-wide">
              {t("app.name")}
            </span>
          )}
          <Button
            variant="ghost"
            size="icon"
            onClick={toggleSidebar}
            className="ml-auto h-7 w-7"
          >
            {collapsed ? <PanelLeftOpen size={16} /> : <PanelLeftClose size={16} />}
          </Button>
        </div>

        {/* 导航 */}
        <nav className="flex-1 overflow-y-auto py-2 px-1.5 space-y-0.5">
          {NAV_ITEMS.map((item, i) => {
            const prev = NAV_ITEMS[i - 1];
            const showDivider = prev && prev.section !== item.section;
            const isActive = activeView === item.id;
            const label = t(item.labelKey);

            const btn = (
              <button
                onClick={() => setActiveView(item.id)}
                className={cn(
                  "flex items-center gap-2.5 w-full rounded-xl px-2.5 py-2 text-sm transition-all duration-200",
                  isActive
                    ? "bg-brand-600/15 text-brand-300 shadow-sm shadow-brand-600/5"
                    : "text-slate-500 hover:bg-white/[0.04] hover:text-slate-300",
                )}
              >
                <span className="shrink-0">{item.icon}</span>
                {!collapsed && <span className="truncate">{label}</span>}
              </button>
            );

            return (
              <div key={item.id}>
                {showDivider && <Separator className="!my-2 mx-1 opacity-50" />}
                {collapsed ? (
                  <Tooltip>
                    <TooltipTrigger asChild>{btn}</TooltipTrigger>
                    <TooltipContent side="right">{label}</TooltipContent>
                  </Tooltip>
                ) : (
                  btn
                )}
              </div>
            );
          })}
        </nav>

        {/* 底部：账户 */}
        <div className="border-t border-white/[0.06] p-1.5">
          {collapsed ? (
            <Tooltip>
              <TooltipTrigger asChild>
                <button
                  onClick={() => setActiveView("auth")}
                  className={cn(
                    "flex items-center gap-2.5 w-full rounded-xl px-2.5 py-2 text-sm transition-all duration-200",
                    activeView === "auth"
                      ? "bg-brand-600/15 text-brand-300"
                      : "text-slate-500 hover:bg-white/[0.04] hover:text-slate-300",
                  )}
                >
                  <LogIn size={18} />
                </button>
              </TooltipTrigger>
              <TooltipContent side="right">{t("nav.account")}</TooltipContent>
            </Tooltip>
          ) : (
            <button
              onClick={() => setActiveView("auth")}
              className={cn(
                "flex items-center gap-2.5 w-full rounded-xl px-2.5 py-2 text-sm transition-all duration-200",
                activeView === "auth"
                  ? "bg-brand-600/15 text-brand-300"
                  : "text-slate-500 hover:bg-white/[0.04] hover:text-slate-300",
              )}
            >
              <LogIn size={18} />
              <span>{t("nav.account")}</span>
            </button>
          )}
        </div>
      </aside>

      {/* ── Main ── */}
      <div className="flex flex-col flex-1 min-w-0">
        {/* 错误横幅 */}
        <AnimatePresence>
          {error && (
            <motion.div
              initial={{ height: 0, opacity: 0 }}
              animate={{ height: "auto", opacity: 1 }}
              exit={{ height: 0, opacity: 0 }}
              className="overflow-hidden"
            >
              <div className="bg-red-500/10 border-b border-red-500/20 px-4 py-2 text-sm text-red-300 flex items-center justify-between backdrop-blur-sm">
                <span>{error}</span>
                <Button variant="ghost" size="icon" className="h-6 w-6" onClick={() => setError(null)}>
                  <X size={14} />
                </Button>
              </div>
            </motion.div>
          )}
        </AnimatePresence>

        {/* 内容区 */}
        <main className="flex-1 overflow-y-auto overflow-x-hidden">
          <AnimatePresence mode="wait">
            <motion.div
              key={activeView}
              initial={{ opacity: 0, y: 6 }}
              animate={{ opacity: 1, y: 0 }}
              exit={{ opacity: 0, y: -6 }}
              transition={{ duration: 0.2, ease: "easeOut" }}
              className="h-full"
            >
              {VIEW_MAP[activeView]}
            </motion.div>
          </AnimatePresence>
        </main>

        {/* Status Bar */}
        <footer className="h-7 bg-white/[0.02] border-t border-white/[0.06] flex items-center px-3 text-xs text-slate-600 gap-4 shrink-0">
          <span className="flex items-center gap-1.5">
            {isTranslating ? (
              <>
                <span className="w-1.5 h-1.5 rounded-full bg-emerald-400 animate-pulse" />
                {t("status.translating")}
              </>
            ) : (
              <>
                <span className="w-1.5 h-1.5 rounded-full bg-slate-600" />
                {t("status.ready")}
              </>
            )}
          </span>
          <span className="ml-auto">{t("app.version")}</span>
        </footer>
      </div>
    </div>
  );
}
