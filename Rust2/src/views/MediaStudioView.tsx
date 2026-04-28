import { useState, useRef, useEffect, useCallback } from "react";
import {
  Image, Video, Send, Trash2, Plus, Loader2,
  MessageSquare, X,
  PanelLeftClose, PanelLeftOpen, ImagePlus,
} from "lucide-react";
import { cn } from "../lib/utils";
import {
  Button, EmptyState, ScrollArea, Badge,
} from "../components/ui";
import { useAppStore } from "../stores/app-store";
import {
  api,
  type StudioSession, type StudioMessage, type StudioMediaRef,
  type StudioSessionBundle, type StudioTask, type StudioReferenceImage,
  type StudioTaskEvent, type StudioMessageDelta,
} from "../lib/tauri-api";
import { convertFileSrc } from "@tauri-apps/api/core";
import { MarkdownRenderer } from "../components/MarkdownRenderer";

const LRU_MAX = 3;

export function MediaStudioView() {
  const config = useAppStore((s) => s.config);
  const [sessions, setSessions] = useState<StudioSession[]>([]);
  const [activeSessionId, setActiveSessionId] = useState<string | null>(null);
  const [sidebarOpen, setSidebarOpen] = useState(true);
  const [renamingId, setRenamingId] = useState<string | null>(null);
  const [renameText, setRenameText] = useState("");
  const loadedSessions = useRef<Map<string, FullSession>>(new Map());
  const accessOrder = useRef<string[]>([]);
  const [currentBundle, setCurrentBundle] = useState<StudioSessionBundle | null>(null);
  const [streamingText, setStreamingText] = useState("");
  const [streamingReasoning, setStreamingReasoning] = useState("");
  const [streamingMsgId, setStreamingMsgId] = useState<string | null>(null);
  const [isStreaming, setIsStreaming] = useState(false);
  const [input, setInput] = useState("");
  const scrollRef = useRef<HTMLDivElement>(null);
  const [imgWidth, setImgWidth] = useState(1024);
  const [imgHeight, setImgHeight] = useState(1024);
  const [imgQuality, setImgQuality] = useState("auto");
  const [imgFormat, setImgFormat] = useState("png");
  const [imgCount, setImgCount] = useState(1);
  const [imgBackground, setImgBackground] = useState("auto");
  const [vidSize, setVidSize] = useState("1080x1920");
  const [vidDuration, setVidDuration] = useState(10);
  const [vidCount, setVidCount] = useState(1);
  const [referenceImages, setReferenceImages] = useState<StudioReferenceImage[]>([]);
  const [runningTasks, setRunningTasks] = useState<StudioTask[]>([]);
  const activeSession = sessions.find(s => s.id === activeSessionId);
  const sessionType = activeSession?.session_type || "chat";

  useEffect(() => {
    api.studioListSessions(30, 0).then(setSessions).catch(console.error);
  }, []);

  useEffect(() => {
    let unlisten: (() => void) | null = null;
    api.onStudioMessageDelta((e: StudioMessageDelta) => {
      if (e.done) {
        setIsStreaming(false);
        setStreamingMsgId(null);
        if (activeSessionId) {
          api.studioGetSessionBundle(activeSessionId).then(b => {
            setCurrentBundle(b);
            setStreamingText("");
            setStreamingReasoning("");
          });
        }
        return;
      }
      if (e.token) setStreamingText(prev => prev + e.token);
      if (e.reasoning) setStreamingReasoning(prev => prev + e.reasoning);
      if (e.message_id) setStreamingMsgId(e.message_id);
    }).then(fn => { unlisten = fn; });
    return () => { unlisten?.(); };
  }, [activeSessionId]);

  useEffect(() => {
    let unlisten: (() => void) | null = null;
    api.onStudioTaskUpdate((e: StudioTaskEvent) => {
      if (e.status === "completed" || e.status === "failed") {
        setRunningTasks(prev => prev.filter(t => t.id !== e.task_id));
        if (activeSessionId && e.session_id === activeSessionId) {
          api.studioGetSessionBundle(activeSessionId).then(setCurrentBundle);
        }
      } else {
        setRunningTasks(prev => prev.map(t =>
          t.id === e.task_id ? { ...t, progress: e.progress ?? t.progress, status: e.status } : t
        ));
      }
    }).then(fn => { unlisten = fn; });
    return () => { unlisten?.(); };
  }, [activeSessionId]);

  useEffect(() => {
    let unlisten: (() => void) | null = null;
    api.onStudioMessageNew((e) => {
      if (e.session_id === activeSessionId && currentBundle) {
        setCurrentBundle(prev => {
          if (!prev) return prev;
          const msgs = [...prev.messages, e.message];
          const refs = { ...prev.media_refs };
          if (e.media_refs && e.media_refs.length > 0) {
            refs[e.message.id] = e.media_refs;
          }
          return { ...prev, messages: msgs, media_refs: refs };
        });
      }
    }).then(fn => { unlisten = fn; });
    return () => { unlisten?.(); };
  }, [activeSessionId, currentBundle]);

  useEffect(() => {
    if (scrollRef.current) scrollRef.current.scrollTop = scrollRef.current.scrollHeight;
  }, [currentBundle?.messages, streamingText]);

  const handleSelectSession = useCallback(async (id: string) => {
    if (activeSessionId && currentBundle) {
      loadedSessions.current.set(activeSessionId, { bundle: currentBundle, refImages: referenceImages });
    }
    setActiveSessionId(id);
    setStreamingText(""); setStreamingReasoning(""); setStreamingMsgId(null); setIsStreaming(false);
    accessOrder.current = accessOrder.current.filter(x => x !== id);
    accessOrder.current.push(id);
    while (accessOrder.current.length > LRU_MAX) {
      const evicted = accessOrder.current.shift();
      if (evicted) loadedSessions.current.delete(evicted);
    }
    const cached = loadedSessions.current.get(id);
    if (cached) {
      setCurrentBundle(cached.bundle); setReferenceImages(cached.refImages);
    } else {
      try {
        const [bundle, refs, tasks] = await Promise.all([
          api.studioGetSessionBundle(id), api.studioListReferenceImages(id), api.studioListRunningTasks(id),
        ]);
        setCurrentBundle(bundle); setReferenceImages(refs); setRunningTasks(tasks);
        loadedSessions.current.set(id, { bundle, refImages: refs });
      } catch (err) { console.error("加载会话失败:", err); }
    }
  }, [activeSessionId, currentBundle, referenceImages]);

  const handleNewSession = useCallback(async (type: string) => {
    const nameMap: Record<string, string> = { chat: "新对话", image: "新图片", video: "新视频" };
    try {
      const session = await api.studioCreateSession(type, nameMap[type] || "新会话");
      setSessions(prev => [session, ...prev]);
      handleSelectSession(session.id);
    } catch (err) { console.error(err); }
  }, [handleSelectSession]);

  const handleDeleteSession = useCallback(async (id: string) => {
    try {
      await api.studioSoftDeleteSession(id);
      setSessions(prev => prev.filter(s => s.id !== id));
      if (activeSessionId === id) { setActiveSessionId(null); setCurrentBundle(null); }
    } catch (err) { console.error(err); }
  }, [activeSessionId]);

  const handleRename = useCallback(async (id: string) => {
    const trimmed = renameText.trim();
    if (!trimmed) { setRenamingId(null); return; }
    try {
      await api.studioRenameSession(id, trimmed);
      setSessions(prev => prev.map(s => s.id === id ? { ...s, name: trimmed } : s));
    } catch (err) { console.error(err); }
    setRenamingId(null);
  }, [renameText]);

  const handleSendChat = useCallback(async () => {
    if (!input.trim() || !activeSessionId || isStreaming) return;
    const text = input.trim(); setInput(""); setIsStreaming(true); setStreamingText(""); setStreamingReasoning("");
    const ep = config?.endpoints?.find(e => e.enabled);
    const endpointId = ep?.id || "";
    const model = ep?.models?.find(m => m.capabilities.includes("text"))?.model_id || "";
    try {
      const resultJson = await api.studioChatStream(activeSessionId, text, endpointId, model);
      const result = JSON.parse(resultJson);
      if (result.user_message) {
        setCurrentBundle(prev => prev ? { ...prev, messages: [...prev.messages, result.user_message] } : prev);
      }
      setStreamingMsgId(result.assistant_message_id);
    } catch (err) { console.error("Chat stream failed:", err); setIsStreaming(false); }
  }, [input, activeSessionId, isStreaming, config]);

  const handleGenerateImage = useCallback(async () => {
    if (!input.trim() || !activeSessionId) return;
    const text = input.trim(); setInput("");
    try {
      const userMsg = await api.studioAppendMessage({ sessionId: activeSessionId, role: "user", text, contentType: "text" });
      setCurrentBundle(prev => prev ? { ...prev, messages: [...prev.messages, userMsg] } : prev);
    } catch {}
    const ep = config?.endpoints?.find(e => e.enabled);
    const imgModel = ep?.models?.find(m => m.capabilities.includes("image"));
    try {
      const taskId = await api.studioStartImageTask({
        sessionId: activeSessionId, prompt: text,
        params: { width: imgWidth, height: imgHeight, quality: imgQuality, format: imgFormat, n: imgCount, background: imgBackground, model: imgModel?.model_id || "gpt-image-1", endpoint_id: ep?.id || "" },
        referencePaths: referenceImages.map(r => r.file_path),
      });
      setRunningTasks(prev => [...prev, { id: taskId, session_id: activeSessionId, task_type: "image", status: "running", prompt: text, progress: 0, has_reference_input: referenceImages.length > 0, created_at: new Date().toISOString(), updated_at: new Date().toISOString() } as StudioTask]);
    } catch (err) { console.error(err); }
  }, [input, activeSessionId, config, imgWidth, imgHeight, imgQuality, imgFormat, imgCount, imgBackground, referenceImages]);

  const handleGenerateVideo = useCallback(async () => {
    if (!input.trim() || !activeSessionId) return;
    const text = input.trim(); setInput("");
    try {
      const userMsg = await api.studioAppendMessage({ sessionId: activeSessionId, role: "user", text, contentType: "text" });
      setCurrentBundle(prev => prev ? { ...prev, messages: [...prev.messages, userMsg] } : prev);
    } catch {}
    const refPath = referenceImages.length > 0 ? referenceImages[0].file_path : undefined;
    const ep = config?.endpoints?.find(e => e.enabled);
    const vidModel = ep?.models?.find(m => m.capabilities.includes("video"));
    try {
      const taskId = await api.studioStartVideoTask({
        sessionId: activeSessionId, prompt: text,
        params: { model: vidModel?.model_id || "sora", endpoint_id: ep?.id || "", size: vidSize, duration_seconds: vidDuration, n: vidCount },
        referencePath: refPath,
      });
      setRunningTasks(prev => [...prev, { id: taskId, session_id: activeSessionId, task_type: "video", status: "running", prompt: text, progress: 0, has_reference_input: !!refPath, created_at: new Date().toISOString(), updated_at: new Date().toISOString() } as StudioTask]);
    } catch (err) { console.error(err); }
  }, [input, activeSessionId, config, referenceImages, vidSize, vidDuration, vidCount]);

  const handleAddReferenceImage = useCallback(async () => {
    if (!activeSessionId) return;
    try {
      const result = await (window as any).__TAURI__?.dialog?.open({ multiple: true, filters: [{ name: "Image", extensions: ["png", "jpg", "jpeg", "webp"] }] });
      if (!result) return;
      const paths: string[] = Array.isArray(result) ? result : [result];
      if (sessionType === "video" && (referenceImages.length + paths.length) > 1) {
        console.warn("Sora 当前仅支持 1 张参考图");
        return;
      }
      for (const p of paths) {
        const img = await api.studioAddReferenceImage(activeSessionId, p);
        setReferenceImages(prev => [...prev, img]);
      }
    } catch (err) { console.error(err); }
  }, [activeSessionId, referenceImages, sessionType]);

  const handleDeleteReferenceImage = useCallback(async (id: string) => {
    try { await api.studioDeleteReferenceImage(id); setReferenceImages(prev => prev.filter(r => r.id !== id)); } catch (err) { console.error(err); }
  }, []);

  const handleSend = useCallback(() => {
    if (sessionType === "image") handleGenerateImage();
    else if (sessionType === "video") handleGenerateVideo();
    else handleSendChat();
  }, [sessionType, handleGenerateImage, handleGenerateVideo, handleSendChat]);

  const isVideoRefLimitExceeded = sessionType === "video" && referenceImages.length > 1;

  return (
    <div className="flex h-full">
      {sidebarOpen && (
        <div className="w-80 border-r border-[var(--border-subtle)] flex flex-col bg-[var(--surface-0)]">
          <div className="flex items-center justify-between px-3 py-2 border-b border-[var(--border-subtle)]">
            <span className="text-sm font-semibold text-[var(--text-primary)]">创作工坊</span>
            <div className="flex items-center gap-1">
              <NewSessionMenu onCreate={handleNewSession} />
              <button onClick={() => setSidebarOpen(false)} className="p-1 rounded hover:bg-[var(--surface-2)]"><PanelLeftClose size={16} /></button>
            </div>
          </div>
          <ScrollArea className="flex-1">
            {sessions.map(s => (
              <div key={s.id} onClick={() => handleSelectSession(s.id)}
                onContextMenu={(e) => { e.preventDefault(); setRenamingId(s.id); setRenameText(s.name); }}
                className={cn("group flex items-center gap-2 px-3 py-2 cursor-pointer text-sm border-b border-[var(--border-subtle)] transition-colors",
                  s.id === activeSessionId ? "bg-brand-600/10 text-[var(--active-text)]" : "text-[var(--text-secondary)] hover:bg-[var(--surface-1)]")}>
                {s.session_type === "image" ? <Image size={14} /> : s.session_type === "video" ? <Video size={14} /> : <MessageSquare size={14} />}
                <div className="flex-1 min-w-0">
                  {renamingId === s.id ? (
                    <input autoFocus value={renameText} onChange={e => setRenameText(e.target.value)}
                      onBlur={() => handleRename(s.id)} onKeyDown={e => e.key === "Enter" && handleRename(s.id)}
                      className="w-full bg-transparent border-b border-[var(--active-text)] outline-none text-sm" />
                  ) : <span className="truncate block">{s.name}</span>}
                  {s.latest_message_preview && <span className="text-xs text-[var(--text-muted)] truncate block">{s.latest_message_preview}</span>}
                </div>
                <Badge variant="gray" className="text-[10px] shrink-0">
                  {s.session_type === "chat" ? "对话" : s.session_type === "image" ? "图片" : "视频"}
                </Badge>
                <button onClick={(e) => { e.stopPropagation(); handleDeleteSession(s.id); }}
                  className="p-0.5 rounded hover:bg-red-500/20 shrink-0 opacity-0 group-hover:opacity-100"><Trash2 size={12} /></button>
              </div>
            ))}
          </ScrollArea>
        </div>
      )}
      <div className="flex-1 flex flex-col min-w-0">
        {!sidebarOpen && (
          <div className="flex items-center px-3 py-1.5 border-b border-[var(--border-subtle)]">
            <button onClick={() => setSidebarOpen(true)} className="p-1 rounded hover:bg-[var(--surface-2)] mr-2"><PanelLeftOpen size={16} /></button>
            <span className="text-sm font-medium text-[var(--text-primary)]">{activeSession?.name || "创作工坊"}</span>
          </div>
        )}
        {!activeSessionId ? (
          <div className="flex-1 flex items-center justify-center">
            <EmptyState icon={<MessageSquare size={48} />} title="选择或新建会话" description="在左侧选择一个会话，或点击 + 新建" />
          </div>
        ) : (
          <>
            {sessionType === "image" && (
              <ImageParamsBar width={imgWidth} height={imgHeight} quality={imgQuality} format={imgFormat} count={imgCount} background={imgBackground}
                onWidthChange={setImgWidth} onHeightChange={setImgHeight} onQualityChange={setImgQuality} onFormatChange={setImgFormat} onCountChange={setImgCount} onBackgroundChange={setImgBackground} />
            )}
            {sessionType === "video" && (
              <VideoParamsBar size={vidSize} duration={vidDuration} count={vidCount} onSizeChange={setVidSize} onDurationChange={setVidDuration} onCountChange={setVidCount} />
            )}
            {(sessionType === "image" || sessionType === "video") && (
              <div className="flex items-center gap-2 px-4 py-2 border-b border-[var(--border-subtle)] bg-[var(--surface-0)]">
                <span className="text-xs text-[var(--text-muted)]">参考图:</span>
                {referenceImages.map(img => (
                  <div key={img.id} className="relative group">
                    <img src={convertFileSrc(img.file_path)} className="w-10 h-10 rounded object-cover border border-[var(--border-subtle)]" />
                    <button onClick={() => handleDeleteReferenceImage(img.id)}
                      className="absolute -top-1 -right-1 bg-red-500 text-white rounded-full p-0.5 opacity-0 group-hover:opacity-100 transition-opacity"><X size={10} /></button>
                  </div>
                ))}
                <button onClick={handleAddReferenceImage}
                  className="w-10 h-10 rounded border border-dashed border-[var(--border-subtle)] flex items-center justify-center hover:bg-[var(--surface-1)]">
                  <ImagePlus size={16} className="text-[var(--text-muted)]" />
                </button>
                {isVideoRefLimitExceeded && <span className="text-xs text-red-500 ml-2">Sora 当前仅支持 1 张参考图</span>}
              </div>
            )}
            {runningTasks.length > 0 && (
              <div className="px-4 py-1.5 border-b border-[var(--border-subtle)] bg-[var(--surface-0)]">
                {runningTasks.map(task => (
                  <div key={task.id} className="flex items-center gap-2 text-xs">
                    <Loader2 size={12} className="animate-spin text-[var(--active-text)]" />
                    <span className="text-[var(--text-secondary)]">{task.task_type === "image" ? "生成图片" : "生成视频"}... {Math.round((task.progress || 0) * 100)}%</span>
                    <div className="flex-1 h-1 bg-[var(--surface-2)] rounded-full overflow-hidden">
                      <div className="h-full bg-brand-600 transition-all duration-300" style={{ width: `${(task.progress || 0) * 100}%` }} />
                    </div>
                  </div>
                ))}
              </div>
            )}
            <div ref={scrollRef} className="flex-1 overflow-y-auto px-4 py-4 space-y-4">
              {currentBundle?.messages.map(msg => (
                <MessageBubble key={msg.id} message={msg} mediaRefs={currentBundle.media_refs[msg.id]} citations={currentBundle.citations[msg.id]} />
              ))}
              {isStreaming && streamingMsgId && (
                <div className="flex justify-start">
                  <div className="max-w-[80%] rounded-[10px_10px_10px_2px] px-4 py-3 bg-[var(--surface-1)] border border-[var(--border-subtle)]">
                    {streamingReasoning && <div className="text-xs text-[var(--text-muted)] mb-2 italic border-l-2 border-[var(--border-subtle)] pl-2">{streamingReasoning}</div>}
                    {streamingText ? <MarkdownRenderer content={streamingText} /> : <Loader2 size={16} className="animate-spin text-[var(--text-muted)]" />}
                  </div>
                </div>
              )}
            </div>
            <div className="px-4 py-3 border-t border-[var(--border-subtle)] bg-[var(--surface-0)]">
              <div className="flex gap-2">
                <textarea value={input} onChange={(e) => setInput(e.target.value)}
                  onKeyDown={(e) => { if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); handleSend(); } }}
                  placeholder={sessionType === "image" ? "描述你想生成的图片..." : sessionType === "video" ? "描述你想生成的视频..." : "输入消息..."}
                  className="flex-1 min-h-[40px] max-h-[120px] resize-none rounded-lg border border-[var(--border-subtle)] bg-[var(--surface-1)] px-3 py-2 text-sm text-[var(--text-primary)] placeholder:text-[var(--text-muted)] focus:outline-none focus:ring-1 focus:ring-brand-500"
                  rows={1} />
                <Button onClick={handleSend} disabled={!input.trim() || isStreaming || isVideoRefLimitExceeded} className="shrink-0">
                  {isStreaming ? <Loader2 size={16} className="animate-spin" /> : <Send size={16} />}
                </Button>
              </div>
            </div>
          </>
        )}
      </div>
    </div>
  );
}

