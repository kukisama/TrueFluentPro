import { create } from "zustand";
import type { AudioTask } from "../lib/tauri-api";
import { api } from "../lib/tauri-api";

type TaskFilter = "all" | "Queued" | "Executing" | "Completed" | "Failed" | "Cancelled";

interface TaskMonitorState {
  tasks: AudioTask[];
  filter: TaskFilter;
  selectedTaskId: string | null;
  loading: boolean;
  autoRefresh: boolean;

  // Actions
  loadTasks: () => Promise<void>;
  setFilter: (filter: TaskFilter) => void;
  selectTask: (id: string | null) => void;
  cancelTask: (id: string) => Promise<void>;
  retryTask: (id: string) => Promise<void>;
  setAutoRefresh: (v: boolean) => void;
  filteredTasks: () => AudioTask[];
}

export const useTaskMonitorStore = create<TaskMonitorState>((set, get) => ({
  tasks: [],
  filter: "all",
  selectedTaskId: null,
  loading: false,
  autoRefresh: true,

  loadTasks: async () => {
    set({ loading: true });
    try {
      const tasks = await api.listTasks();
      set({ tasks });
    } finally {
      set({ loading: false });
    }
  },

  setFilter: (filter) => set({ filter }),
  selectTask: (id) => set({ selectedTaskId: id }),

  cancelTask: async (id) => {
    await api.cancelTask(id);
    get().loadTasks();
  },

  retryTask: async (id) => {
    await api.retryTask(id);
    get().loadTasks();
  },

  setAutoRefresh: (v) => set({ autoRefresh: v }),

  filteredTasks: () => {
    const { tasks, filter } = get();
    if (filter === "all") return tasks;
    return tasks.filter((t) => t.status === filter);
  },
}));
