import { create } from "zustand";
import type { AppConfig, BatchTask, ProviderInfo, TranslationHistory } from "../lib/tauri-api";

export type AppView =
  | "live-translation"
  | "batch-processing"
  | "audio-lab"
  | "media-studio"
  | "task-monitor"
  | "settings"
  | "about"
  | "help"
  | "auth";

interface AppState {
  // 导航
  activeView: AppView;
  sidebarCollapsed: boolean;
  setActiveView: (view: AppView) => void;
  toggleSidebar: () => void;

  // 配置
  config: AppConfig | null;
  setConfig: (config: AppConfig) => void;

  // Provider
  providers: ProviderInfo[];
  setProviders: (p: ProviderInfo[]) => void;

  // 实时翻译状态
  isTranslating: boolean;
  sessionId: string | null;
  recognizedSegments: { source: string; translation: string; time: string }[];
  setTranslating: (v: boolean) => void;
  setSessionId: (id: string | null) => void;
  addSegment: (seg: { source: string; translation: string; time: string }) => void;
  clearSegments: () => void;

  // AI 流式补全
  streamingContent: string;
  isStreaming: boolean;
  appendStreamToken: (token: string) => void;
  clearStream: () => void;
  setStreaming: (v: boolean) => void;

  // 批量任务
  batchTasks: BatchTask[];
  setBatchTasks: (tasks: BatchTask[]) => void;

  // 翻译历史
  history: TranslationHistory[];
  setHistory: (h: TranslationHistory[]) => void;

  // 全局加载/错误
  loading: boolean;
  error: string | null;
  setLoading: (v: boolean) => void;
  setError: (e: string | null) => void;
}

export const useAppStore = create<AppState>((set) => ({
  // 导航
  activeView: "media-studio",
  sidebarCollapsed: false,
  setActiveView: (view) => set({ activeView: view }),
  toggleSidebar: () => set((s) => ({ sidebarCollapsed: !s.sidebarCollapsed })),

  // 配置
  config: null,
  setConfig: (config) => set({ config }),

  // Provider
  providers: [],
  setProviders: (providers) => set({ providers }),

  // 实时翻译
  isTranslating: false,
  sessionId: null,
  recognizedSegments: [],
  setTranslating: (v) => set({ isTranslating: v }),
  setSessionId: (id) => set({ sessionId: id }),
  addSegment: (seg) =>
    set((s) => ({ recognizedSegments: [...s.recognizedSegments, seg] })),
  clearSegments: () => set({ recognizedSegments: [] }),

  // AI 流式
  streamingContent: "",
  isStreaming: false,
  appendStreamToken: (token) =>
    set((s) => ({ streamingContent: s.streamingContent + token })),
  clearStream: () => set({ streamingContent: "", isStreaming: false }),
  setStreaming: (v) => set({ isStreaming: v }),

  // 批量任务
  batchTasks: [],
  setBatchTasks: (tasks) => set({ batchTasks: tasks }),

  // 历史
  history: [],
  setHistory: (h) => set({ history: h }),

  // 全局
  loading: false,
  error: null,
  setLoading: (v) => set({ loading: v }),
  setError: (e) => set({ error: e }),
}));
