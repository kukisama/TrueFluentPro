import { useState, useRef, useEffect, useCallback } from "react";
import { useTranslation } from "react-i18next";
import {
  Image, Video, Sparkles, Send, Download, Trash2,
  Maximize2, Plus, Loader2, Globe, ImagePlus,
  MessageSquare, Edit2, RefreshCw, Brain,
  Copy, StopCircle, Check, X,
  PanelLeftClose, PanelLeftOpen,
} from "lucide-react";
import { cn } from "../lib/utils";
import {
  Button, GlassCard, Input, Textarea, Select, Label,
  FadeIn, EmptyState, ScrollArea, Badge,
} from "../components/ui";
import { useAppStore } from "../stores/app-store";
import { api, type ImageGenResult, type StreamTokenEvent, type Session } from "../lib/tauri-api";
import { convertFileSrc } from "@tauri-apps/api/core";
import { MarkdownRenderer } from "../components/MarkdownRenderer";

/* ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
   创作工坊 — AI 对话 + 图片 + 视频
   对标 C# MediaStudioView / MediaSessionViewModel
   ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━ */

// 图片尺寸预设按模型能力区分（来源 Assets/image-models.json）
const IMAGE_SIZE_PRESETS: Record<string, { label: string; w: number; h: number }[]> = {
  "gpt-image-2": [
    { label: "1:1", w: 1024, h: 1024 },
    { label: "16:9", w: 1792, h: 1024 },
    { label: "9:16", w: 1024, h: 1792 },
    { label: "4:3", w: 1024, h: 768 },
    { label: "3:4", w: 768, h: 1024 },
  ],
  "gpt-image-1.5": [
    { label: "1:1", w: 1024, h: 1024 },
    { label: "3:2", w: 1536, h: 1024 },
    { label: "2:3", w: 1024, h: 1536 },
  ],
  "gpt-image-1": [
    { label: "1:1", w: 1024, h: 1024 },
    { label: "3:2", w: 1536, h: 1024 },
    { label: "2:3", w: 1024, h: 1536 },
  ],
  "_default": [
    { label: "1:1", w: 1024, h: 1024 },
    { label: "16:9", w: 1792, h: 1024 },
    { label: "9:16", w: 1024, h: 1792 },
  ],
};

type ChatMode = "text" | "image" | "search";

interface ChatMessage {
  id: string;
  role: "user" | "assistant" | "system";
  content: string;
  mode?: ChatMode;
  imageBase64?: string;
  imagePrompt?: string;
  searchResults?: string[];
  timestamp: string;
  loading?: boolean;
  // C# 对标: ChatMessageViewModel 的小巧思
  reasoningText?: string;
  promptTokens?: number;
  completionTokens?: number;
}

