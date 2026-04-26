import { create } from "zustand";
import type { AudioLibraryItem, AudioLifecycle, AudioTask } from "../lib/tauri-api";
import { api } from "../lib/tauri-api";

interface AudioLabState {
  items: AudioLibraryItem[];
  selectedItemId: string | null;
  lifecycle: AudioLifecycle[];
  tasks: AudioTask[];
  loading: boolean;

  // Actions
  loadItems: () => Promise<void>;
  selectItem: (id: string | null) => void;
  loadLifecycle: (itemId: string) => Promise<void>;
  loadTasks: (itemId: string) => Promise<void>;
  addItem: (item: AudioLibraryItem) => void;
  removeItem: (id: string) => Promise<void>;
  submitStage: (audioItemId: string, stage: string, taskType: string) => Promise<void>;
}

export const useAudioLabStore = create<AudioLabState>((set, get) => ({
  items: [],
  selectedItemId: null,
  lifecycle: [],
  tasks: [],
  loading: false,

  loadItems: async () => {
    set({ loading: true });
    try {
      const items = await api.listAudioItems();
      set({ items });
    } finally {
      set({ loading: false });
    }
  },

  selectItem: (id) => {
    set({ selectedItemId: id, lifecycle: [], tasks: [] });
    if (id) {
      get().loadLifecycle(id);
      get().loadTasks(id);
    }
  },

  loadLifecycle: async (itemId) => {
    const lifecycle = await api.getAudioLifecycle(itemId);
    set({ lifecycle });
  },

  loadTasks: async (itemId) => {
    const tasks = await api.listTasks(undefined, 100);
    set({ tasks: tasks.filter(t => t.audio_item_id === itemId) });
  },

  addItem: (item) => set((s) => ({ items: [item, ...s.items] })),

  removeItem: async (id) => {
    await api.deleteAudioItem(id);
    set((s) => ({
      items: s.items.filter((i) => i.id !== id),
      selectedItemId: s.selectedItemId === id ? null : s.selectedItemId,
    }));
  },

  submitStage: async (audioItemId, stage, taskType) => {
    await api.submitTask({
      audio_item_id: audioItemId,
      stage: stage as any,
      task_type: taskType as any,
      status: "Queued",
      priority: 5,
      retry_count: 0,
      max_retries: 3,
      progress: 0,
    });
    get().loadTasks(audioItemId);
    get().loadLifecycle(audioItemId);
  },
}));
