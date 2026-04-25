import { useState, useRef, useEffect, useCallback } from "react";
import { useTranslation } from "react-i18next";
import {
  Image, Video, Sparkles, Send, Download, Trash2,
  Maximize2, Plus, Loader2,
} from "lucide-react";
import { cn } from "../lib/utils";
import {
  Button, GlassCard, Input, Textarea, Select, Label,
  Tabs, TabsList, TabsTrigger, TabsContent,
  FadeIn, EmptyState, ScrollArea,
} from "../components/ui";
import { useAppStore } from "../stores/app-store";
import { api, type ImageGenResult, type StreamTokenEvent } from "../lib/tauri-api";

const SIZE_PRESETS = [
  { label: "1:1", w: 1024, h: 1024 },
  { label: "16:9", w: 1792, h: 1024 },
  { label: "9:16", w: 1024, h: 1792 },
  { label: "4:3", w: 1024, h: 768 },
];

interface GeneratedImage {
  id: string;
  prompt: string;
  base64?: string;
  revised_prompt?: string;
  time: string;
  loading?: boolean;
}

export function MediaStudioView() {
  const { t } = useTranslation();

  return (
    <div className="flex flex-col h-full">
      <Tabs defaultValue="image" className="flex flex-col h-full">
        {/* Tab 栏 */}
        <div className="flex items-center gap-4 px-6 py-2.5 border-b border-white/[0.06] bg-white/[0.02]">
          <h1 className="text-base font-semibold text-slate-100 mr-2">{t("media.title")}</h1>
          <TabsList>
            <TabsTrigger value="image"><Image size={14} /> {t("media.imageGen")}</TabsTrigger>
            <TabsTrigger value="video"><Video size={14} /> {t("media.videoGen")}</TabsTrigger>
            <TabsTrigger value="insight"><Sparkles size={14} /> {t("media.aiInsight")}</TabsTrigger>
          </TabsList>
        </div>

        <TabsContent value="image" className="flex-1 overflow-hidden">
          <ImageGenPanel />
        </TabsContent>
        <TabsContent value="video" className="flex-1 overflow-hidden">
          <VideoGenPanel />
        </TabsContent>
        <TabsContent value="insight" className="flex-1 overflow-hidden">
          <InsightPanel />
        </TabsContent>
      </Tabs>
    </div>
  );
}

// ── 图片生成：接真实后端 ──

