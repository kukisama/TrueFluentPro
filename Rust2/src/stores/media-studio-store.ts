import { create } from "zustand";
import type { Session, ChatMessage } from "../lib/tauri-api";
import { api } from "../lib/tauri-api";

interface MediaStudioState {
  sessions: Session[];
  activeSessionId: string | null;
  messages: ChatMessage[];
  inputText: string;
  isGenerating: boolean;
  attachments: File[];

  // Actions
  loadSessions: () => Promise<void>;
  selectSession: (id: string | null) => void;
  createSession: (name?: string) => Promise<string>;
  deleteSession: (id: string) => Promise<void>;
  renameSession: (id: string, name: string) => Promise<void>;
  loadMessages: (sessionId: string) => Promise<void>;
  setInputText: (text: string) => void;
  setGenerating: (v: boolean) => void;
  addAttachment: (file: File) => void;
  removeAttachment: (idx: number) => void;
  clearAttachments: () => void;
  appendMessage: (msg: ChatMessage) => void;
  updateLastAssistantContent: (content: string) => void;
}

export const useMediaStudioStore = create<MediaStudioState>((set, get) => ({
  sessions: [],
  activeSessionId: null,
  messages: [],
  inputText: "",
  isGenerating: false,
  attachments: [],

  loadSessions: async () => {
    const sessions = await api.listSessions();
    set({ sessions });
  },

  selectSession: (id) => {
    set({ activeSessionId: id, messages: [] });
    if (id) get().loadMessages(id);
  },

  createSession: async (name) => {
    const session = await api.createSession(name || `会话 ${new Date().toLocaleString()}`, "chat");
    set((s) => ({ sessions: [session, ...s.sessions], activeSessionId: session.id }));
    return session.id;
  },

  deleteSession: async (id) => {
    await api.deleteSession(id);
    set((s) => ({
      sessions: s.sessions.filter((ss) => ss.id !== id),
      activeSessionId: s.activeSessionId === id ? null : s.activeSessionId,
      messages: s.activeSessionId === id ? [] : s.messages,
    }));
  },

  renameSession: async (id, name) => {
    // Rename via update pattern — store only
    set((s) => ({
      sessions: s.sessions.map((ss) => (ss.id === id ? { ...ss, title: name } : ss)),
    }));
  },

  loadMessages: async (sessionId) => {
    const messages = await api.getSessionMessages(sessionId);
    set({ messages });
  },

  setInputText: (text) => set({ inputText: text }),
  setGenerating: (v) => set({ isGenerating: v }),

  addAttachment: (file) => set((s) => ({ attachments: [...s.attachments, file] })),
  removeAttachment: (idx) => set((s) => ({ attachments: s.attachments.filter((_, i) => i !== idx) })),
  clearAttachments: () => set({ attachments: [] }),

  appendMessage: (msg) => set((s) => ({ messages: [...s.messages, msg] })),
  updateLastAssistantContent: (content) =>
    set((s) => {
      const msgs = [...s.messages];
      for (let i = msgs.length - 1; i >= 0; i--) {
        if (msgs[i].role === "assistant") {
          msgs[i] = { ...msgs[i], content };
          break;
        }
      }
      return { messages: msgs };
    }),
}));
