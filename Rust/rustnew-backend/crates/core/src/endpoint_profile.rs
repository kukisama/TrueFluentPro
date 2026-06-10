//! 厂商资料包（Endpoint Profile）—— 移植自 C# `EndpointProfileDefinition` +
//! `EndpointProfileCatalogService` + `EndpointTemplateService.ApplyTemplate`。
//!
//! 设计照搬 C#：声明式 JSON 资料包 + 创建时套默认模板 + 运行时按候选路由解析。
//! JSON 直接从 `Assets/EndpointProfiles/Profiles/` 拷贝到 `resources/endpoint-profiles/`，
//! schema 不变。serde 默认忽略未建模字段，因此这里只镜像当前用到的部分。
//!
//! 范围（本期）：覆盖 **AI 终结点**（`AiEndpoint`）。语音资源（`SpeechResource`）
//! 仍走独立体系，后续若统一再迁移。

use serde::Deserialize;

use crate::endpoint::{
    AiEndpoint, AiProviderType, ApiKeyHeaderMode, AuthMode, EndpointKind, TextProtocol,
};

// 编译期内嵌四个内置资料包（与 C# `EndpointProfileCatalogService` 的内置集合对齐）。
const PROFILE_AZURE_OPENAI: &str =
    include_str!("../resources/endpoint-profiles/azure-openai.json");
const PROFILE_APIM_GATEWAY: &str =
    include_str!("../resources/endpoint-profiles/apim-gateway.json");
const PROFILE_OPENAI_COMPATIBLE: &str =
    include_str!("../resources/endpoint-profiles/openai-compatible.json");
const PROFILE_AZURE_SPEECH: &str =
    include_str!("../resources/endpoint-profiles/azure-speech.json");

/// 资料包里 `authMode` 的取值（JSON 用 `AAD`，与 [`AuthMode::Aad`] 拼写不同，单独建模）。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Deserialize)]
pub enum ProfileAuthMode {
    #[default]
    ApiKey,
    #[serde(rename = "AAD")]
    Aad,
}

impl ProfileAuthMode {
    fn to_auth_mode(self) -> AuthMode {
        match self {
            ProfileAuthMode::ApiKey => AuthMode::ApiKey,
            ProfileAuthMode::Aad => AuthMode::Aad,
        }
    }
}

/// 资料包 `defaults` 块：创建终结点时注入的默认行为（对齐 C# `EndpointProfileDefaults`）。
#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ProfileDefaults {
    /// JSON 取值 `OpenAiCompatible` / `AzureOpenAi`，直接映射 [`AiProviderType`]。
    pub provider_type: AiProviderType,
    pub auth_mode: ProfileAuthMode,
    /// JSON 取值 `Auto` / `ApiKeyHeader` / `Bearer`，直接映射 [`ApiKeyHeaderMode`]。
    pub api_key_header_mode: ApiKeyHeaderMode,
    /// JSON 取值 `Auto` / `ChatCompletionsV1` / `ChatCompletionsRaw` / `Responses`。
    pub text_api_protocol_mode: TextProtocol,
    pub api_version: String,
    pub supports_aad: bool,
    pub clear_azure_identity_fields: bool,
}

