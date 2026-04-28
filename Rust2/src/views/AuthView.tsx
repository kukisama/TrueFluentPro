import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Shield, Key, LogIn, LogOut, CheckCircle, Zap } from "lucide-react";
import {
  Button, GlassCard, Input, Label, FadeIn,
  Tabs, TabsList, TabsTrigger, TabsContent, Separator,
} from "../components/ui";

export function AuthView() {
  const { t } = useTranslation();
  const [loggedIn, setLoggedIn] = useState(false);

  return (
    <div className="flex flex-col h-full overflow-y-auto">
      <div className="flex items-center gap-3 px-6 py-3 border-b border-[var(--border-subtle)]"
        style={{ backgroundColor: "var(--toolbar-bg)" }}>
        <h1 className="text-base font-semibold text-[var(--text-primary)]">{t("auth.title")}</h1>
      </div>

      <div className="flex-1 flex items-center justify-center p-6">
        <div className="w-full max-w-md">
          {loggedIn ? (
            <FadeIn>
              <GlassCard className="text-center py-8">
                <CheckCircle size={40} className="text-emerald-400 mx-auto mb-3" />
                <h2 className="text-lg font-semibold text-[var(--text-primary)] mb-2">{t("auth.loggedIn")}</h2>
                <div className="text-sm text-[var(--text-secondary)] space-y-1 mb-4">
                  <p>{t("auth.tenant")}: contoso.onmicrosoft.com</p>
                  <p>{t("auth.mode")}: Azure AD</p>
                </div>
                <Button variant="secondary" onClick={() => setLoggedIn(false)}>
                  <LogOut size={14} /> {t("auth.logout")}
                </Button>
              </GlassCard>
            </FadeIn>
          ) : (
            <FadeIn>
              <GlassCard className="py-6">
                <div className="text-center mb-6">
                  <h2 className="text-xl font-bold text-gradient mb-1">TrueFluentPro</h2>
                  <p className="text-sm text-[var(--text-muted)]">{t("auth.subtitle")}</p>
                </div>

                <Tabs defaultValue="aad">
                  <TabsList className="w-full justify-center mb-4">
                    <TabsTrigger value="aad"><Shield size={14} /> {t("auth.azureAd")}</TabsTrigger>
                    <TabsTrigger value="apikey"><Key size={14} /> {t("auth.apiKeyMode")}</TabsTrigger>
                  </TabsList>

                  <TabsContent value="aad" className="space-y-4">
                    <p className="text-sm text-[var(--text-muted)]">{t("auth.azureAdDesc")}</p>
                    <Button className="w-full" onClick={() => setLoggedIn(true)}>
                      <LogIn size={14} /> {t("auth.loginWithAad")}
                    </Button>
                  </TabsContent>

                  <TabsContent value="apikey" className="space-y-4">
                    <div>
                      <Label>{t("auth.gatewayUrl")}</Label>
                      <Input placeholder="https://gateway.truefluent.pro" />
                    </div>
                    <div>
                      <Label>{t("auth.apiKey")}</Label>
                      <Input type="password" placeholder="tfp-..." />
                    </div>
                    <Button className="w-full">
                      <Zap size={14} /> {t("auth.connect")}
                    </Button>
                  </TabsContent>
                </Tabs>

                <Separator />
                <p className="text-xs text-[var(--text-muted)] text-center">{t("auth.localModeNote")}</p>
              </GlassCard>
            </FadeIn>
          )}
        </div>
      </div>
    </div>
  );
}
