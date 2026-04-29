import { useTranslation } from "react-i18next";
import {
  Languages, Mic, Palette, Image, ListChecks, Settings, Info,
  PanelLeftClose, PanelLeftOpen, LogIn, X, Sun, Moon, Monitor,
  Minus, Maximize2,
} from "lucide-react";
import { AnimatePresence, motion } from "framer-motion";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { cn } from "../lib/utils";
import { Button, Separator, Tooltip, TooltipTrigger, TooltipContent } from "./ui";
import { useAppStore, type AppView } from "../stores/app-store";
import { useThemeStore, type ThemeMode } from "../stores/theme-store";
import { InfoBar } from "./InfoBar";
import { LiveTranslationView } from "../views/LiveTranslationView";
import { AboutView } from "../views/AboutView";
import { AuthView } from "../views/AuthView";
import { TaskMonitorView } from "../views/TaskMonitorView";
import { SettingsView } from "../views/SettingsView";
import { MediaStudioView } from "../views/MediaStudioView";
import { MediaCenterView } from "../views/MediaCenterView";
import { AudioLabView } from "../views/AudioLabView";

/* ── Placeholder for views not yet implemented ── */

// @ts-ignore: ViewPlaceholder retained for future use
function ViewPlaceholder({ name }: { name: string }) {
  return (
    <div className="flex items-center justify-center h-full text-[var(--text-muted)]">
      <p className="text-sm">{name} — Coming Soon</p>
    </div>
  );
}

interface NavItem {
  id: AppView;
  labelKey: string;
  icon: React.ReactNode;
  section?: string;
}

const NAV_ITEMS: NavItem[] = [
  { id: "live-translation", labelKey: "nav.liveTranslation", icon: <Languages size={18} />, section: "core" },
  { id: "media-studio", labelKey: "nav.mediaStudio", icon: <Palette size={18} />, section: "core" },
  { id: "media-center", labelKey: "nav.mediaCenter", icon: <Image size={18} />, section: "core" },
  { id: "audio-lab", labelKey: "nav.audioLab", icon: <Mic size={18} />, section: "core" },
  { id: "task-monitor", labelKey: "nav.taskMonitor", icon: <ListChecks size={18} />, section: "manage" },
  { id: "settings", labelKey: "nav.settings", icon: <Settings size={18} />, section: "system" },
  { id: "about", labelKey: "nav.about", icon: <Info size={18} />, section: "system" },
];

/** 需要保活的视图（切走后保留 DOM，保持 state） */
const KEEP_ALIVE_VIEWS: AppView[] = [
  "live-translation", "media-studio", "media-center", "audio-lab",
];

/** 不需要保活的视图（切走即销毁） */
const DISPOSABLE_VIEW_MAP: Partial<Record<AppView, React.ReactNode>> = {
  "task-monitor": <TaskMonitorView />,
  settings: <SettingsView />,
  about: <AboutView />,
  help: <AboutView />,
  auth: <AuthView />,
};

const KEEP_ALIVE_COMPONENTS: Record<string, React.FC> = {
  "live-translation": () => <LiveTranslationView />,
  "media-studio": MediaStudioView,
  "media-center": MediaCenterView,
  "audio-lab": AudioLabView,
};

const THEME_ICONS: Record<ThemeMode, React.ReactNode> = {
  system: <Monitor size={16} />,
  light: <Sun size={16} />,
  dark: <Moon size={16} />,
};

const THEME_LABEL_KEYS: Record<ThemeMode, string> = {
  system: "layout.themeSystem",
  light: "layout.themeLight",
  dark: "layout.themeDark",
};

const appWindow = getCurrentWindow();