/// 一组候选 URL（用于运行时按声明顺序挑路由）。
#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ProfileModelDiscovery {
    pub url_candidates: Vec<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ProfileText {
    pub preferred_protocol: String,
    pub deployment_chat_completions_url_candidates: Vec<String>,
    pub responses_url_candidates: Vec<String>,
    pub chat_completions_v1_url_candidates: Vec<String>,
    pub chat_completions_raw_url_candidates: Vec<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ProfileAudio {
    pub default_api_version: String,
    pub transcription_url_candidates: Vec<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ProfileSpeech {
    pub default_api_version: String,
    pub synthesis_url_candidates: Vec<String>,
}

/// `overrides.routes.text` 块：资料包对文本路由的首选 URL 模板
/// （对齐 C# `EndpointProfileRouteText`，仅取测试所需字段）。
#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ProfileRouteText {
    /// 非 Azure（model-based）首选 URL 模板。
    pub primary_url: String,
    /// Azure（deployment-based）首选 URL 模板。
    pub deployment_primary_url: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ProfileRoutes {
    pub text: ProfileRouteText,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ProfileOverrides {
    pub routes: ProfileRoutes,
}

/// `fallbacks` 块：显式回退 URL（仅取文本所需）。
#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ProfileFallbacks {
    pub text: Vec<String>,
}

/// 厂商资料包定义（镜像 C# `EndpointProfileDefinition` 的常用字段）。
#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct EndpointProfile {
    pub schema_version: u32,
    pub id: String,
    pub vendor: String,
    pub base_policy_id: String,
    /// JSON 取值与 [`EndpointKind`] 变体名一致，直接反序列化。
    pub endpoint_type: EndpointKind,
    pub display_name: String,
    pub subtitle: String,
    pub glyph: String,
    pub summary: String,
    pub default_name_prefix: String,
    pub icon_asset_path: String,
    pub tags: Vec<String>,
    pub defaults: ProfileDefaults,
    pub model_discovery: ProfileModelDiscovery,
    pub text: ProfileText,
    pub audio: ProfileAudio,
    pub speech: ProfileSpeech,
    /// 资料包对路由的覆盖（首选 URL 模板）。
    pub overrides: ProfileOverrides,
    /// 显式回退 URL。
    pub fallbacks: ProfileFallbacks,
}

/// 内置资料包目录（对齐 C# `IEndpointProfileCatalogService`）。
#[derive(Debug, Clone)]
pub struct EndpointProfileCatalog {
    profiles: Vec<EndpointProfile>,
}

impl EndpointProfileCatalog {
    /// 加载全部内置资料包。解析失败的单个包会被跳过并打日志（不让坏包拖垮启动）。
    pub fn builtin() -> Self {
        let raw = [
            ("azure-openai", PROFILE_AZURE_OPENAI),
            ("apim-gateway", PROFILE_APIM_GATEWAY),
            ("openai-compatible", PROFILE_OPENAI_COMPATIBLE),
            ("azure-speech", PROFILE_AZURE_SPEECH),
        ];
        let mut profiles = Vec::with_capacity(raw.len());
        for (name, json) in raw {
            match serde_json::from_str::<EndpointProfile>(json) {
                Ok(p) => profiles.push(p),
                Err(e) => tracing::warn!(profile = name, error = %e, "解析厂商资料包失败，已跳过"),
            }
        }
        Self { profiles }
    }

    /// 全部资料包。
    pub fn all(&self) -> &[EndpointProfile] {
        &self.profiles
    }

    /// 按资料包 id 精确查找（对齐 C# `FindProfile`）。
    pub fn find(&self, profile_id: &str) -> Option<&EndpointProfile> {
        self.profiles.iter().find(|p| p.id == profile_id)
    }

    /// 按终结点类型取资料包（对齐 C# `GetProfile(EndpointApiType)`）。
    pub fn get(&self, kind: EndpointKind) -> Option<&EndpointProfile> {
        self.profiles.iter().find(|p| p.endpoint_type == kind)
    }
}

impl EndpointProfile {
    /// 把本资料包的默认值套到终结点上（照搬 C# `EndpointTemplateService.ApplyTemplate`）。
    ///
    /// 设置 `profile_id` / `kind` / 鉴权 / 协议 / API 版本；若声明 `clearAzureIdentityFields`
    /// 则清空 Azure 身份字段。
    pub fn apply_to(&self, endpoint: &mut AiEndpoint) {
        let d = &self.defaults;
        endpoint.profile_id = self.id.clone();
        endpoint.kind = self.endpoint_type;
        endpoint.provider_type = d.provider_type;
        endpoint.auth_mode = d.auth_mode.to_auth_mode();
        endpoint.api_key_header_mode = d.api_key_header_mode;
        endpoint.text_protocol = d.text_api_protocol_mode;
        endpoint.api_version = d.api_version.trim().to_string();
        if d.clear_azure_identity_fields {
            endpoint.azure_tenant_id.clear();
            endpoint.azure_client_id.clear();
        }
    }

    /// 按资料包声明构建文本接口的候选 URL（移植自 C#
    /// `EndpointProfileUrlBuilder.BuildResolvedTextUrlCandidates` 的核心优先级）。
    ///
    /// 优先级：override 首选（Azure 用 `deploymentPrimaryUrl`，否则 `primaryUrl`）
    /// → `fallbacks.text` → legacy 候选；占位符 `{baseUrl}`/`{deployment}`/`{apiVersion}`
    /// 已替换；按声明顺序去重，跳过空串。
    pub fn build_text_url_candidates(
        &self,
        base_url: &str,
        is_azure: bool,
        deployment: &str,
        api_version: &str,
    ) -> Vec<String> {
        let base = base_url.trim().trim_end_matches('/');
        if base.is_empty() {
            return Vec::new();
        }

        let render = |template: &str| -> String {
            template
                .replace("{baseUrl}", base)
                .replace("{deployment}", deployment.trim())
                .replace("{apiVersion}", api_version.trim())
        };

        let mut urls: Vec<String> = Vec::new();
        let push = |raw: &str, out: &mut Vec<String>| {
            if raw.trim().is_empty() {
                return;
            }
            let url = render(raw);
            if !out.iter().any(|u| u.eq_ignore_ascii_case(&url)) {
                out.push(url);
            }
        };

        // 1) override 首选
        let primary = if is_azure {
            &self.overrides.routes.text.deployment_primary_url
        } else {
            &self.overrides.routes.text.primary_url
        };
        push(primary, &mut urls);

        // 2) 显式回退
        for t in &self.fallbacks.text {
            push(t, &mut urls);
        }

        // 3) legacy 候选（按当前模式取相应数组）
        let legacy: &[String] = if is_azure {
            &self.text.deployment_chat_completions_url_candidates
        } else {
            // 非 Azure：v1 / raw / responses 依次纳入
            // （responses 放最后，连通性测试会按 URL 形态自动选择请求体）
            &self.text.chat_completions_v1_url_candidates
        };
        for t in legacy {
            push(t, &mut urls);
        }
        if !is_azure {
            for t in &self.text.chat_completions_raw_url_candidates {
                push(t, &mut urls);
            }
            for t in &self.text.responses_url_candidates {
                push(t, &mut urls);
            }
        }

        urls
    }
}

/// 便捷入口：按终结点类型套模板。找不到资料包返回 false（不修改终结点）。
pub fn apply_template(
    catalog: &EndpointProfileCatalog,
    endpoint: &mut AiEndpoint,
    kind: EndpointKind,
) -> bool {
    match catalog.get(kind) {
        Some(profile) => {
            profile.apply_to(endpoint);
            true
        }
        None => false,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn builtin_catalog_loads_all_four() {
        let catalog = EndpointProfileCatalog::builtin();
        assert_eq!(catalog.all().len(), 4, "应加载四个内置资料包");
    }

    #[test]
    fn get_by_kind_returns_matching_profile() {
        let catalog = EndpointProfileCatalog::builtin();
        let aoai = catalog.get(EndpointKind::AzureOpenAi).expect("缺 azure-openai");
        assert_eq!(aoai.id, "builtin.microsoft.azure-openai");
        let speech = catalog.get(EndpointKind::AzureSpeech).expect("缺 azure-speech");
        assert_eq!(speech.id, "builtin.microsoft.azure-speech");
    }

    #[test]
    fn apply_template_azure_openai_sets_defaults() {
        let catalog = EndpointProfileCatalog::builtin();
        let mut ep = AiEndpoint::default();
        assert!(apply_template(&catalog, &mut ep, EndpointKind::AzureOpenAi));
        assert_eq!(ep.profile_id, "builtin.microsoft.azure-openai");
        assert_eq!(ep.kind, EndpointKind::AzureOpenAi);
        assert_eq!(ep.provider_type, AiProviderType::AzureOpenAi);
        assert_eq!(ep.auth_mode, AuthMode::ApiKey);
        assert_eq!(ep.api_key_header_mode, ApiKeyHeaderMode::ApiKeyHeader);
        assert_eq!(ep.text_protocol, TextProtocol::Responses);
        assert_eq!(ep.api_version, "2024-02-01");
    }

    #[test]
    fn apply_template_openai_compatible_clears_azure_fields() {
        let catalog = EndpointProfileCatalog::builtin();
        let mut ep = AiEndpoint::default();
        ep.azure_tenant_id = "tenant".into();
        ep.azure_client_id = "client".into();
        assert!(apply_template(&catalog, &mut ep, EndpointKind::OpenAiCompatible));
        assert_eq!(ep.provider_type, AiProviderType::OpenAiCompatible);
        assert_eq!(ep.api_key_header_mode, ApiKeyHeaderMode::Bearer);
        assert_eq!(ep.text_protocol, TextProtocol::ChatCompletionsV1);
        // clearAzureIdentityFields = true
        assert!(ep.azure_tenant_id.is_empty());
        assert!(ep.azure_client_id.is_empty());
    }

    #[test]
    fn apim_gateway_profile_present() {
        let catalog = EndpointProfileCatalog::builtin();
        let apim = catalog
            .get(EndpointKind::ApiManagementGateway)
            .expect("缺 apim-gateway");
        assert!(!apim.text.responses_url_candidates.is_empty() || !apim.id.is_empty());
    }

    #[test]
    fn build_text_urls_openai_compatible_uses_override_primary() {
        let catalog = EndpointProfileCatalog::builtin();
        let p = catalog.get(EndpointKind::OpenAiCompatible).unwrap();
        let urls = p.build_text_url_candidates("https://api.example.com/", false, "gpt-4o", "");
        assert!(
            urls.first().map(|u| u.as_str()) == Some("https://api.example.com/v1/chat/completions"),
            "openai-compatible 首选应为 /v1/chat/completions，实际：{urls:?}"
        );
    }

    #[test]
    fn build_text_urls_azure_uses_deployment_primary() {
        let catalog = EndpointProfileCatalog::builtin();
        let p = catalog.get(EndpointKind::AzureOpenAi).unwrap();
        let urls = p.build_text_url_candidates("https://r.openai.azure.com", true, "my-dep", "2024-02-01");
        assert!(!urls.is_empty(), "Azure 应产出候选 URL");
        assert!(urls[0].starts_with("https://r.openai.azure.com/openai/v1/responses"));
        // fallbacks 里有 chat/completions，应被纳入并完成 {deployment}/{apiVersion} 替换
        assert!(
            urls.iter().any(|u| u.contains("/openai/deployments/my-dep/chat/completions")
                && u.contains("api-version=2024-02-01")),
            "应包含替换后的部署式 chat/completions 回退，实际：{urls:?}"
        );
    }
}
