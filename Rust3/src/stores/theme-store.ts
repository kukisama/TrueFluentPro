import { create } from "zustand";

export type ThemeMode = "system" | "light" | "dark";

interface ThemeState {
  mode: ThemeMode;
  resolved: "light" | "dark";
  fontSize: number;
  transitionDuration: number;
  setMode: (mode: ThemeMode) => void;
  setFontSize: (size: number) => void;
  setTransitionDuration: (ms: number) => void;
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

function applyFontSize(size: number) {
  document.documentElement.style.fontSize = `${size}px`;
}

const stored = localStorage.getItem("theme-mode") as ThemeMode | null;
const initialMode: ThemeMode = stored && CYCLE_ORDER.includes(stored) ? stored : "dark";
const initialResolved = resolveTheme(initialMode);
const storedFontSize = Number(localStorage.getItem("font-size")) || 14;
const storedTransitionDuration = Number(localStorage.getItem("transition-duration") ?? 200);
applyTheme(initialResolved);
applyFontSize(storedFontSize);

export const useThemeStore = create<ThemeState>((set) => ({
  mode: initialMode,
  resolved: initialResolved,
  fontSize: storedFontSize,
  transitionDuration: storedTransitionDuration,

  setMode: (mode) => {
    const resolved = resolveTheme(mode);
    applyTheme(resolved);
    localStorage.setItem("theme-mode", mode);
    set({ mode, resolved });
  },

  setFontSize: (size) => {
    const clamped = Math.min(20, Math.max(12, size));
    applyFontSize(clamped);
    localStorage.setItem("font-size", String(clamped));
    set({ fontSize: clamped });
  },

  setTransitionDuration: (ms) => {
    const clamped = Math.min(1000, Math.max(0, ms));
    localStorage.setItem("transition-duration", String(clamped));
    set({ transitionDuration: clamped });
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

window.matchMedia("(prefers-color-scheme: dark)").addEventListener("change", () => {
  const { mode } = useThemeStore.getState();
  if (mode === "system") {
    const resolved = resolveTheme("system");
    applyTheme(resolved);
    useThemeStore.setState({ resolved });
  }
});