function NewSessionMenu({ onCreate }: { onCreate: (type: string) => void }) {
  const [isOpen, setIsOpen] = useState(false);
  return (
    <div className="relative">
      <button onClick={() => setIsOpen(!isOpen)} className="p-1 rounded hover:bg-[var(--surface-2)]"><Plus size={16} /></button>
      {isOpen && (
        <div className="absolute right-0 top-full mt-1 bg-[var(--surface-1)] border border-[var(--border-subtle)] rounded-lg shadow-lg z-50 py-1 min-w-[140px]">
          {[{ type: "chat", icon: <MessageSquare size={14} />, label: "文本对话" }, { type: "image", icon: <Image size={14} />, label: "图片创作" }, { type: "video", icon: <Video size={14} />, label: "视频创作" }].map(item => (
            <button key={item.type} onClick={() => { onCreate(item.type); setIsOpen(false); }}
              className="flex items-center gap-2 w-full px-3 py-1.5 text-sm text-[var(--text-secondary)] hover:bg-[var(--surface-2)]">{item.icon} {item.label}</button>
          ))}
        </div>
      )}
    </div>
  );
}

function MessageBubble({ message, mediaRefs, citations }: { message: StudioMessage; mediaRefs?: StudioMediaRef[]; citations?: import("../lib/tauri-api").StudioCitation[] }) {
  const isUser = message.role === "user";
  return (
    <div className={cn("flex", isUser ? "justify-end" : "justify-start")}>
      <div className={cn("max-w-[80%] px-4 py-3 border border-[var(--border-subtle)]",
        isUser ? "rounded-[10px_10px_2px_10px] bg-brand-600/10" : "rounded-[10px_10px_10px_2px] bg-[var(--surface-1)]")}>
        {message.reasoning_text && <div className="text-xs text-[var(--text-muted)] mb-2 italic border-l-2 border-[var(--border-subtle)] pl-2">{message.reasoning_text}</div>}
        {message.content_type === "text" || !message.content_type ? (
          isUser ? <p className="text-sm text-[var(--text-primary)] whitespace-pre-wrap">{message.text}</p> : <MarkdownRenderer content={message.text} />
        ) : <p className="text-sm text-[var(--text-primary)]">{message.text}</p>}
        {mediaRefs && mediaRefs.length > 0 && (
          <div className="mt-2 grid grid-cols-2 gap-2">
            {mediaRefs.map(ref_ => (
              <div key={ref_.id} className="rounded overflow-hidden">
                {ref_.media_kind === "video" ? <video src={convertFileSrc(ref_.media_path)} controls className="w-full rounded" />
                  : <img src={convertFileSrc(ref_.media_path)} className="w-full rounded cursor-pointer hover:opacity-90 transition-opacity" loading="lazy" />}
              </div>
            ))}
          </div>
        )}
        {citations && citations.length > 0 && (
          <div className="mt-2 space-y-1">
            {citations.map(c => <a key={c.id} href={c.url} target="_blank" rel="noopener noreferrer" className="block text-xs text-brand-500 hover:underline truncate">[{c.citation_number}] {c.title}</a>)}
          </div>
        )}
        {!isUser && (message.generate_seconds || message.prompt_tokens) && (
          <div className="mt-1 text-[10px] text-[var(--text-muted)]">
            {message.generate_seconds && `${message.generate_seconds.toFixed(1)}s`}
            {message.prompt_tokens && ` · ${message.prompt_tokens}+${message.completion_tokens || 0} tokens`}
          </div>
        )}
      </div>
    </div>
  );
}