/** Invisible resize edge zones for frameless window */
function ResizeEdges() {
  const EDGE = 5; // px
  const startResize = (dir: string) => (e: React.MouseEvent) => {
    e.preventDefault();
    appWindow.startResizeDragging(dir as any);
  };
  return (
    <>
      {/* Edges */}
      <div style={{ position: "fixed", top: 0, left: EDGE, right: EDGE, height: EDGE, cursor: "n-resize", zIndex: 9999 }} onMouseDown={startResize("North")} />
      <div style={{ position: "fixed", bottom: 0, left: EDGE, right: EDGE, height: EDGE, cursor: "s-resize", zIndex: 9999 }} onMouseDown={startResize("South")} />
      <div style={{ position: "fixed", top: EDGE, left: 0, bottom: EDGE, width: EDGE, cursor: "w-resize", zIndex: 9999 }} onMouseDown={startResize("West")} />
      <div style={{ position: "fixed", top: EDGE, right: 0, bottom: EDGE, width: EDGE, cursor: "e-resize", zIndex: 9999 }} onMouseDown={startResize("East")} />
      {/* Corners */}
      <div style={{ position: "fixed", top: 0, left: 0, width: EDGE, height: EDGE, cursor: "nw-resize", zIndex: 10000 }} onMouseDown={startResize("NorthWest")} />
      <div style={{ position: "fixed", top: 0, right: 0, width: EDGE, height: EDGE, cursor: "ne-resize", zIndex: 10000 }} onMouseDown={startResize("NorthEast")} />
      <div style={{ position: "fixed", bottom: 0, left: 0, width: EDGE, height: EDGE, cursor: "sw-resize", zIndex: 10000 }} onMouseDown={startResize("SouthWest")} />
      <div style={{ position: "fixed", bottom: 0, right: 0, width: EDGE, height: EDGE, cursor: "se-resize", zIndex: 10000 }} onMouseDown={startResize("SouthEast")} />
    </>
  );
}

/** 窗口控制按钮（最小化/最大化/关闭），嵌入右上角 */
function WindowControls() {
  return (
    <div className="flex items-center h-9 shrink-0 z-50">
      <button
        onClick={() => appWindow.minimize()}
        className="h-full w-10 flex items-center justify-center text-[var(--text-muted)] hover:bg-[var(--hover-bg)] hover:text-[var(--text-primary)] transition-colors"
      >
        <Minus size={14} />
      </button>
      <button
        onClick={() => appWindow.toggleMaximize()}
        className="h-full w-10 flex items-center justify-center text-[var(--text-muted)] hover:bg-[var(--hover-bg)] hover:text-[var(--text-primary)] transition-colors"
      >
        <Maximize2 size={12} />
      </button>
      <button
        onClick={() => appWindow.close()}
        className="h-full w-10 flex items-center justify-center text-[var(--text-muted)] hover:bg-red-500/80 hover:text-white transition-colors rounded-tr-none"
      >
        <X size={14} />
      </button>
    </div>
  );
}

