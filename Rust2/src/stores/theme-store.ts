import { create } from "zustand";

export type ThemeMode = "system" | "light" | "dark";

interface ThemeState {
  mode: ThemeMode;
  resolved: "light" | "dark";
  setMode: (mode: ThemeMode) => void;
  cycleTheme: () => void;
}

const CYCLE_ORDER: ThemeMode[] = ["system", "light", "dark"];

function resolveTheme(mode: ThemeMode): "light" | "dark" {
  if (mode === "system") {
    return window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
  }
  return mode;
}

function applyTheme(resolved: "light" | "dark") {
  const root = document.documentElement;
  if (resolved === "light") {
    root.classList.add("light");
    root.classList.remove("dark");
  } else {
    root.classList.add("dark");
    root.classList.remove("light");
  }
}

// 从 localStorage 读取已保存的主题偏好
const stored = localStorage.getItem("theme-mode") as ThemeMode | null;
const initialMode: ThemeMode = stored && CYCLE_ORDER.includes(stored) ? stored : "dark";
const initialResolved = resolveTheme(initialMode);
applyTheme(initialResolved);

export const useThemeStore = create<ThemeState>((set) => ({
  mode: initialMode,
  resolved: initialResolved,

  setMode: (mode) => {
    const resolved = resolveTheme(mode);
    applyTheme(resolved);
    localStorage.setItem("theme-mode", mode);
    set({ mode, resolved });
  },

  cycleTheme: () => {
    set((s) => {
      const idx = CYCLE_ORDER.indexOf(s.mode);
      const next = CYCLE_ORDER[(idx + 1) % CYCLE_ORDER.length];
      const resolved = resolveTheme(next);
      applyTheme(resolved);
      localStorage.setItem("theme-mode", next);
      return { mode: next, resolved };
    });
  },
}));

// 监听系统主题变化
window.matchMedia("(prefers-color-scheme: dark)").addEventListener("change", () => {
  const { mode } = useThemeStore.getState();
  if (mode === "system") {
    const resolved = resolveTheme("system");
    applyTheme(resolved);
    useThemeStore.setState({ resolved });
  }
});