function ImageParamsBar(props: { width: number; height: number; quality: string; format: string; count: number; background: string;
  onWidthChange: (v: number) => void; onHeightChange: (v: number) => void; onQualityChange: (v: string) => void; onFormatChange: (v: string) => void; onCountChange: (v: number) => void; onBackgroundChange: (v: string) => void; }) {
  const sizePresets = [{ label: "1:1 (1024)", w: 1024, h: 1024 }, { label: "16:9 (1792x1024)", w: 1792, h: 1024 }, { label: "9:16 (1024x1792)", w: 1024, h: 1792 }, { label: "4:3 (1024x768)", w: 1024, h: 768 }, { label: "3:4 (768x1024)", w: 768, h: 1024 }];
  return (
    <div className="flex flex-wrap items-center gap-3 px-4 py-2 border-b border-[var(--border-subtle)] bg-[var(--surface-0)] text-xs">
      <label className="flex items-center gap-1">尺寸:
        <select value={`${props.width}x${props.height}`} onChange={e => { const [w, h] = e.target.value.split("x").map(Number); props.onWidthChange(w); props.onHeightChange(h); }}
          className="bg-[var(--surface-1)] border border-[var(--border-subtle)] rounded px-1.5 py-0.5 text-xs">
          {sizePresets.map(p => <option key={p.label} value={`${p.w}x${p.h}`}>{p.label}</option>)}
        </select>
      </label>
      <label className="flex items-center gap-1">质量:
        <select value={props.quality} onChange={e => props.onQualityChange(e.target.value)} className="bg-[var(--surface-1)] border border-[var(--border-subtle)] rounded px-1.5 py-0.5 text-xs">
          <option value="auto">自动</option><option value="low">低</option><option value="medium">中</option><option value="high">高</option>
        </select>
      </label>
      <label className="flex items-center gap-1">格式:
        <select value={props.format} onChange={e => props.onFormatChange(e.target.value)} className="bg-[var(--surface-1)] border border-[var(--border-subtle)] rounded px-1.5 py-0.5 text-xs">
          <option value="png">PNG</option><option value="jpeg">JPEG</option><option value="webp">WebP</option>
        </select>
      </label>
      <label className="flex items-center gap-1">数量:
        <select value={props.count} onChange={e => props.onCountChange(Number(e.target.value))} className="bg-[var(--surface-1)] border border-[var(--border-subtle)] rounded px-1.5 py-0.5 text-xs">
          {[1, 2, 4].map(n => <option key={n} value={n}>{n}</option>)}
        </select>
      </label>
      <label className="flex items-center gap-1">背景:
        <select value={props.background} onChange={e => props.onBackgroundChange(e.target.value)} className="bg-[var(--surface-1)] border border-[var(--border-subtle)] rounded px-1.5 py-0.5 text-xs">
          <option value="auto">自动</option><option value="transparent">透明</option><option value="opaque">不透明</option>
        </select>
      </label>
    </div>
  );
}