export function AppLayout() {
  const { t } = useTranslation();
  const activeView = useAppStore((s) => s.activeView);
  const setActiveView = useAppStore((s) => s.setActiveView);
  const collapsed = useAppStore((s) => s.sidebarCollapsed);
  const toggleSidebar = useAppStore((s) => s.toggleSidebar);
  const error = useAppStore((s) => s.error);
  const setError = useAppStore((s) => s.setError);

  const themeMode = useThemeStore((s) => s.mode);
  const cycleTheme = useThemeStore((s) => s.cycleTheme);

  return (
    <div className="flex h-screen w-screen overflow-hidden" style={{ backgroundColor: "var(--surface-0)" }}>
      <ResizeEdges />
      {/* ── Sidebar ── */}
      <aside
        className={cn(
          "flex flex-col border-r border-[var(--border-subtle)] transition-all duration-300 ease-out",
          collapsed ? "w-[56px]" : "w-[200px]",
        )}
        style={{ backgroundColor: "var(--sidebar-bg)" }}
      >
        {/* Logo / 折叠 — 同时作为窗口拖拽区域 */}
        <div data-tauri-drag-region className="flex items-center h-12 px-3 border-b border-[var(--border-subtle)] select-none">
          {!collapsed && (
            <span className="text-sm font-bold text-gradient whitespace-nowrap mr-auto tracking-wide">
              {t("app.name")}
            </span>
          )}
          <Button variant="ghost" size="icon" onClick={toggleSidebar} className="ml-auto h-7 w-7">
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
                    ? "bg-[var(--active-bg)] text-[var(--active-text)] shadow-sm"
                    : "text-[var(--text-muted)] hover:bg-[var(--hover-bg)] hover:text-[var(--text-secondary)]",
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

        {/* 底部：主题切换 + 账户 */}
        <div className="border-t border-[var(--border-subtle)] p-1.5 space-y-0.5">
          {/* 主题切换按钮 */}
          {collapsed ? (
            <Tooltip>
              <TooltipTrigger asChild>
                <button
                  onClick={cycleTheme}
                  className="flex items-center gap-2.5 w-full rounded-xl px-2.5 py-2 text-sm transition-all duration-200 text-[var(--text-muted)] hover:bg-[var(--hover-bg)] hover:text-[var(--text-secondary)]"
                >
                  {THEME_ICONS[themeMode]}
                </button>
              </TooltipTrigger>
              <TooltipContent side="right">{t(THEME_LABEL_KEYS[themeMode])}</TooltipContent>
            </Tooltip>
          ) : (
            <button
              onClick={cycleTheme}
              className="flex items-center gap-2.5 w-full rounded-xl px-2.5 py-2 text-sm transition-all duration-200 text-[var(--text-muted)] hover:bg-[var(--hover-bg)] hover:text-[var(--text-secondary)]"
            >
              {THEME_ICONS[themeMode]}
              <span>{t(THEME_LABEL_KEYS[themeMode])}</span>
            </button>
          )}

          {/* 账户 */}
          {collapsed ? (
            <Tooltip>
              <TooltipTrigger asChild>
                <button
                  onClick={() => setActiveView("auth")}
                  className={cn(
                    "flex items-center gap-2.5 w-full rounded-xl px-2.5 py-2 text-sm transition-all duration-200",
                    activeView === "auth"
                      ? "bg-[var(--active-bg)] text-[var(--active-text)]"
                      : "text-[var(--text-muted)] hover:bg-[var(--hover-bg)] hover:text-[var(--text-secondary)]",
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
                  ? "bg-[var(--active-bg)] text-[var(--active-text)]"
                  : "text-[var(--text-muted)] hover:bg-[var(--hover-bg)] hover:text-[var(--text-secondary)]",
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
        {/* 顶部拖拽区 + 窗口按钮 */}
        <div data-tauri-drag-region className="flex items-center shrink-0 select-none h-9 border-b border-[var(--border-subtle)]"
          style={{ backgroundColor: "var(--surface-0)" }}>
          <div data-tauri-drag-region className="flex-1 h-full" />
          <WindowControls />
        </div>

        {/* 错误横幅 */}
        <AnimatePresence>
          {error && (
            <motion.div
              initial={{ height: 0, opacity: 0 }}
              animate={{ height: "auto", opacity: 1 }}
              exit={{ height: 0, opacity: 0 }}
              className="overflow-hidden"
            >
              <div className="bg-red-500/10 border-b border-red-500/20 px-4 py-2 text-sm text-red-400 flex items-center justify-between backdrop-blur-sm">
                <span>{error}</span>
                <Button variant="ghost" size="icon" className="h-6 w-6" onClick={() => setError(null)}>
                  <X size={14} />
                </Button>
              </div>
            </motion.div>
          )}
        </AnimatePresence>

        {/* InfoBar 通知横幅 */}
        <InfoBar />

        {/* 内容区 */}
        <main className="flex-1 overflow-y-auto overflow-x-hidden relative">
          {/* 保活视图：始终 mounted，通过 display 控制可见性 */}
          {KEEP_ALIVE_VIEWS.map((viewId) => {
            const Comp = KEEP_ALIVE_COMPONENTS[viewId];
            return (
              <div
                key={viewId}
                className="h-full"
                style={{ display: activeView === viewId ? "block" : "none" }}
              >
                <Comp />
              </div>
            );
          })}
          {/* 非保活视图：正常挂载/卸载 */}
          {!KEEP_ALIVE_VIEWS.includes(activeView) && (
            <div className="h-full">
              {DISPOSABLE_VIEW_MAP[activeView]}
            </div>
          )}
        </main>
      </div>
    </div>
  );
}
