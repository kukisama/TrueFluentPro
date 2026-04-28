import { create } from "zustand";
import type {
  AudioFile,
  AudioLabBundle,
  AudioLabTabKind,
  AudioStagePreset,
  StudioTask,
} from "../lib/tauri-api";
import { api } from "../lib/tauri-api";

/* ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
   听析中心 Zustand Store
   对齐 C# AudioLabViewModel 核心字段
   ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━ */

/** 播放状态 */
export interface PlaybackState {
  positionMs: number;
  isPlaying: boolean;
  speed: number;
  durationMs: number;
  playbackPath: string;
}

/** 说话人颜色（对齐 C# SpeakerColors） */
export const SPEAKER_COLORS = [
  "#4A90D9", // 蓝
  "#E8913A", // 橙
  "#50B86C", // 绿
  "#9B59B6", // 紫
  "#E74C3C", // 红
  "#F1C40F", // 黄
];

export function speakerColor(index: number): string {
  return SPEAKER_COLORS[index % SPEAKER_COLORS.length];
}

interface AudioLabState {
  // ── 文件库 ──
  files: AudioFile[];
  filesLoading: boolean;
  isFilePanelOpen: boolean;

  // ── 当前活跃 ──
  activeFileId: string | null;
  activeSessionId: string | null;

  // ── Bundle 缓存（LRU 限 3） ──
  loadedBundles: Map<string, AudioLabBundle>;

  // ── Tab ──
  selectedTab: AudioLabTabKind;

  // ── 播放器 ──
  playback: PlaybackState;

  // ── 运行中任务 ──
  runningTasks: StudioTask[];

  // ── 阶段预设 ──
  stagePresets: AudioStagePreset[];

  // ── 编辑模式 ──
  editingStageKey: string | null;
  editText: string;

  // ── 自定义阶段选中 key ──
  customStageKey: string | null;

  // ── Actions ──
  loadFiles: () => Promise<void>;
  setFilePanelOpen: (v: boolean) => void;
  selectFile: (fileId: string, sessionId: string) => Promise<void>;
  setSelectedTab: (tab: AudioLabTabKind) => void;
  updatePlayback: (partial: Partial<PlaybackState>) => void;
  loadRunningTasks: (sessionId: string) => Promise<void>;
  loadStagePresets: () => Promise<void>;
  refreshBundle: (sessionId: string) => Promise<void>;
  setEditingStage: (stageKey: string | null, text?: string) => void;
  setCustomStageKey: (key: string | null) => void;

  // ── Bundle 访问器 ──
  currentBundle: () => AudioLabBundle | undefined;
}

/** LRU 限 3 的 Map 插入 */
function lruSet<K, V>(map: Map<K, V>, key: K, val: V, max: number) {
  if (map.has(key)) map.delete(key);
  map.set(key, val);
  if (map.size > max) {
    const oldest = map.keys().next().value;
    if (oldest !== undefined) map.delete(oldest);
  }
}

export const useAudioLabStore = create<AudioLabState>((set, get) => ({
  // ── 初始值 ──
  files: [],
  filesLoading: false,
  isFilePanelOpen: true,

  activeFileId: null,
  activeSessionId: null,

  loadedBundles: new Map(),

  selectedTab: "Transcript",

  playback: {
    positionMs: 0,
    isPlaying: false,
    speed: 1,
    durationMs: 0,
    playbackPath: "",
  },

  runningTasks: [],
  stagePresets: [],
  editingStageKey: null,
  editText: "",
  customStageKey: null,

  // ── Actions ──

  loadFiles: async () => {
    set({ filesLoading: true });
    try {
      const files = await api.audiolabListFiles(200, 0);
      set({ files, filesLoading: false });
    } catch (err) {
      console.error("loadFiles failed:", err);
      set({ filesLoading: false });
    }
  },

  setFilePanelOpen: (v) => set({ isFilePanelOpen: v }),

  selectFile: async (fileId, sessionId) => {
    set({ activeFileId: fileId, activeSessionId: sessionId, selectedTab: "Transcript" });

    // 懒加载 bundle
    const state = get();
    if (!state.loadedBundles.has(sessionId)) {
      try {
        const bundle = await api.audiolabGetBundle(sessionId);
        const map = new Map(state.loadedBundles);
        lruSet(map, sessionId, bundle, 3);
        set({ loadedBundles: map });
      } catch (err) {
        console.error("loadBundle failed:", err);
      }
    }

    // 打开播放器
    try {
      const info = await api.audiolabPlaybackOpen(sessionId);
      set({
        playback: {
          ...get().playback,
          playbackPath: info.playback_path,
          durationMs: info.duration_ms,
          positionMs: 0,
          isPlaying: false,
        },
      });
    } catch (err) {
      console.error("playbackOpen failed:", err);
    }

    // 加载运行中任务
    try {
      const tasks = await api.audiolabListRunningTasks(sessionId);
      set({ runningTasks: tasks });
    } catch (err) {
      console.error("loadRunningTasks failed:", err);
    }
  },

  setSelectedTab: (tab) => set({ selectedTab: tab }),

  updatePlayback: (partial) => set((s) => ({
    playback: { ...s.playback, ...partial },
  })),

  loadRunningTasks: async (sessionId) => {
    try {
      const tasks = await api.audiolabListRunningTasks(sessionId);
      set({ runningTasks: tasks });
    } catch (err) {
      console.error("loadRunningTasks failed:", err);
    }
  },

  loadStagePresets: async () => {
    try {
      const presets = await api.audiolabListStagePresets();
      set({ stagePresets: presets });
    } catch (err) {
      console.error("loadStagePresets failed:", err);
    }
  },

  refreshBundle: async (sessionId) => {
    try {
      const bundle = await api.audiolabGetBundle(sessionId);
      const map = new Map(get().loadedBundles);
      lruSet(map, sessionId, bundle, 3);
      set({ loadedBundles: map });
    } catch (err) {
      console.error("refreshBundle failed:", err);
    }
  },

  setEditingStage: (stageKey, text) => set({
    editingStageKey: stageKey,
    editText: text ?? "",
  }),

  setCustomStageKey: (key) => set({ customStageKey: key }),

  currentBundle: () => {
    const s = get();
    return s.activeSessionId ? s.loadedBundles.get(s.activeSessionId) : undefined;
  },
}));
