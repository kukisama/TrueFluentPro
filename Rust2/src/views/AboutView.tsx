import { useTranslation } from "react-i18next";
import { Info, HelpCircle, Cpu, Shield, HardDrive, RefreshCw } from "lucide-react";
import {
  GlassCard, Separator, FadeIn,
  Tabs, TabsList, TabsTrigger, TabsContent,
} from "../components/ui";

export function AboutView() {
  const { t } = useTranslation();

  return (
    <div className="flex flex-col h-full">
      <Tabs defaultValue="about" className="flex flex-col h-full">
        <div className="flex items-center gap-4 px-6 py-2.5 border-b border-white/[0.06] bg-white/[0.02]">
          <TabsList>
            <TabsTrigger value="about"><Info size={14} /> {t("about.title")}</TabsTrigger>
            <TabsTrigger value="help"><HelpCircle size={14} /> {t("about.help")}</TabsTrigger>
          </TabsList>
        </div>
        <TabsContent value="about" className="flex-1 overflow-y-auto p-6">
          <AboutContent />
        </TabsContent>
        <TabsContent value="help" className="flex-1 overflow-y-auto p-6">
          <HelpContent />
        </TabsContent>
      </Tabs>
    </div>
  );
}

function AboutContent() {
  const { t } = useTranslation();
  return (
    <div className="max-w-2xl mx-auto space-y-6">
      <FadeIn>
        {/* Logo + 版本 */}
        <GlassCard className="flex flex-col items-center py-8" glow>
          <div className="w-20 h-20 rounded-2xl bg-gradient-to-br from-brand-500 to-cyan-500 flex items-center justify-center mb-4 shadow-xl shadow-brand-600/30">
            <span className="text-3xl font-bold text-white">译</span>
          </div>
          <h1 className="text-xl font-bold text-gradient">{t("app.name")}</h1>
          <p className="text-sm text-slate-400 mt-1">TrueFluentPro</p>
          <p className="text-xs text-slate-500 mt-2">{t("app.version")} (Tauri 2 + React)</p>
        </GlassCard>
      </FadeIn>

      <FadeIn delay={0.1}>
        <GlassCard>
          <h2 className="text-sm font-semibold text-slate-200 mb-3">{t("about.techStack")}</h2>
          <div className="grid grid-cols-2 gap-3 text-sm">
            {[
              { icon: <Cpu size={14} />, text: "Tauri 2 + Rust" },
              { icon: <Shield size={14} />, text: "React 19 + TypeScript" },
              { icon: <HardDrive size={14} />, text: "SQLite + Zustand" },
              { icon: <RefreshCw size={14} />, text: "插槽式 Provider 架构" },
            ].map((item, i) => (
              <div key={i} className="flex items-center gap-2 text-slate-400">
                <span className="text-brand-400">{item.icon}</span>
                {item.text}
              </div>
            ))}
          </div>
        </GlassCard>
      </FadeIn>

      <FadeIn delay={0.2}>
        <GlassCard>
          <h2 className="text-sm font-semibold text-slate-200 mb-3">{t("about.providerSlots")}</h2>
          <div className="space-y-2 text-sm">
            {[
              { color: "bg-emerald-400", text: t("about.slotText") },
              { color: "bg-amber-400", text: t("about.slotRealtime") },
              { color: "bg-emerald-400", text: t("about.slotStt") },
              { color: "bg-emerald-400", text: t("about.slotTts") },
              { color: "bg-emerald-400", text: t("about.slotAi") },
              { color: "bg-emerald-400", text: t("about.slotImage") },
            ].map((slot, i) => (
              <div key={i} className="flex items-center gap-2 text-slate-400">
                <span className={`w-2 h-2 rounded-full ${slot.color}`} />
                {slot.text}
              </div>
            ))}
          </div>
        </GlassCard>
      </FadeIn>
    </div>
  );
}

function HelpContent() {
  const { t } = useTranslation();
  return (
    <div className="max-w-2xl mx-auto space-y-6">
      <FadeIn>
        <GlassCard>
          <h2 className="text-sm font-semibold text-slate-200 mb-3">{t("about.faq")}</h2>
          <div className="space-y-4 text-sm text-slate-400">
            <div>
              <p className="text-slate-200 font-medium">如何开始使用？</p>
              <p className="mt-1">在设置 → 端点管理中添加你的 AI 服务端点（Azure OpenAI、OpenAI 等），添加后即可使用各项功能。</p>
            </div>
            <Separator />
            <div>
              <p className="text-slate-200 font-medium">支持哪些 AI 服务？</p>
              <p className="mt-1">Azure OpenAI、OpenAI、DeepL、Google Translate、腾讯云、阿里云，以及任何 OpenAI 兼容接口。</p>
            </div>
            <Separator />
            <div>
              <p className="text-slate-200 font-medium">实时翻译需要什么？</p>
              <p className="mt-1">实时翻译依赖 Speech SDK，当前版本暂未集成，计划在后续版本中加入。</p>
            </div>
          </div>
        </GlassCard>
      </FadeIn>
    </div>
  );
}
