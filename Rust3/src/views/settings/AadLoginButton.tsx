import { useState, useEffect } from "react";
import { useTranslation } from "react-i18next";
import { Shield, Loader2, CheckCircle2, XCircle } from "lucide-react";
import { Button } from "../../components/ui";
import { api } from "../../lib/api";
import type { AadAuthResult } from "../../lib/types";

type AadStatus = "idle" | "waiting" | "selecting-tenant" | "success" | "error";

export default function AadLoginButton({
  endpointId,
  tenantId,
  clientId,
}: {
  endpointId: string;
  tenantId: string;
  clientId: string;
}) {
  const { t } = useTranslation();
  const [status, setStatus] = useState<AadStatus>("idle");
  const [errorMsg, setErrorMsg] = useState("");
  const [warningMsg, setWarningMsg] = useState("");
  const [tenants, setTenants] = useState<
    Array<{ tenant_id: string; display_name: string; default_domain: string }>
  >([]);
  const [tenantCtx, setTenantCtx] = useState<{ client_id: string; scope: string }>({
    client_id: "",
    scope: "",
  });

  useEffect(() => {
    const unlistenAuth = api.onAadAuthResult((result: AadAuthResult) => {
      if (result.endpoint_id !== endpointId) return;
      if (result.reauth) {
        setStatus("waiting");
        return;
      }
      if (result.success) {
        setStatus("success");
        if (result.warning) setWarningMsg(result.warning);
      } else {
        setStatus("error");
        setErrorMsg(result.error || t("endpointForm.authFailed"));
      }
    });
    const unlistenTenant = api.onAadTenantSelection((event) => {
      if (event.endpoint_id !== endpointId) return;
      setTenants(event.tenants);
      setTenantCtx({ client_id: event.client_id, scope: event.scope });
      setStatus("selecting-tenant");
    });
    return () => {
      unlistenAuth.then((fn) => fn());
      unlistenTenant.then((fn) => fn());
    };
  }, [endpointId, t]);

  const handleLogin = async () => {
    if (!endpointId) return;
    setStatus("waiting");
    setErrorMsg("");
    setWarningMsg("");
    try {
      await api.aadStartDeviceCodeFlow(endpointId, tenantId, clientId);
    } catch (e: unknown) {
      setStatus("error");
      setErrorMsg(String(e));
    }
  };

  const handleSelectTenant = async (tid: string) => {
    setStatus("waiting");
    try {
      await api.aadSelectTenant(endpointId, tid, tenantCtx.client_id, tenantCtx.scope);
    } catch (e: unknown) {
      setStatus("error");
      setErrorMsg(String(e));
    }
  };

  return (
    <div
      className="rounded-lg p-3 space-y-2"
      style={{ background: "var(--card-bg)", border: "1px solid var(--card-border)" }}
    >
      {status === "idle" && (
        <>
          <p className="text-xs text-[var(--text-secondary)]">
            🔐 {t("endpointForm.aadLoginDesc")}
          </p>
          <Button variant="secondary" size="sm" onClick={handleLogin}>
            <Shield size={14} /> {t("endpointForm.loginAad")}
          </Button>
        </>
      )}
      {status === "waiting" && (
        <div className="flex items-center gap-2 text-xs text-[var(--text-muted)]">
          <Loader2 size={12} className="animate-spin" /> {t("endpointForm.browserOpened")}
          <Button variant="ghost" size="sm" onClick={() => setStatus("idle")} className="ml-2 text-xs">
            {t("endpointForm.cancel")}
          </Button>
        </div>
      )}
      {status === "selecting-tenant" && (
        <div className="space-y-2">
          <p className="text-xs text-[var(--text-secondary)]">
            🏢 {t("endpointForm.multiTenantHint")}
          </p>
          <div
            className="space-y-0.5 max-h-40 overflow-y-auto rounded p-1"
            style={{ border: "1px solid var(--border-subtle)" }}
          >
            {tenants.map((tn) => (
              <button
                key={tn.tenant_id}
                className="w-full text-left px-3 py-2 rounded text-xs transition-colors flex flex-col gap-0.5"
                style={{ border: "1px solid transparent" }}
                onMouseEnter={(e) => {
                  e.currentTarget.style.background = "var(--hover-bg)";
                  e.currentTarget.style.borderColor = "var(--border-medium)";
                }}
                onMouseLeave={(e) => {
                  e.currentTarget.style.background = "";
                  e.currentTarget.style.borderColor = "transparent";
                }}
                onClick={() => handleSelectTenant(tn.tenant_id)}
              >
                <div className="flex items-center gap-2">
                  <span className="font-medium" style={{ color: "var(--text-primary)" }}>
                    {tn.display_name || tn.default_domain || t("endpointForm.unnamedTenant")}
                  </span>
                  {tn.default_domain && tn.display_name && (
                    <span className="text-[10px]" style={{ color: "var(--text-muted)" }}>
                      ({tn.default_domain})
                    </span>
                  )}
                </div>
                <span className="text-[10px] font-mono" style={{ color: "var(--text-muted)" }}>
                  {tn.tenant_id}
                </span>
              </button>
            ))}
          </div>
          <Button variant="ghost" size="sm" onClick={() => setStatus("idle")} className="text-xs">
            {t("endpointForm.cancel")}
          </Button>
        </div>
      )}
      {status === "success" && (
        <div className="space-y-1">
          <div className="flex items-center gap-1 text-xs" style={{ color: "var(--active-text)" }}>
            <CheckCircle2 size={14} /> {t("endpointForm.aadSuccess")}
            <Button variant="ghost" size="sm" onClick={() => setStatus("idle")} className="ml-2 text-xs">
              {t("endpointForm.reLogin")}
            </Button>
          </div>
          {warningMsg && (
            <p className="text-xs" style={{ color: "var(--text-secondary)" }}>
              {warningMsg}
            </p>
          )}
        </div>
      )}
      {status === "error" && (
        <div className="space-y-1">
          <p className="text-xs flex items-center gap-1" style={{ color: "#ef4444" }}>
            <XCircle size={14} /> {errorMsg}
          </p>
          <Button variant="secondary" size="sm" onClick={handleLogin}>
            {t("endpointForm.retry")}
          </Button>
        </div>
      )}
    </div>
  );
}
