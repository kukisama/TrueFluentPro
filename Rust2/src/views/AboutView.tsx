import { useState, useEffect } from "react";
import { useTranslation } from "react-i18next";
import { Info, Code2, Plug, HelpCircle } from "lucide-react";
import {
  GlassCard, Badge, FadeIn, Separator,
  Tabs, TabsList, TabsTrigger, TabsContent,
} from "../components/ui";
import { api, type AppInfo } from "../lib/tauri-api";

export function AboutView() {
  const { t } = useTranslation();
  const [appInfo, setAppInfo] = useState<AppInfo | null>(null);

  useEffect(() => {
    api.getAppInfo().then(setAppInfo).catch(() => {});
  }, []);

  return (
    <div className="flex flex-col h-full">
      <Tabs defaultValue="about" className="flex flex-col h-full">
        <div className="flex items-center gap-4 px-6 py-2.5 border-b border-[var(--border-subtle)]"
          style={{ backgroundColor: "var(--toolbar-bg)" }}>
          <h1 className="text-base font-semibold text-[var(--text-primary)] mr-2">{t("about.title")}</h1>
          <TabsList>
            <TabsTrigger value="about"><Info size={14} /> {t("about.title")}</TabsTrigger>
            <TabsTrigger value="help"><HelpCircle size={14} /> {t("about.help")}</TabsTrigger>
          </TabsList>
        </div>

        <TabsContent value="about" className="flex-1 overflow-y-auto p-6">
          <div className="max-w-2xl mx-auto space-y-6">
            <FadeIn>
              <GlassCard className="text-center py-8">
                <h2 className="text-3xl font-bold text-gradient mb-2">译见 Pro</h2>
                <Badge variant="blue">{appInfo ? `v${appInfo.version}` : "v0.1.0"}</Badge>
                <p className="text-sm text-[var(--text-secondary)] mt-3">
                  全平台 AI 翻译 & 创作工具
                </p>
                {appInfo && (
                  <p className="text-xs text-[var(--text-muted)] mt-2">
                    {appInfo.platform} / {appInfo.arch}
                  </p>
                )}
              </GlassCard>
            </FadeIn>

            <FadeIn delay={0.1}>
              <GlassCard>
                <h3 className="text-sm font-semibold text-[var(--text-primary)] mb-3">
                  <Code2 size={14} className="inline mr-1.5 align-text-bottom" />
                  {t("about.techStack")}
                </h3>
                <div className="text-sm text-[var(--text-secondary)] space-y-1.5">
                  <p>前端: React 19 + TypeScript + Tailwind CSS + Radix UI + Framer Motion</p>
                  <p>后端: Rust + Tauri 2 + reqwest (SSE streaming)</p>
                  <p>存储: SQLite (rusqlite, bundled)</p>
                  <p>主题: CSS 变量驱动的 dark/light 双主题</p>
                </div>
              </GlassCard>
            </FadeIn>

            <FadeIn delay={0.15}>
              <GlassCard>
                <h3 className="text-sm font-semibold text-[var(--text-primary)] mb-3">
                  <Plug size={14} className="inline mr-1.5 align-text-bottom" />
                  {t("about.providerSlots")}
                </h3>
                <div className="text-sm text-[var(--text-secondary)] space-y-1.5">
                  <p>1. {t("about.slotText")}</p>
                  <p>2. {t("about.slotRealtime")}</p>
                  <p>3. {t("about.slotStt")}</p>
                  <p>4. {t("about.slotTts")}</p>
                  <p>5. {t("about.slotAi")}</p>
                  <p>6. {t("about.slotImage")}</p>
                </div>
              </GlassCard>
            </FadeIn>
          </div>
        </TabsContent>

        <TabsContent value="help" className="flex-1 overflow-y-auto p-6">
          <div className="max-w-2xl mx-auto space-y-6">
            <FadeIn>
              <GlassCard>
                <h3 className="text-sm font-semibold text-[var(--text-primary)] mb-3">{t("about.faq")}</h3>
                <div className="text-sm text-[var(--text-secondary)] space-y-3">
                  <div>
                    <p className="font-medium text-[var(--text-primary)]">如何添加 AI 端点？</p>
                    <p className="text-[var(--text-muted)]">进入 设置 → 端点管理 → 添加端点，填入 URL 和 API Key。</p>
                  </div>
                  <Separator />
                  <div>
                    <p className="font-medium text-[var(--text-primary)]">支持哪些 AI 服务？</p>
                    <p className="text-[var(--text-muted)]">Azure OpenAI、OpenAI、任何兼容 OpenAI API 的服务（如 ollama、vLLM、DeepSeek）。</p>
                  </div>
                  <Separator />
                  <div>
                    <p className="font-medium text-[var(--text-primary)]">实时翻译需要什么？</p>
                    <p className="text-[var(--text-muted)]">需要配置 Azure Speech 订阅。目前仅支持 Azure Cognitive Services。</p>
                  </div>
                </div>
              </GlassCard>
            </FadeIn>
          </div>
        </TabsContent>
      </Tabs>
    </div>
  );
}
