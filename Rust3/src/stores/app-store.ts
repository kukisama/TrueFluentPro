import { create } from "zustand";
import type { AppConfig, ProviderInfo, TranslationHistory } from "../lib/types";

export type AppView =
  | "live-translation"
  | "media-studio"
  | "media-center"
  | "audio-lab"
  | "task-monitor"
  | "settings"
  | "about"
  | "help"
  | "auth";

interface AppState {
  // Navigation
  activeView: AppView;
  sidebarCollapsed: boolean;
  setActiveView: (view: AppView) => void;
  toggleSidebar: () => void;

  // Config
  config: AppConfig | null;
  setConfig: (config: AppConfig) => void;

  // Provider
  providers: ProviderInfo[];
  setProviders: (p: ProviderInfo[]) => void;

  // Realtime translation
  isTranslating: boolean;
  sessionId: string | null;
  recognizedSegments: { source: string; translation: string; time: string }[];
  setTranslating: (v: boolean) => void;
  setSessionId: (id: string | null) => void;
  addSegment: (seg: { source: string; translation: string; time: string }) => void;
  clearSegments: () => void;

  // AI streaming
  streamingContent: string;
  isStreaming: boolean;
  appendStreamToken: (token: string) => void;
  clearStream: () => void;
  setStreaming: (v: boolean) => void;

  // InfoBar
  infoBarOpen: boolean;
  infoBarMessage: string;
  infoBarSeverity: "info" | "warning" | "error" | "success";
  showInfoBar: (message: string, severity?: "info" | "warning" | "error" | "success") => void;
  hideInfoBar: () => void;

  // Translation history
  history: TranslationHistory[];
  setHistory: (h: TranslationHistory[]) => void;

  // Global loading / error
  loading: boolean;
  error: string | null;
  setLoading: (v: boolean) => void;
  setError: (e: string | null) => void;
}

export const useAppStore = create<AppState>((set) => ({
  // Navigation
  activeView: "media-studio",
  sidebarCollapsed: false,
  setActiveView: (view) => set({ activeView: view }),
  toggleSidebar: () => set((s) => ({ sidebarCollapsed: !s.sidebarCollapsed })),

  // Config
  config: null,
  setConfig: (config) => set({ config }),

  // Provider
  providers: [],
  setProviders: (providers) => set({ providers }),

  // Realtime translation
  isTranslating: false,
  sessionId: null,
  recognizedSegments: [],
  setTranslating: (v) => set({ isTranslating: v }),
  setSessionId: (id) => set({ sessionId: id }),
  addSegment: (seg) =>
    set((s) => ({ recognizedSegments: [...s.recognizedSegments, seg] })),
  clearSegments: () => set({ recognizedSegments: [] }),

  // AI streaming
  streamingContent: "",
  isStreaming: false,
  appendStreamToken: (token) =>
    set((s) => ({ streamingContent: s.streamingContent + token })),
  clearStream: () => set({ streamingContent: "", isStreaming: false }),
  setStreaming: (v) => set({ isStreaming: v }),

  // InfoBar
  infoBarOpen: false,
  infoBarMessage: "",
  infoBarSeverity: "info",
  showInfoBar: (message, severity = "info") =>
    set({ infoBarOpen: true, infoBarMessage: message, infoBarSeverity: severity }),
  hideInfoBar: () => set({ infoBarOpen: false }),

  // History
  history: [],
  setHistory: (h) => set({ history: h }),

  // Global
  loading: false,
  error: null,
  setLoading: (v) => set({ loading: v }),
  setError: (e) => set({ error: e }),
}));