function VideoParamsBar(props: { size: string; duration: number; count: number; onSizeChange: (v: string) => void; onDurationChange: (v: number) => void; onCountChange: (v: number) => void; }) {
  return (
    <div className="flex items-center gap-3 px-4 py-2 border-b border-[var(--border-subtle)] bg-[var(--surface-0)] text-xs">
      <label className="flex items-center gap-1">分辨率:
        <select value={props.size} onChange={e => props.onSizeChange(e.target.value)} className="bg-[var(--surface-1)] border border-[var(--border-subtle)] rounded px-1.5 py-0.5 text-xs">
          <option value="1080x1920">1080x1920 (竖屏)</option><option value="1920x1080">1920x1080 (横屏)</option><option value="1080x1080">1080x1080 (方形)</option>
        </select>
      </label>
      <label className="flex items-center gap-1">时长:
        <select value={props.duration} onChange={e => props.onDurationChange(Number(e.target.value))} className="bg-[var(--surface-1)] border border-[var(--border-subtle)] rounded px-1.5 py-0.5 text-xs">
          <option value={5}>5秒</option><option value={10}>10秒</option><option value={15}>15秒</option><option value={20}>20秒</option>
        </select>
      </label>
      <label className="flex items-center gap-1">数量:
        <select value={props.count} onChange={e => props.onCountChange(Number(e.target.value))} className="bg-[var(--surface-1)] border border-[var(--border-subtle)] rounded px-1.5 py-0.5 text-xs">
          {[1, 2].map(n => <option key={n} value={n}>{n}</option>)}
        </select>
      </label>
    </div>
  );
}

interface FullSession { bundle: StudioSessionBundle; refImages: StudioReferenceImage[]; }
