import { useState } from "react";
import { useTranslation } from "react-i18next";
import { LogIn, Shield, User, Key } from "lucide-react";
import {
  Button, GlassCard, Input, Label, Badge,
  Tabs, TabsList, TabsTrigger, TabsContent,
  Separator, FadeIn,
} from "../components/ui";

export function AuthView() {
  const { t } = useTranslation();
  const [isLoggedIn, setIsLoggedIn] = useState(false);

  if (isLoggedIn) {
    return (
      <div className="flex items-center justify-center h-full">
        <FadeIn>
          <GlassCard className="max-w-sm w-full text-center" glow>
            <div className="w-16 h-16 rounded-full bg-brand-600/20 flex items-center justify-center mx-auto mb-4">
              <User size={28} className="text-brand-400" />
            </div>
            <h2 className="text-lg font-semibold text-slate-100">{t("auth.loggedIn")}</h2>
            <p className="text-sm text-slate-400 mt-1">user@example.com</p>
            <p className="text-xs text-slate-500 mt-0.5">Azure AD 认证</p>

            <Separator />

            <div className="text-left space-y-2.5 text-sm">
              <div className="flex justify-between">
                <span className="text-slate-500">{t("auth.tenant")}</span>
                <span className="text-slate-300">contoso.onmicrosoft.com</span>
              </div>
              <div className="flex justify-between">
                <span className="text-slate-500">{t("auth.subscription")}</span>
                <span className="text-slate-300">Production</span>
              </div>
              <div className="flex justify-between">
                <span className="text-slate-500">{t("auth.mode")}</span>
                <Badge variant="blue">Cloud</Badge>
              </div>
            </div>

            <Button variant="secondary" className="w-full mt-6" onClick={() => setIsLoggedIn(false)}>
              {t("auth.logout")}
            </Button>
          </GlassCard>
        </FadeIn>
      </div>
    );
  }

  return (
    <div className="flex items-center justify-center h-full">
      <FadeIn>
        <GlassCard className="max-w-md w-full" glow>
          <div className="text-center mb-6">
            <div className="w-16 h-16 rounded-2xl bg-gradient-to-br from-brand-500 to-cyan-500 flex items-center justify-center mx-auto mb-4 shadow-xl shadow-brand-600/30">
              <span className="text-2xl font-bold text-white">译</span>
            </div>
            <h2 className="text-xl font-bold text-slate-100">{t("auth.title")}</h2>
            <p className="text-sm text-slate-400 mt-1">{t("auth.subtitle")}</p>
          </div>

          <Tabs defaultValue="aad">
            <TabsList className="w-full mb-6">
              <TabsTrigger value="aad" className="flex-1">
                <Shield size={14} /> {t("auth.azureAd")}
              </TabsTrigger>
              <TabsTrigger value="key" className="flex-1">
                <Key size={14} /> {t("auth.apiKeyMode")}
              </TabsTrigger>
            </TabsList>

            <TabsContent value="aad">
              <div className="space-y-4">
                <p className="text-sm text-slate-400">{t("auth.azureAdDesc")}</p>
                <Button className="w-full" onClick={() => setIsLoggedIn(true)}>
                  <LogIn size={16} /> {t("auth.loginWithAad")}
                </Button>
              </div>
            </TabsContent>

            <TabsContent value="key">
              <div className="space-y-4">
                <div>
                  <Label>{t("auth.gatewayUrl")}</Label>
                  <Input placeholder="https://your-gateway.example.com" />
                </div>
                <div>
                  <Label>{t("auth.apiKey")}</Label>
                  <Input type="password" placeholder="your-api-key" />
                </div>
                <Button className="w-full" onClick={() => setIsLoggedIn(true)}>
                  <Key size={16} /> {t("auth.connect")}
                </Button>
              </div>
            </TabsContent>
          </Tabs>

          <Separator />

          <p className="text-xs text-slate-500 text-center">{t("auth.localModeNote")}</p>
        </GlassCard>
      </FadeIn>
    </div>
  );
}