export function MediaStudioView() {
  const { t } = useTranslation();
  const [activeTab, setActiveTab] = useState<"chat" | "image" | "video">("chat");

  return (
    <div className="flex flex-col h-full">
      {/* Tab 头 */}
      <div className="flex items-center gap-4 px-6 py-2.5 border-b border-[var(--border-subtle)]"
        style={{ backgroundColor: "var(--toolbar-bg)" }}>
        <h1 className="text-base font-semibold text-[var(--text-primary)] mr-2">{t("media.title")}</h1>
        <div className="inline-flex items-center rounded-lg bg-[var(--surface-1)] p-0.5 gap-0.5">
          {([
            { key: "chat" as const, icon: <MessageSquare size={14} />, label: "AI 对话" },
            { key: "image" as const, icon: <Image size={14} />, label: t("media.imageGen") },
            { key: "video" as const, icon: <Video size={14} />, label: t("media.videoGen") },
          ]).map((tab) => (
            <button key={tab.key} onClick={() => setActiveTab(tab.key)}
              className={cn(
                "inline-flex items-center gap-1.5 rounded-lg px-3 py-1.5 text-sm font-medium transition-all duration-200",
                activeTab === tab.key
                  ? "bg-brand-600/20 text-[var(--active-text)] shadow-sm"
                  : "text-[var(--text-muted)] hover:text-[var(--text-primary)]"
              )}>
              {tab.icon} {tab.label}
            </button>
          ))}
        </div>
      </div>

      {/* 所有面板始终挂载，通过 display 切换可见性，避免切 Tab 丢状态 */}
      <div className={cn("flex-1 overflow-hidden", activeTab !== "chat" && "hidden")}><AiChatPanel /></div>
      <div className={cn("flex-1 overflow-hidden", activeTab !== "image" && "hidden")}><ImageGenPanel /></div>
      <div className={cn("flex-1 overflow-hidden", activeTab !== "video" && "hidden")}><VideoGenPanel /></div>
    </div>
  );
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  AI 对话面板 — 文字 + 图片 + 搜索一体化
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

function AiChatPanel() {
  const config = useAppStore((s) => s.config);
  const [input, setInput] = useState("");
  const [chatMode, setChatMode] = useState<ChatMode>("text");
  const [messages, setMessages] = useState<ChatMessage[]>([
    {
      id: "sys-0",
      role: "assistant",
      content: "你好！我是 AI 助手，支持多种交互模式：\n\n- **文字对话** — 流式回复，支持 Markdown\n- **图片生成** — 输入描述，在线生成\n- **联网搜索** — 结合搜索结果回答问题\n\n请输入你的问题，或切换模式开始。",
      timestamp: now(),
    },
  ]);
  const [streaming, setStreaming] = useState(false);
  const [streamBuffer, setStreamBuffer] = useState("");
  // 🔒 追踪当前流式响应属于哪个会话，切换会话时隐藏非当前会话的流
  const streamingSessionRef = useRef<string | null>(null);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [editText, setEditText] = useState("");
  const [reasoningExpanded, setReasoningExpanded] = useState<Record<string, boolean>>({});
  const scrollRef = useRef<HTMLDivElement>(null);
  const unlistenRef = useRef<(() => void) | null>(null);
  const fileInputRef = useRef<HTMLInputElement>(null);
  const activeSessionRef = useRef<string | null>(null);
  const [attachments, setAttachments] = useState<{ name: string; size: number; base64: string; mime: string }[]>([]);

  // ── 会话持久化（对齐 C# SessionListViewModel）──
  const [sessions, setSessions] = useState<Session[]>([]);
  const [activeSessionId, setActiveSessionId] = useState<string | null>(null);
  const [sidebarOpen, setSidebarOpen] = useState(true);
  // O-05: 会话重命名
  const [renamingSessionId, setRenamingSessionId] = useState<string | null>(null);
  const [renameText, setRenameText] = useState("");

  // 保持 ref 与 state 同步，让流式回调读取最新值
  useEffect(() => { activeSessionRef.current = activeSessionId; }, [activeSessionId]);

  useEffect(() => {
    api.listSessions().then(setSessions).catch(() => {});
  }, []);

  const handleNewSession = useCallback(async () => {
    try {
      const session = await api.createSession("新对话", "chat");
      setSessions((prev) => [session, ...prev]);
      setActiveSessionId(session.id);
      setMessages([{
        id: "sys-0", role: "assistant",
        content: "新对话已创建，请开始提问。", timestamp: now(),
      }]);
    } catch (err) { console.error("Failed to create session:", err); }
  }, []);

  // O-16: handleSelectSession 已替换为 handleSelectSessionLRU（见下方 LRU 区块）

  const handleDeleteSession = useCallback(async (sessionId: string) => {
    try {
      await api.deleteSession(sessionId);
      setSessions((prev) => prev.filter((s) => s.id !== sessionId));
      if (activeSessionId === sessionId) {
        setActiveSessionId(null);
        setMessages([{ id: "sys-0", role: "assistant", content: "请选择或新建对话。", timestamp: now() }]);
      }
    } catch (err) { console.error("Failed to delete session:", err); }
  }, [activeSessionId]);

  // O-05: 会话重命名持久化到后端
  const handleRenameSession = useCallback(async (sessionId: string) => {
    const trimmed = renameText.trim();
    if (!trimmed) { setRenamingSessionId(null); return; }
    try {
      await api.renameSession(sessionId, trimmed);
      setSessions((prev) => prev.map((s) => s.id === sessionId ? { ...s, title: trimmed } : s));
    } catch (err) { console.error("Failed to rename session:", err); }
    setRenamingSessionId(null);
  }, [renameText]);

  // O-16: 会话 LRU 内存管理 — 最多缓存 8 个会话的消息
  const MAX_LOADED_SESSIONS = 8;
  const sessionCacheRef = useRef<Map<string, ChatMessage[]>>(new Map());
  const sessionAccessOrder = useRef<string[]>([]);

  const evictOldestSessions = useCallback(() => {
    const cache = sessionCacheRef.current;
    const order = sessionAccessOrder.current;
    while (order.length > MAX_LOADED_SESSIONS) {
      const oldest = order.shift();
      if (oldest) cache.delete(oldest);
    }
  }, []);

  // 切换会话时保存当前消息到缓存，并从缓存或后端加载目标会话
  const handleSelectSessionLRU = useCallback(async (sessionId: string) => {
    if (sessionId === activeSessionId) return;
    // 保存当前会话消息到缓存
    if (activeSessionId && messages.length > 0) {
      sessionCacheRef.current.set(activeSessionId, [...messages]);
    }
    // 🔒 切换会话时：隐藏流式状态（后台流继续，但 UI 不展示）
    setStreamBuffer("");
    setActiveSessionId(sessionId);
    // 更新 LRU 顺序
    sessionAccessOrder.current = sessionAccessOrder.current.filter((id) => id !== sessionId);
    sessionAccessOrder.current.push(sessionId);
    evictOldestSessions();
    // 尝试从缓存读取
    const cached = sessionCacheRef.current.get(sessionId);
    if (cached) {
      setMessages(cached);
      return;
    }
    // 缓存未命中，从后端加载
    try {
      const msgs = await api.getSessionMessages(sessionId);
      const loaded = msgs.length > 0
        ? msgs.map((m) => ({
            id: m.id, role: m.role as any, content: m.content, timestamp: m.created_at || now(),
            mode: m.mode || "text",
            imageBase64: m.image_base64 || undefined,
            reasoningText: m.reasoning_text || undefined,
            promptTokens: m.prompt_tokens || undefined,
            completionTokens: m.completion_tokens || undefined,
          }))
        : [{ id: "sys-0", role: "assistant" as const, content: "对话记录为空。", timestamp: now() }];
      setMessages(loaded);
      sessionCacheRef.current.set(sessionId, loaded);
    } catch (err) { console.error("Failed to load session messages:", err); }
  }, [activeSessionId, messages, evictOldestSessions]);

  // 从配置读取对话模型（对齐 C# ConversationModelRef）
  const conversationModel = config?.ai?.conversation_model;
  const resolvedEndpoint = config?.endpoints.find(
    (ep) => ep.enabled && (
      conversationModel?.endpoint_id
        ? ep.id === conversationModel.endpoint_id
        : ep.endpoint_type !== "azure_speech" && ep.models.some((m) => m.capabilities.includes("text"))
    ),
  );
  const resolvedModelId = conversationModel?.model_id || resolvedEndpoint?.models.find((m) => m.capabilities.includes("text"))?.model_id || "gpt-4.1";

  useEffect(() => {
    if (scrollRef.current) scrollRef.current.scrollTop = scrollRef.current.scrollHeight;
  }, [messages, streamBuffer]);

  useEffect(() => {
    return () => { unlistenRef.current?.(); };
  }, []);

  const handleStop = useCallback(() => {
    unlistenRef.current?.();
    unlistenRef.current = null;
    if (streamBuffer) {
      setMessages((prev) => [...prev, { id: genId(), role: "assistant", content: streamBuffer + "\n\n*[已中断]*", timestamp: now() }]);
    }
    setStreamBuffer("");
    setStreaming(false);
    streamingSessionRef.current = null;
  }, [streamBuffer]);

  // 核心发送（复用于普通发送、编辑后重发、再生成）
  // sessionIdOverride: 强制绑定的 session_id（用于自动创建后 state 尚未生效的场景）
  const doSend = useCallback(async (newMessages: ChatMessage[], mode: ChatMode, sessionIdOverride?: string) => {
    // 🔒 捕获发起请求时的 session_id，确保响应写回正确会话
    const boundSessionId = sessionIdOverride || activeSessionId;

    if (!resolvedEndpoint) {
      setMessages((prev) => [...prev, { id: genId(), role: "assistant", content: "请先在设置中配置 AI 端点和对话模型", timestamp: now() }]);
      return;
    }

    // 将消息持久化到指定 session（使用绑定的 sessionId，非当前值）
    const persistToSession = async (msg: ChatMessage) => {
      if (!boundSessionId) return;
      try {
        await api.addMessage({
          session_id: boundSessionId,
          role: msg.role,
          content: msg.content,
          mode: msg.mode,
          image_base64: msg.imageBase64,
        });
        setSessions((prev) => prev.map((s) =>
          s.id === boundSessionId ? { ...s, message_count: (s.message_count || 0) + 1 } : s
        ));
      } catch { /* best-effort */ }
    };

    // 图片模式
    if (mode === "image") {
      const userMsg = newMessages[newMessages.length - 1];
      const loadingMsg: ChatMessage = { id: genId(), role: "assistant", content: "正在生成图片...", timestamp: now(), loading: true };
      if (activeSessionRef.current === boundSessionId) {
        setMessages([...newMessages, loadingMsg]);
      }
      try {
        const results: ImageGenResult[] = await api.generateImage({
          prompt: userMsg.content,
          width: 1024, height: 1024,
          model: config?.media?.image_model?.model_id || "gpt-image-2",
          output_format: "png",
          endpoint_id: config?.media?.image_model?.endpoint_id || resolvedEndpoint.id,
        });
        const imgContent = results[0]?.revised_prompt || userMsg.content;
        if (activeSessionRef.current === boundSessionId) {
          setMessages((prev) => prev.map((m) =>
            m.id === loadingMsg.id
              ? { ...m, content: imgContent, imageBase64: results[0]?.base64, imagePrompt: userMsg.content, loading: false }
              : m,
          ));
        }
        persistToSession({ id: loadingMsg.id, role: "assistant", content: imgContent, mode: "image", imageBase64: results[0]?.base64, timestamp: now() });
      } catch (err) {
        if (activeSessionRef.current === boundSessionId) {
          setMessages((prev) => prev.map((m) =>
            m.id === loadingMsg.id ? { ...m, content: `图片生成失败: ${err}`, loading: false } : m,
          ));
        }
      }
      return;
    }

    // 文字/搜索模式 — 流式
    streamingSessionRef.current = boundSessionId;
    // 仅在仍处于发起会话时更新 UI
    if (activeSessionRef.current === boundSessionId) {
      setStreaming(true);
      setStreamBuffer("");
      setMessages(newMessages);
    }

    const systemPrompt = config?.ai?.insight_system_prompt ||
      (mode === "search"
        ? "你是一个具有联网搜索能力的 AI 助手。"
        : "你是一个全能 AI 助手，擅长分析文本、总结内容、编写代码、创意写作。");

    try {
      const apiMessages = [
        { role: "system", content: systemPrompt },
        ...newMessages
          .filter((m) => m.role !== "system")
          .slice(-(config?.ai?.max_conversation_turns ?? 20))
          .map((m) => ({ role: m.role, content: m.content })),
      ];

      const streamId = await api.aiCompleteStream({
        messages: apiMessages,
        model: resolvedModelId,
        temperature: 0.7,
        max_tokens: 4096,
        endpoint_id: resolvedEndpoint.id,
      });

      let buffer = "";
      let reasoningBuffer = "";
      let usageData: { prompt_tokens: number; completion_tokens: number } | undefined;

      const unlisten = await api.onStreamToken((event: StreamTokenEvent) => {
        if (event.stream_id !== streamId) return;
        // 🔒 判断用户是否仍在发起请求的会话上
        const stillViewing = activeSessionRef.current === boundSessionId;

        if (event.done) {
          const assistantMsg: ChatMessage = {
            id: genId(), role: "assistant", content: buffer,
            timestamp: now(), mode: mode === "search" ? "search" : undefined,
            reasoningText: reasoningBuffer || undefined,
            promptTokens: usageData?.prompt_tokens,
            completionTokens: usageData?.completion_tokens,
          };
          if (stillViewing) {
            setMessages((prev) => [...prev, assistantMsg]);
            setStreamBuffer("");
            setStreaming(false);
            streamingSessionRef.current = null;
          } else {
            // 用户已切走：将完成的消息存入 LRU 缓存
            const cached = sessionCacheRef.current.get(boundSessionId || "");
            if (cached) {
              cached.push(assistantMsg);
              sessionCacheRef.current.set(boundSessionId || "", cached);
            }
          }
          persistToSession(assistantMsg);
          unlistenRef.current = null;
          unlisten();
          return;
        }
        if (event.error) {
          if (stillViewing) {
            setMessages((prev) => [...prev, { id: genId(), role: "assistant", content: `错误: ${event.error}`, timestamp: now() }]);
            setStreamBuffer("");
            setStreaming(false);
            streamingSessionRef.current = null;
          }
          unlistenRef.current = null;
          unlisten();
          return;
        }
        if (event.reasoning) {
          reasoningBuffer += event.reasoning;
        }
        if (event.usage) {
          usageData = event.usage;
        }
        if (event.token) {
          buffer += event.token;
          if (stillViewing) {
            setStreamBuffer(buffer);
          }
        }
      });
      unlistenRef.current = unlisten;
    } catch (err) {
      setMessages((prev) => [...prev, { id: genId(), role: "assistant", content: `请求失败: ${err}`, timestamp: now() }]);
      setStreaming(false);
      streamingSessionRef.current = null;
    }
  }, [resolvedEndpoint, resolvedModelId, config, activeSessionId]);

  const handleSend = useCallback(async () => {
    if (!input.trim()) return;
    // 当前会话正在流式中时阻止重复发送
    if (streaming && streamingSessionRef.current === activeSessionId) return;

    // 如果没有活跃会话，自动创建一个
    let sessionId = activeSessionId;
    if (!sessionId) {
      try {
        const session = await api.createSession("新对话", "chat");
        setSessions((prev) => [session, ...prev]);
        setActiveSessionId(session.id);
        sessionId = session.id;
      } catch (err) { console.error("自动创建会话失败:", err); }
    }

    // 构建消息内容：有图片附件时使用多模态 content parts
    let content: string | Array<{ type: string; text?: string; image_url?: { url: string } }> = input;
    if (attachments.length > 0) {
      const parts: Array<{ type: string; text?: string; image_url?: { url: string } }> = [
        { type: "text", text: input },
      ];
      for (const a of attachments) {
        if (a.mime.startsWith("image/")) {
          parts.push({ type: "image_url", image_url: { url: `data:${a.mime};base64,${a.base64}` } });
        } else {
          // 文本类附件：解码 base64 内容追加到 text
          try {
            const text = atob(a.base64);
            parts.push({ type: "text", text: `\n\n--- 附件: ${a.name} ---\n${text}` });
          } catch { /* ignore decode errors */ }
        }
      }
      content = parts;
    }

    const userMsg: ChatMessage = { id: genId(), role: "user", content: input, mode: chatMode, timestamp: now() };
    setInput("");
    setAttachments([]);

    // 用户消息立即显示在 UI 上
    setMessages((prev) => [...prev, userMsg]);

    // 用户消息立即持久化（使用确定的 sessionId 而非闭包中的 activeSessionId）
    if (sessionId) {
      api.addMessage({ session_id: sessionId, role: userMsg.role, content: userMsg.content, mode: userMsg.mode }).catch(() => {});
      setSessions((prev) => prev.map((s) => s.id === sessionId ? { ...s, message_count: (s.message_count || 0) + 1 } : s));
    }

    // 构建带附件的消息列表用于 API 调用
    const apiMessages = [...messages, { ...userMsg, content: content as string }];
    await doSend(apiMessages, chatMode, sessionId || undefined);
  }, [input, messages, streaming, chatMode, attachments, doSend, activeSessionId]);

  // ── 编辑消息后重发（对齐 C# SendEditCommand）──
  const handleEditSend = useCallback(async (msgId: string) => {
    if (streaming && streamingSessionRef.current === activeSessionId) return;
    const idx = messages.findIndex((m) => m.id === msgId);
    if (idx < 0) return;
    // 截断到编辑消息位置，替换内容
    const edited = { ...messages[idx], content: editText };
    const truncated = [...messages.slice(0, idx), edited];
    setEditingId(null);
    setEditText("");
    await doSend(truncated, edited.mode || "text");
  }, [messages, editText, streaming, doSend]);

  // ── 仅保存编辑（对齐 C# SaveEditCommand）──
  const handleEditSave = (msgId: string) => {
    setMessages((prev) => prev.map((m) => m.id === msgId ? { ...m, content: editText } : m));
    setEditingId(null);
    setEditText("");
  };

  // ── 再生成（对齐 C# RegenerateCommand）──
  const handleRegenerate = useCallback(async (msgId: string) => {
    if (streaming && streamingSessionRef.current === activeSessionId) return;
    const idx = messages.findIndex((m) => m.id === msgId);
    if (idx < 0) return;
    // 删除这条 AI 回复，用到它之前的消息重新生成
    const truncated = messages.slice(0, idx);
    await doSend(truncated, messages[idx - 1]?.mode || "text");
  }, [messages, streaming, doSend]);

  // ── 删除消息（对齐 C# DeleteMessageCommand）──
  const handleDelete = (msgId: string) => {
    setMessages((prev) => prev.filter((m) => m.id !== msgId));
  };

  const handleClear = () => {
    setMessages([{
      id: "sys-0",
      role: "assistant",
      content: "对话已清空。请开始新的对话。",
      timestamp: now(),
    }]);
  };

  return (
    <div className="flex h-full relative">
      {/* ── 会话列表侧栏 ── */}
      <div className={cn(
        "border-r border-[var(--border-subtle)] flex flex-col shrink-0 transition-all duration-200 overflow-hidden",
        sidebarOpen ? "w-[200px]" : "w-0 border-r-0",
      )} style={{ backgroundColor: "var(--sidebar-bg)" }}>
        <div className="w-[200px]">
          <div className="p-2 border-b border-[var(--border-subtle)] flex items-center gap-1">
            <Button variant="ghost" size="icon" className="h-7 w-7" onClick={handleNewSession} title="新建对话">
              <Plus size={14} />
            </Button>
            <span className="text-xs text-[var(--text-muted)] flex-1">会话</span>
            <Button variant="ghost" size="icon" className="h-7 w-7" onClick={() => setSidebarOpen(false)}>
              <PanelLeftClose size={14} />
            </Button>
          </div>
          <ScrollArea className="flex-1" style={{ height: "calc(100vh - 120px)" }}>
            <div className="p-1.5 space-y-0.5">
              {sessions.map((s) => (
                <button key={s.id} onClick={() => handleSelectSessionLRU(s.id)}
                  onDoubleClick={() => { setRenamingSessionId(s.id); setRenameText(s.title); }}
                  className={cn(
                    "w-full text-left px-2.5 py-2 rounded-lg text-xs transition-all group",
                    activeSessionId === s.id
                      ? "bg-brand-600/15 text-[var(--active-text)]"
                      : "text-[var(--text-secondary)] hover:bg-[var(--hover-bg)]"
                  )}>
                  <div className="flex items-center gap-1.5">
                    <MessageSquare size={12} className="shrink-0" />
                    {renamingSessionId === s.id ? (
                      <input value={renameText} onChange={(e) => setRenameText(e.target.value)}
                        onBlur={() => handleRenameSession(s.id)}
                        onKeyDown={(e) => { if (e.key === "Enter") handleRenameSession(s.id); if (e.key === "Escape") setRenamingSessionId(null); }}
                        className="flex-1 bg-[var(--input-bg)] border border-[var(--border-medium)] rounded px-1 text-xs"
                        autoFocus onClick={(e) => e.stopPropagation()} />
                    ) : (
                      <span className="truncate flex-1">{s.title}</span>
                    )}
                    <button onClick={(e) => { e.stopPropagation(); handleDeleteSession(s.id); }}
                      className="opacity-0 group-hover:opacity-100 transition-opacity">
                      <Trash2 size={10} className="text-red-400" />
                    </button>
                  </div>
                  <p className="text-[10px] text-[var(--text-muted)] mt-0.5 ml-4">
                    {s.message_count || 0} 条
                  </p>
                </button>
              ))}
            </div>
          </ScrollArea>
        </div>
      </div>

      <div className="flex flex-col flex-1 min-w-0">
        {/* 侧栏开关 */}
        {!sidebarOpen && (
          <Button variant="ghost" size="icon" className="absolute top-2 left-2 z-10 h-7 w-7"
            onClick={() => setSidebarOpen(true)} title="展开会话列表">
            <PanelLeftOpen size={14} />
          </Button>
        )}
        {/* 消息区 */}
        <div ref={scrollRef} className="flex-1 overflow-y-auto p-6 space-y-3">
          {messages.map((msg) => (
            <FadeIn key={msg.id} delay={0}>
              <div className={cn("max-w-3xl group", msg.role === "user" ? "ml-auto" : "")}>
                {/* 模式标签 */}
                {msg.mode && msg.role === "user" && (
                  <div className={cn("flex mb-1", msg.role === "user" ? "justify-end" : "")}>
                    <Badge variant={msg.mode === "image" ? "amber" : msg.mode === "search" ? "green" : "blue"}>
                      {msg.mode === "image" && <><ImagePlus size={10} /> 图片</>}
                      {msg.mode === "search" && <><Globe size={10} /> 搜索</>}
                      {msg.mode === "text" && <><MessageSquare size={10} /> 文字</>}
                    </Badge>
                  </div>
                )}
                <GlassCard
                  className={cn(
                    "px-4 py-3 relative",
                    msg.role === "user" ? "bg-[var(--user-msg-bg)] border-[var(--user-msg-border)]" : "",
                  )}
                >
                  {msg.loading ? (
                    <div className="flex items-center gap-2">
                      <Loader2 size={14} className="animate-spin text-brand-400" />
                      <span className="text-sm text-[var(--text-muted)]">{msg.content}</span>
                    </div>
                  ) : editingId === msg.id ? (
                    /* ── 编辑模式（对齐 C# IsEditing） ── */
                    <div className="space-y-2">
                      <Textarea value={editText} onChange={(e) => setEditText(e.target.value)}
                        className="min-h-[60px] text-sm" autoFocus />
                      <div className="flex justify-end gap-2">
                        <Button variant="ghost" size="sm" onClick={() => { setEditingId(null); setEditText(""); }}>
                          <X size={12} /> 取消
                        </Button>
                        <Button variant="secondary" size="sm" onClick={() => handleEditSave(msg.id)}>
                          <Check size={12} /> 仅保存
                        </Button>
                        <Button size="sm" onClick={() => handleEditSend(msg.id)}>
                          <Send size={12} /> 保存并重新生成
                        </Button>
                      </div>
                    </div>
                  ) : (
                    <>
                      {/* 推理思考过程（对齐 C# ReasoningText）*/}
                      {msg.reasoningText && (
                        <div className="mb-2">
                          <button
                            className="flex items-center gap-1.5 text-xs text-[var(--text-muted)] hover:text-[var(--text-secondary)] transition-colors"
                            onClick={() => setReasoningExpanded((s) => ({ ...s, [msg.id]: !s[msg.id] }))}
                          >
                            <Brain size={12} /> 思考过程 {reasoningExpanded[msg.id] ? "▴" : "▾"}
                          </button>
                          {reasoningExpanded[msg.id] && (
                            <div className="mt-1 p-2 rounded bg-[var(--surface-1)] text-xs text-[var(--text-secondary)] whitespace-pre-wrap">
                              {msg.reasoningText}
                            </div>
                          )}
                        </div>
                      )}
                      {msg.role === "assistant" ? (
                        <MarkdownRenderer content={msg.content} className="text-sm text-[var(--text-primary)] prose-sm" />
                      ) : (
                        <p className="text-sm text-[var(--text-primary)] whitespace-pre-wrap">{msg.content}</p>
                      )}
                      {msg.imageBase64 && (
                        <div className="mt-3 rounded-xl overflow-hidden border border-[var(--border-subtle)]">
                          <img src={`data:image/png;base64,${msg.imageBase64}`} alt={msg.imagePrompt} className="max-w-md w-full" />
                        </div>
                      )}
                      {/* Token 统计（对齐 C# TokenUsageText）*/}
                      {msg.role === "assistant" && (msg.promptTokens || msg.completionTokens) && (
                        <p className="text-[10px] text-[var(--text-muted)] mt-1">
                          {msg.promptTokens && `↑${msg.promptTokens}`}{msg.completionTokens && ` ↓${msg.completionTokens}`} tokens
                        </p>
                      )}
                    </>
                  )}

                  {/* 操作按钮（Hover 显示，对齐 C# msg-footer-btn）*/}
                  {editingId !== msg.id && (
                    <div className="absolute top-2 right-2 opacity-0 group-hover:opacity-100 transition-opacity flex gap-1">
                      {msg.role === "user" && (
                        <Button variant="ghost" size="icon" className="h-6 w-6" title="编辑"
                          onClick={() => { setEditingId(msg.id); setEditText(msg.content); }}>
                          <Edit2 size={11} />
                        </Button>
                      )}
                      {msg.role === "assistant" && !msg.loading && (
                        <Button variant="ghost" size="icon" className="h-6 w-6" title="再生成"
                          onClick={() => handleRegenerate(msg.id)}>
                          <RefreshCw size={11} />
                        </Button>
                      )}
                      <Button variant="ghost" size="icon" className="h-6 w-6" title="复制"
                        onClick={() => navigator.clipboard.writeText(msg.content)}>
                        <Copy size={11} />
                      </Button>
                      <Button variant="ghost" size="icon" className="h-6 w-6 text-red-400" title="删除"
                        onClick={() => handleDelete(msg.id)}>
                        <Trash2 size={11} />
                      </Button>
                    </div>
                  )}
                </GlassCard>
                <span className="text-[10px] text-[var(--text-muted)] mt-0.5 block">{msg.timestamp}</span>
              </div>
            </FadeIn>
          ))}

          {/* 流式输出 */}
          {streamBuffer && (
            <div className="max-w-3xl">
              <GlassCard className="px-4 py-3">
                <MarkdownRenderer content={streamBuffer} className="text-sm text-[var(--text-primary)] prose-sm" />
                <span className="inline-block w-1.5 h-4 bg-brand-400 animate-pulse ml-0.5 align-middle rounded-sm" />
              </GlassCard>
            </div>
          )}
        </div>

        {/* 输入区 */}
        <div className="border-t border-[var(--border-subtle)] p-4" style={{ backgroundColor: "var(--toolbar-bg)" }}>
          {/* 模式选择器 + 当前模型显示 */}
          <div className="flex items-center gap-2 mb-2 max-w-3xl mx-auto">
            {([
              { mode: "text" as ChatMode, icon: <MessageSquare size={13} />, label: "文字", disabled: false },
              { mode: "image" as ChatMode, icon: <ImagePlus size={13} />, label: "图片", disabled: false },
              { mode: "search" as ChatMode, icon: <Globe size={13} />, label: "搜索 (Coming Soon)", disabled: true },
            ]).map(({ mode, icon, label, disabled }) => (
              <button
                key={mode}
                onClick={() => !disabled && setChatMode(mode)}
                disabled={disabled}
                className={cn(
                  "flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-xs font-medium transition-all duration-200",
                  disabled
                    ? "text-[var(--text-placeholder)] cursor-not-allowed opacity-50"
                    : chatMode === mode
                      ? "bg-brand-600/15 text-[var(--active-text)] shadow-sm"
                      : "text-[var(--text-muted)] hover:bg-[var(--hover-bg)]",
                )}
              >
                {icon} {label}
              </button>
            ))}
            <span className="text-[10px] text-[var(--text-muted)] ml-1">
              {resolvedEndpoint ? `${resolvedEndpoint.name} / ${resolvedModelId}` : "未配置模型"}
            </span>
            <div className="flex-1" />
            <Button variant="ghost" size="sm" onClick={handleClear} disabled={streaming && streamingSessionRef.current === activeSessionId}>
              <Trash2 size={13} /> 清空
            </Button>
          </div>

          <div className="flex gap-2 max-w-3xl mx-auto">
            {/* 附件按钮 */}
            <input ref={fileInputRef} type="file" className="hidden" multiple accept="image/*,.txt,.md,.json,.csv,.pdf"
              onChange={async (e) => {
                const files = Array.from(e.target.files || []);
                const readPromises = files.map((f) => new Promise<{ name: string; size: number; base64: string; mime: string }>((resolve) => {
                  const reader = new FileReader();
                  reader.onload = () => {
                    const dataUrl = reader.result as string;
                    const base64 = dataUrl.split(",")[1] || "";
                    resolve({ name: f.name, size: f.size, base64, mime: f.type || "application/octet-stream" });
                  };
                  reader.readAsDataURL(f);
                }));
                const results = await Promise.all(readPromises);
                setAttachments((prev) => [...prev, ...results]);
                e.target.value = "";
              }} />
            <Button variant="ghost" size="icon" className="h-10 w-10 shrink-0" title="附件"
              onClick={() => fileInputRef.current?.click()}>
              <Plus size={16} />
            </Button>
            {/* O-06: 提示词优化 ✨ */}
            <Button variant="ghost" size="icon" className="h-10 w-10 shrink-0" title="优化提示词 ✨"
              disabled={!input.trim() || (streaming && streamingSessionRef.current === activeSessionId)}
              onClick={async () => {
                try {
                  const optimized = await api.optimizePrompt(input, resolvedEndpoint?.id);
                  setInput(optimized);
                } catch (err) { console.error("Prompt optimization failed:", err); }
              }}>
              <Sparkles size={16} />
            </Button>
            <div className="flex-1 flex flex-col gap-1">
              {attachments.length > 0 && (
                <div className="flex gap-1 flex-wrap">
                  {attachments.map((a, i) => (
                    <Badge key={i} variant="blue" className="text-[10px] gap-1">
                      {a.name}
                      <button onClick={() => setAttachments((p) => p.filter((_, j) => j !== i))} className="ml-0.5">×</button>
                    </Badge>
                  ))}
                </div>
              )}
              <Input
                value={input}
                onChange={(e) => setInput(e.target.value)}
                onKeyDown={(e) => e.key === "Enter" && !e.shiftKey && (e.preventDefault(), handleSend())}
                // O-17: Ctrl+V 粘贴图片到对话
                onPaste={(e) => {
                  const items = Array.from(e.clipboardData.items);
                  const imageItems = items.filter((it) => it.type.startsWith("image/"));
                  if (imageItems.length === 0) return;
                  e.preventDefault();
                  for (const item of imageItems) {
                    const blob = item.getAsFile();
                    if (!blob) continue;
                    const reader = new FileReader();
                    reader.onload = () => {
                      const dataUrl = reader.result as string;
                      const base64 = dataUrl.split(",")[1] || "";
                      setAttachments((prev) => [...prev, { name: `clipboard-${Date.now()}.png`, size: blob.size, base64, mime: item.type }]);
                    };
                    reader.readAsDataURL(blob);
                  }
                }}
                placeholder={
                  chatMode === "image" ? "描述你想要的图片..."
                  : chatMode === "search" ? "输入需要联网搜索的问题..."
                  : "输入问题或粘贴文本..."
                }
              />
            </div>
            {streaming && streamingSessionRef.current === activeSessionId ? (
              <Button variant="danger" onClick={handleStop} className="px-5">
                <StopCircle size={16} /> 停止
              </Button>
            ) : (
              <Button onClick={handleSend} disabled={!input.trim()} className="px-5">
                <Send size={16} />
              </Button>
            )}
          </div>
          {!resolvedEndpoint && (
            <p className="text-xs text-amber-600 dark:text-amber-400 mt-2 text-center">
              请先在「设置 → AI 洞察」配置对话模型
            </p>
          )}
        </div>
      </div>
    </div>
  );
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  图片生成面板
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

interface GeneratedImage {
  id: string;
  prompt: string;
  base64?: string;
  filePath?: string;
  revised_prompt?: string;
  time: string;
  elapsedSec?: number;
  loading?: boolean;
}

function ImageGenPanel() {
  const { t } = useTranslation();
  const config = useAppStore((s) => s.config);
  const [prompt, setPrompt] = useState("");
  const [sizeIdx, setSizeIdx] = useState(0);
  const [quality, setQuality] = useState(config?.media?.image_quality || "auto");
  const [format, setFormat] = useState("png");
  const [background, setBackground] = useState("auto");
  const [images, setImages] = useState<GeneratedImage[]>([]);
  const [generating, setGenerating] = useState(false);
  const [elapsedSec, setElapsedSec] = useState(0);
  const timerRef = useRef<ReturnType<typeof setInterval> | null>(null);

  // 🔧 启动时从 DB 加载历史图片
  useEffect(() => {
    api.listSavedImages(50).then((saved) => {
      if (saved && saved.length > 0) {
        const loaded: GeneratedImage[] = saved.map((s) => ({
          id: s.id,
          prompt: s.prompt,
          revised_prompt: s.revised_prompt,
          base64: undefined, // 不加载 base64 到内存，使用文件路径
          filePath: s.file_path,
          time: s.created_at,
          loading: false,
          elapsedSec: s.generate_seconds,
        }));
        setImages(loaded);
      }
    }).catch(() => {});
  }, []);

  // 从配置中获取图片模型（对齐 C# ImageModelRef）
  const imageModelRef = config?.media?.image_model;
  const imageEndpoint = config?.endpoints.find(
    (ep) => ep.enabled && (
      imageModelRef?.endpoint_id
        ? ep.id === imageModelRef.endpoint_id
        : ep.endpoint_type !== "azure_speech" && ep.models.some((m) => m.capabilities.includes("image"))
    ),
  );
  const imageModelId = imageModelRef?.model_id || imageEndpoint?.models.find((m) => m.capabilities.includes("image"))?.model_id || "gpt-image-2";

  // 根据模型获取对应尺寸预设
  const sizePresets = IMAGE_SIZE_PRESETS[imageModelId] || IMAGE_SIZE_PRESETS["_default"];
  // 切换模型后如果 sizeIdx 超出范围则重置
  const safeSizeIdx = sizeIdx >= sizePresets.length ? 0 : sizeIdx;

  const handleGenerate = async () => {
    if (!prompt.trim() || !imageEndpoint || generating) return;
    const id = Date.now().toString();
    setImages((prev) => [{ id, prompt, time: now(), loading: true }, ...prev]);
    setGenerating(true);

    // 实时计时器（对齐 C# Stopwatch + Timer 1s）
    const startMs = Date.now();
    setElapsedSec(0);
    timerRef.current = setInterval(() => {
      setElapsedSec(Math.floor((Date.now() - startMs) / 1000));
    }, 1000);

    try {
      const results = await api.generateImage({
        prompt, width: sizePresets[safeSizeIdx].w, height: sizePresets[safeSizeIdx].h,
        model: imageModelId, quality, output_format: format, background,
        endpoint_id: imageEndpoint.id,
      });
      const totalSec = ((Date.now() - startMs) / 1000);
      setImages((prev) => prev.map((img) =>
        img.id === id ? { ...img, base64: results[0]?.base64, revised_prompt: results[0]?.revised_prompt, loading: false, elapsedSec: totalSec } : img,
      ));
      // 保存图片到文件 + 数据库
      for (const r of results) {
        if (r.base64) {
          api.saveImage({
            base64: r.base64,
            prompt,
            revised_prompt: r.revised_prompt,
            format: format || "png",
            width: sizePresets[safeSizeIdx].w,
            height: sizePresets[safeSizeIdx].h,
            model_id: imageModelId,
            endpoint_id: imageEndpoint.id,
            generate_seconds: totalSec,
            source: "image_gen",
          }).catch((e) => console.error("保存图片失败:", e));
        }
      }
    } catch (err) {
      const totalSec = ((Date.now() - startMs) / 1000);
      setImages((prev) => prev.map((img) =>
        img.id === id ? { ...img, loading: false, revised_prompt: String(err), elapsedSec: totalSec } : img,
      ));
    } finally {
      if (timerRef.current) { clearInterval(timerRef.current); timerRef.current = null; }
      setGenerating(false);
      setPrompt("");
    }
  };

  return (
    <div className="flex h-full">
      {/* 左侧参数 */}
      <div className="w-80 border-r border-[var(--border-subtle)] p-5 flex flex-col shrink-0"
        style={{ backgroundColor: "var(--sidebar-bg)" }}>
        <FadeIn>
          <Label>{t("media.prompt")}</Label>
          <Textarea value={prompt} onChange={(e) => setPrompt(e.target.value)} className="h-32 mb-4" placeholder={t("media.promptPlaceholder")} />

          <Label>{t("media.size")}</Label>
          <div className="flex gap-2 mb-4">
            {sizePresets.map((p, i) => (
              <button key={p.label} onClick={() => setSizeIdx(i)}
                className={cn(
                  "px-3 py-1.5 rounded-lg text-xs font-medium transition-all duration-200",
                  safeSizeIdx === i ? "bg-brand-600 text-white shadow-md shadow-brand-600/20" : "bg-[var(--input-bg)] text-[var(--text-muted)] hover:bg-[var(--hover-bg)]",
                )}>
                {p.label}
              </button>
            ))}
          </div>

          <Label>{t("media.model")}</Label>
          <p className="text-xs text-[var(--text-secondary)] px-2 py-1.5 bg-[var(--surface-1)] rounded">
            {imageEndpoint ? `${imageEndpoint.name} / ${imageModelId}` : "未配置图片模型 → 去设置"}
          </p>

          <Label>{t("media.quality")}</Label>
          <Select className="w-full mb-4" value={quality} onChange={(e) => setQuality(e.target.value)}>
            <option value="auto">auto</option><option value="low">low</option><option value="medium">medium</option><option value="high">high</option>
          </Select>

          <Label>格式</Label>
          <Select className="w-full mb-4" value={format} onChange={(e) => setFormat(e.target.value)}>
            <option value="png">png</option><option value="jpeg">jpeg</option><option value="webp">webp</option>
          </Select>

          <Label>背景</Label>
          <Select className="w-full mb-4" value={background} onChange={(e) => setBackground(e.target.value)}>
            <option value="auto">auto</option><option value="opaque">opaque</option><option value="transparent">transparent</option>
          </Select>

          <Button className="mt-auto w-full" onClick={handleGenerate} disabled={!prompt.trim() || !imageEndpoint || generating}>
            {generating
              ? <><Loader2 size={14} className="animate-spin" /> ⏳ 等待服务端生成... {elapsedSec}s</>
              : <><Sparkles size={14} /> {t("media.generate")}</>}
          </Button>
          {!imageEndpoint && <p className="text-xs text-amber-500 mt-2 text-center">请先在设置中配置 AI 端点</p>}
        </FadeIn>
      </div>

      {/* 右侧画廊 */}
      <ScrollArea className="flex-1">
        <div className="p-6">
          {images.length === 0 ? (
            <EmptyState icon={<Image size={48} />} title={t("media.inputPromptHint")} />
          ) : (
            <div className="grid grid-cols-2 lg:grid-cols-3 gap-4">
              {images.map((img, i) => (
                <FadeIn key={img.id} delay={i * 0.05}>
                  <GlassCard className="group relative overflow-hidden p-0">
                    <div className="aspect-square bg-[var(--input-bg)] flex items-center justify-center overflow-hidden">
                      {img.loading ? (
                        <div className="flex flex-col items-center gap-2">
                          <Loader2 size={24} className="text-brand-400 animate-spin" />
                          <span className="text-xs text-[var(--text-muted)]">⏳ 等待服务端生成... {elapsedSec}s</span>
                        </div>
                      ) : img.base64 ? (
                        <img src={`data:image/png;base64,${img.base64}`} alt={img.prompt} className="w-full h-full object-cover" />
                      ) : img.filePath ? (
                        <img src={convertFileSrc(img.filePath)} alt={img.prompt} className="w-full h-full object-cover" />
                      ) : (
                        <Image size={32} className="text-[var(--text-muted)]" />
                      )}
                    </div>
                    <div className="p-3">
                      <p className="text-xs text-[var(--text-muted)] truncate">{img.prompt}</p>
                      {img.revised_prompt && !img.base64 && !img.filePath && <p className="text-xs text-red-400 truncate mt-0.5">{img.revised_prompt}</p>}
                      <span className="text-[10px] text-[var(--text-placeholder)]">{img.time}{img.elapsedSec != null ? ` · ${img.elapsedSec.toFixed(1)}s` : ""}</span>
                    </div>
                    {(img.base64 || img.filePath) && (
                      <div className="absolute top-2 right-2 flex gap-1 opacity-0 group-hover:opacity-100 transition-opacity">
                        <Button variant="secondary" size="icon" className="h-7 w-7 backdrop-blur-md"
                          onClick={() => {
                            const imgSrc = img.base64 ? `data:image/png;base64,${img.base64}` : img.filePath ? convertFileSrc(img.filePath) : "";
                            const w = window.open("", "_blank", "width=1200,height=900");
                            if (w) { w.document.write(`<img src="${imgSrc}" style="max-width:100%;max-height:100vh" />`); w.document.title = img.prompt || "预览"; }
                          }}><Maximize2 size={12} /></Button>
                        <Button variant="secondary" size="icon" className="h-7 w-7 backdrop-blur-md"
                          onClick={() => {
                            const link = document.createElement("a");
                            link.href = img.base64 ? `data:image/png;base64,${img.base64}` : img.filePath ? convertFileSrc(img.filePath) : "";
                            link.download = `${(img.prompt || "image").slice(0, 30)}.png`;
                            link.click();
                          }}><Download size={12} /></Button>
                        <Button variant="secondary" size="icon" className="h-7 w-7 backdrop-blur-md text-red-400"
                          onClick={() => setImages((prev) => prev.filter((x) => x.id !== img.id))}>
                          <Trash2 size={12} />
                        </Button>
                      </div>
                    )}
                  </GlassCard>
                </FadeIn>
              ))}
            </div>
          )}
        </div>
      </ScrollArea>
    </div>
  );
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  视频生成面板
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

const VIDEO_ASPECT_PRESETS = [
  { label: "16:9", size: "1920x1080" },
  { label: "9:16", size: "1080x1920" },
  { label: "1:1", size: "1080x1080" },
];
const VIDEO_DURATION_PRESETS = [5, 10, 15, 20];

function VideoGenPanel() {
  const { t } = useTranslation();
  const config = useAppStore((s) => s.config);
  const [prompt, setPrompt] = useState("");
  const [aspectIdx, setAspectIdx] = useState(0);
  const [duration, setDuration] = useState(10);
  const [n, setN] = useState(1);
  const [generating, setGenerating] = useState(false);
  const [status, setStatus] = useState("");
  const [error, setError] = useState("");
  const [result, setResult] = useState<{ file_path?: string; elapsed?: number } | null>(null);
  const taskIdRef = useRef<string>("");

  // 找到有 video 能力的端点和模型
  const videoEndpoint = config?.endpoints.find(
    (ep) => ep.enabled && ep.endpoint_type !== "azure_speech" && ep.models.some((m) => m.capabilities.includes("video")),
  );
  const videoModelId = videoEndpoint?.models.find((m) => m.capabilities.includes("video"))?.model_id || "sora-2";

  useEffect(() => {
    let unlisten: (() => void) | null = null;
    api.onVideoProgress((ev) => {
      if (ev.task_id !== taskIdRef.current) return;
      if (ev.status === "completed") {
        setGenerating(false);
        setStatus("");
        setResult({ file_path: ev.file_path, elapsed: ev.elapsed_seconds });
      } else if (ev.status === "failed") {
        setGenerating(false);
        setStatus("");
        setError(ev.error || "生成失败");
      } else {
        setStatus(ev.message || ev.status);
      }
    }).then((fn) => { unlisten = fn; });
    return () => { unlisten?.(); };
  }, []);

  const handleGenerate = async () => {
    if (!prompt.trim() || !videoEndpoint || generating) return;
    setGenerating(true);
    setError("");
    setResult(null);
    setStatus("正在提交...");
    try {
      const taskId = await api.generateVideo({
        prompt: prompt.trim(),
        model: videoModelId,
        endpoint_id: videoEndpoint.id,
        size: VIDEO_ASPECT_PRESETS[aspectIdx].size,
        duration_seconds: duration,
        n,
      });
      taskIdRef.current = taskId;
    } catch (e: any) {
      setGenerating(false);
      setStatus("");
      setError(String(e));
    }
  };

  return (
    <div className="flex h-full">
      {/* 左侧参数 */}
      <div className="w-[260px] shrink-0 border-r border-[var(--border-subtle)] p-4 space-y-4 overflow-y-auto">
        <Label className="text-xs">纵横比</Label>
        <div className="flex gap-1">
          {VIDEO_ASPECT_PRESETS.map((p, i) => (
            <button
              key={p.label}
              onClick={() => setAspectIdx(i)}
              className={cn(
                "px-3 py-1 rounded text-xs transition-colors",
                i === aspectIdx
                  ? "text-white"
                  : "text-[var(--text-secondary)]",
              )}
              style={{
                background: i === aspectIdx ? 'rgb(var(--color-brand))' : 'var(--input-bg)',
                border: `1px solid ${i === aspectIdx ? 'rgb(var(--color-brand))' : 'var(--input-border)'}`,
              }}
            >
              {p.label}
            </button>
          ))}
        </div>

        <Label className="text-xs">时长</Label>
        <Select value={String(duration)} onChange={(e) => setDuration(Number(e.target.value))}>
          {VIDEO_DURATION_PRESETS.map((d) => (
            <option key={d} value={d}>{d}秒</option>
          ))}
        </Select>

        <Label className="text-xs">变体数</Label>
        <Input type="number" min={1} max={4} value={n} onChange={(e) => setN(Number(e.target.value) || 1)} />

        <Label className="text-xs">模型</Label>
        <div className="text-xs text-[var(--text-muted)] px-1">
          {videoEndpoint ? `${videoEndpoint.name} / ${videoModelId}` : "未配置视频模型 → 去设置"}
        </div>

        <Textarea
          value={prompt}
          onChange={(e) => setPrompt(e.target.value)}
          className="h-28"
          placeholder="描述你想要生成的视频..."
        />

        <Button
          className="w-full"
          onClick={handleGenerate}
          disabled={!prompt.trim() || !videoEndpoint || generating}
        >
          {generating ? <><Loader2 size={14} className="animate-spin" /> 生成中...</> : <><Sparkles size={14} /> 生成</>}
        </Button>

        {!videoEndpoint && (
          <p className="text-xs text-center" style={{ color: '#ef4444' }}>
            请先在设置中配置含视频能力的模型（如 sora-2）
          </p>
        )}
      </div>

      {/* 右侧结果 */}
      <div className="flex-1 flex items-center justify-center p-6">
        {error && (
          <div className="text-center space-y-2">
            <X size={48} style={{ color: '#ef4444' }} className="mx-auto" />
            <p className="text-sm" style={{ color: '#ef4444' }}>{error}</p>
            <Button variant="secondary" size="sm" onClick={() => setError("")}>
              关闭
            </Button>
          </div>
        )}
        {generating && status && (
          <div className="text-center space-y-3">
            <Loader2 size={48} className="animate-spin mx-auto" style={{ color: 'var(--active-text)' }} />
            <p className="text-sm text-[var(--text-secondary)]">{status}</p>
          </div>
        )}
        {result?.file_path && (
          <div className="text-center space-y-3 max-w-lg">
            <video
              src={convertFileSrc(result.file_path)}
              controls
              autoPlay
              loop
              className="rounded-lg max-h-[400px] mx-auto"
              style={{ border: '1px solid var(--border-subtle)' }}
            />
            <p className="text-xs text-[var(--text-muted)]">
              耗时 {result.elapsed?.toFixed(1)}s
            </p>
          </div>
        )}
        {!error && !generating && !result && (
          <EmptyState
            icon={<Video size={48} />}
            title={t("media.videoGen")}
            description="在左侧输入提示词，选择参数后生成视频"
          />
        )}
      </div>
    </div>
  );
}

// ── 工具函数 ──

function now() {
  return new Date().toLocaleTimeString("zh-CN", { hour: "2-digit", minute: "2-digit", second: "2-digit" });
}

function genId() {
  return Date.now().toString(36) + Math.random().toString(36).slice(2, 6);
}