function ImageGenPanel() {
  const { t } = useTranslation();
  const config = useAppStore((s) => s.config);
  const [prompt, setPrompt] = useState("");
  const [sizeIdx, setSizeIdx] = useState(0);
  const [model, setModel] = useState("gpt-image-2");
  const [quality, setQuality] = useState("standard");
  const [images, setImages] = useState<GeneratedImage[]>([]);
  const [generating, setGenerating] = useState(false);

  // 找到可用的图片生成端点
  const imageEndpoint = config?.endpoints.find(
    (ep) => ep.enabled && ["azure_open_ai", "open_ai", "custom"].includes(ep.endpoint_type),
  );

  const handleGenerate = async () => {
    if (!prompt.trim() || !imageEndpoint || generating) return;

    const id = Date.now().toString();
    const placeholder: GeneratedImage = {
      id,
      prompt,
      time: new Date().toLocaleTimeString().slice(0, 5),
      loading: true,
    };
    setImages((prev) => [placeholder, ...prev]);
    setGenerating(true);

    try {
      const results: ImageGenResult[] = await api.generateImage({
        prompt,
        width: SIZE_PRESETS[sizeIdx].w,
        height: SIZE_PRESETS[sizeIdx].h,
        model,
        quality,
        endpoint_id: imageEndpoint.id,
      });

      setImages((prev) =>
        prev.map((img) =>
          img.id === id
            ? {
                ...img,
                base64: results[0]?.base64,
                revised_prompt: results[0]?.revised_prompt,
                loading: false,
              }
            : img,
        ),
      );
    } catch (err) {
      setImages((prev) =>
        prev.map((img) =>
          img.id === id ? { ...img, loading: false, revised_prompt: String(err) } : img,
        ),
      );
    } finally {
      setGenerating(false);
      setPrompt("");
    }
  };

  return (
    <div className="flex h-full">
      {/* 左侧参数面板 */}
      <div className="w-80 border-r border-white/[0.06] p-5 flex flex-col shrink-0 bg-white/[0.01]">
        <FadeIn>
          <Label>{t("media.prompt")}</Label>
          <Textarea
            value={prompt}
            onChange={(e) => setPrompt(e.target.value)}
            className="h-32 mb-4"
            placeholder={t("media.promptPlaceholder")}
          />

          <Label>{t("media.size")}</Label>
          <div className="flex gap-2 mb-4">
            {SIZE_PRESETS.map((p, i) => (
              <button
                key={p.label}
                onClick={() => setSizeIdx(i)}
                className={cn(
                  "px-3 py-1.5 rounded-lg text-xs font-medium transition-all duration-200",
                  sizeIdx === i
                    ? "bg-brand-600 text-white shadow-md shadow-brand-600/20"
                    : "bg-white/5 text-slate-400 hover:bg-white/10",
                )}
              >
                {p.label}
              </button>
            ))}
          </div>

          <Label>{t("media.model")}</Label>
          <Select className="w-full mb-3" value={model} onChange={(e) => setModel(e.target.value)}>
            <option>gpt-image-2</option>
            <option>dall-e-3</option>
          </Select>

          <Label>{t("media.quality")}</Label>
          <Select className="w-full mb-4" value={quality} onChange={(e) => setQuality(e.target.value)}>
            <option>standard</option>
            <option>high</option>
            <option>low</option>
          </Select>

          <Button
            className="mt-auto w-full"
            onClick={handleGenerate}
            disabled={!prompt.trim() || !imageEndpoint || generating}
          >
            {generating ? (
              <><Loader2 size={14} className="animate-spin" /> {t("media.generating")}</>
            ) : (
              <><Sparkles size={14} /> {t("media.generate")}</>
            )}
          </Button>

          {!imageEndpoint && (
            <p className="text-xs text-amber-400/70 mt-2 text-center">
              请先在设置中配置 AI 端点
            </p>
          )}
        </FadeIn>
      </div>

      {/* 右侧画廊 */}
      <ScrollArea className="flex-1">
        <div className="p-6">
          {images.length === 0 ? (
            <EmptyState
              icon={<Image size={48} />}
              title={t("media.inputPromptHint")}
            />
          ) : (
            <div className="grid grid-cols-2 lg:grid-cols-3 gap-4">
              {images.map((img, i) => (
                <FadeIn key={img.id} delay={i * 0.05}>
                  <GlassCard className="group relative overflow-hidden p-0">
                    {/* 图片区 */}
                    <div className="aspect-square bg-white/[0.02] flex items-center justify-center overflow-hidden">
                      {img.loading ? (
                        <div className="flex flex-col items-center gap-2">
                          <Loader2 size={24} className="text-brand-400 animate-spin" />
                          <span className="text-xs text-slate-500">{t("media.generating")}</span>
                        </div>
                      ) : img.base64 ? (
                        <img
                          src={`data:image/png;base64,${img.base64}`}
                          alt={img.prompt}
                          className="w-full h-full object-cover"
                        />
                      ) : (
                        <Image size={32} className="text-slate-600" />
                      )}
                    </div>

                    {/* 信息区 */}
                    <div className="p-3">
                      <p className="text-xs text-slate-400 truncate">{img.prompt}</p>
                      {img.revised_prompt && !img.base64 && (
                        <p className="text-xs text-red-400 truncate mt-0.5">{img.revised_prompt}</p>
                      )}
                      <span className="text-[10px] text-slate-600">{img.time}</span>
                    </div>

                    {/* 悬浮操作 */}
                    {img.base64 && (
                      <div className="absolute top-2 right-2 flex gap-1 opacity-0 group-hover:opacity-100 transition-opacity">
                        <Button variant="secondary" size="icon" className="h-7 w-7 backdrop-blur-md">
                          <Maximize2 size={12} />
                        </Button>
                        <Button variant="secondary" size="icon" className="h-7 w-7 backdrop-blur-md">
                          <Download size={12} />
                        </Button>
                        <Button
                          variant="secondary" size="icon"
                          className="h-7 w-7 backdrop-blur-md text-red-400"
                          onClick={() => setImages((prev) => prev.filter((x) => x.id !== img.id))}
                        >
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

// ── 视频生成 (Shell) ──

function VideoGenPanel() {
  const { t } = useTranslation();
  return (
    <EmptyState
      icon={<Video size={48} />}
      title={t("media.videoGen")}
      description={t("media.videoHint")}
      action={<Button><Plus size={14} /> {t("media.newVideoTask")}</Button>}
    />
  );
}

// ── AI 洞察：真实流式补全 ──

function InsightPanel() {
  const { t } = useTranslation();
  const config = useAppStore((s) => s.config);
  const [input, setInput] = useState("");
  const [messages, setMessages] = useState<{ role: string; content: string }[]>([
    { role: "assistant", content: t("media.aiGreeting") },
  ]);
  const [streaming, setStreaming] = useState(false);
  const [streamBuffer, setStreamBuffer] = useState("");
  const scrollRef = useRef<HTMLDivElement>(null);

  const aiEndpoint = config?.endpoints.find(
    (ep) => ep.enabled && ["azure_open_ai", "open_ai", "custom"].includes(ep.endpoint_type),
  );

  // 自动滚到底部
  useEffect(() => {
    if (scrollRef.current) {
      scrollRef.current.scrollTop = scrollRef.current.scrollHeight;
    }
  }, [messages, streamBuffer]);

  const handleSend = useCallback(async () => {
    if (!input.trim() || streaming) return;

    const userMsg = { role: "user", content: input };
    const newMessages = [...messages, userMsg];
    setMessages(newMessages);
    setInput("");

    if (!aiEndpoint) {
      setMessages((prev) => [
        ...prev,
        { role: "assistant", content: "请先在设置中配置 AI 端点" },
      ]);
      return;
    }

    setStreaming(true);
    setStreamBuffer("");

    try {
      // 启动流式请求
      const streamId = await api.aiCompleteStream({
        messages: newMessages.map((m) => ({ role: m.role, content: m.content })),
        model: "gpt-4.1",
        temperature: 0.7,
        max_tokens: 4096,
        endpoint_id: aiEndpoint.id,
      });

      let buffer = "";
      const unlisten = await api.onStreamToken((event: StreamTokenEvent) => {
        if (event.stream_id !== streamId) return;
        if (event.done) {
          setMessages((prev) => [...prev, { role: "assistant", content: buffer }]);
          setStreamBuffer("");
          setStreaming(false);
          unlisten();
          return;
        }
        if (event.error) {
          setMessages((prev) => [...prev, { role: "assistant", content: `错误: ${event.error}` }]);
          setStreamBuffer("");
          setStreaming(false);
          unlisten();
          return;
        }
        if (event.token) {
          buffer += event.token;
          setStreamBuffer(buffer);
        }
      });
    } catch (err) {
      setMessages((prev) => [
        ...prev,
        { role: "assistant", content: `请求失败: ${err}` },
      ]);
      setStreaming(false);
    }
  }, [input, messages, streaming, aiEndpoint]);

  return (
    <div className="flex flex-col h-full">
      {/* 消息区 */}
      <div ref={scrollRef} className="flex-1 overflow-y-auto p-6 space-y-3">
        {messages.map((msg, i) => (
          <FadeIn key={i} delay={0}>
            <div className={cn("max-w-2xl", msg.role === "user" ? "ml-auto" : "")}>
              <GlassCard
                className={cn(
                  "px-4 py-3",
                  msg.role === "user"
                    ? "bg-brand-600/10 border-brand-500/15"
                    : "",
                )}
              >
                <p className="text-sm text-slate-300 whitespace-pre-wrap">{msg.content}</p>
              </GlassCard>
            </div>
          </FadeIn>
        ))}

        {/* 流式输出 */}
        {streamBuffer && (
          <div className="max-w-2xl">
            <GlassCard className="px-4 py-3">
              <p className="text-sm text-slate-300 whitespace-pre-wrap">
                {streamBuffer}
                <span className="inline-block w-1.5 h-4 bg-brand-400 animate-pulse ml-0.5 align-middle rounded-sm" />
              </p>
            </GlassCard>
          </div>
        )}
      </div>

      {/* 输入区 */}
      <div className="border-t border-white/[0.06] bg-white/[0.02] p-4">
        <div className="flex gap-2 max-w-3xl mx-auto">
          <Input
            value={input}
            onChange={(e) => setInput(e.target.value)}
            onKeyDown={(e) => e.key === "Enter" && !e.shiftKey && (e.preventDefault(), handleSend())}
            placeholder={t("media.inputPlaceholder")}
            className="flex-1"
          />
          <Button onClick={handleSend} disabled={!input.trim() || streaming} className="px-5">
            {streaming ? <Loader2 size={16} className="animate-spin" /> : <Send size={16} />}
          </Button>
        </div>
        {!aiEndpoint && (
          <p className="text-xs text-amber-400/70 mt-2 text-center">
            请先在设置中配置 AI 端点
          </p>
        )}
      </div>
    </div>
  );
}
