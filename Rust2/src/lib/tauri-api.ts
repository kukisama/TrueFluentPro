import { invoke } from "@tauri-apps/api/core";
import { listen, type UnlistenFn } from "@tauri-apps/api/event";

export type { UnlistenFn };

// ── 模型引用（对齐 C# ModelReference）──

export interface ModelReference {
  endpoint_id: string;
  model_id: string;
}

// ── 配置 ──

export interface AiEndpoint {
  id: string;
  name: string;
  endpoint_type: string;
  url: string;
  api_key: string;
  api_version?: string;
  region?: string;
  models: AiModelEntry[];
  enabled: boolean;
  auth_header_mode: string;
  // Azure Speech 专属字段（对齐 C# AiEndpoint）
  speech_subscription_key: string;
  speech_region: string;
  speech_endpoint: string;
}

export interface AiModelEntry {
  model_id: string;
  display_name: string;
  deployment_name?: string;
  capabilities: ModelCapability[];
}

export type ModelCapability = "text" | "image" | "video" | "speech_to_text" | "text_to_speech";

export interface VendorProfile {
  endpoint_type: string;
  label: string;
  badge: string;
  subtitle: string;
  glyph: string;
  default_auth_header: string;
  default_api_version: string;
  supports_model_discovery: boolean;
  model_discovery_urls: string[];
  test_url_templates: Record<string, string>;
}

export interface EndpointTestReport {
  endpoint_id: string;
  endpoint_name: string;
  items: EndpointTestItem[];
  duration_ms: number;
}

export interface EndpointTestItem {
  model_id: string;
  capability: string;
  status: "success" | "failed" | "skipped";
  summary: string;
  detail?: string;
  request_url?: string;
  duration_ms: number;
}

export interface DiscoveredModel {
  id: string;
  display_name?: string;
  owned_by?: string;
}

export interface AudioConfig {
  input_device_id?: string;
  loopback_device_id?: string;
  sample_rate: number;
  enable_aec: boolean;
  enable_ns: boolean;
  enable_agc: boolean;
}

export interface UiConfig {
  theme: string;
  sidebar_collapsed: boolean;
  font_size: number;
  language: string;
}

export interface AppConfig {
  endpoints: AiEndpoint[];
  default_source_lang: string;
  default_target_langs: string[];
  audio: AudioConfig;
  ui: UiConfig;
  ai: AiSettings;
  media: MediaSettings;
  storage: StorageSettings;
  recognition: RecognitionSettings;
  web_search: WebSearchSettings;
}

export interface AiSettings {
  insight_model: ModelReference;
  summary_model: ModelReference;
  quick_model: ModelReference;
  review_model: ModelReference;
  conversation_model: ModelReference;
  intent_model: ModelReference;
  insight_system_prompt: string;
  enable_reasoning: boolean;
  max_conversation_turns: number;
}

export interface MediaSettings {
  image_model: ModelReference;
  video_model: ModelReference;
  image_quality: string;
  image_format: string;
  image_size: string;
  image_count: number;
  image_background: string;
  video_aspect_ratio: string;
  video_resolution: string;
  video_seconds: number;
  video_variants: number;
  video_poll_interval_ms: number;
}

export interface StorageSettings {
  batch_storage_connection_string: string;
  batch_storage_is_valid: boolean;
  batch_audio_container_name: string;
  batch_result_container_name: string;
  enable_recording: boolean;
  recording_mp3_bitrate_kbps: number;
  export_vtt_subtitles: boolean;
  export_srt_subtitles: boolean;
}

export interface RecognitionSettings {
  filter_modal_particles: boolean;
  max_history_items: number;
  realtime_max_length: number;
  enable_auto_timeout: boolean;
  timeout_seconds: number;
  initial_silence_timeout_seconds: number;
  end_silence_timeout_seconds: number;
}

export interface WebSearchSettings {
  provider_id: string;
  trigger_mode: string;
  max_results: number;
  enable_intent_analysis: boolean;
  enable_result_compression: boolean;
  mcp_endpoint: string;
  mcp_tool_name: string;
  mcp_api_key: string;
  debug_mode: boolean;
}

// ── 翻译 ──

export interface TranslateRequest {
  text: string;
  source_lang: string;
  target_lang: string;
  endpoint_id?: string;
}

export interface TranslateResponse {
  translated_text: string;
  source_lang: string;
  target_lang: string;
  confidence?: number;
  provider: string;
}

export interface RealtimeSessionConfig {
  source_lang: string;
  target_langs: string[];
  endpoint_id: string;
  enable_partial: boolean;
  profanity_filter: boolean;
}

export interface RealtimeEvent {
  type: "SessionStarted" | "Recognizing" | "Recognized" | "Translated" | "SessionStopped" | "Error";
  data: Record<string, unknown>;
}

// ── AI 媒体 ──

export interface ImageGenRequest {
  prompt: string;
  negative_prompt?: string;
  width: number;
  height: number;
  model: string;
  quality?: string;
  style?: string;
  endpoint_id: string;
}

export interface ImageGenResult {
  url?: string;
  base64?: string;
  revised_prompt?: string;
}

export interface CompletionRequest {
  messages: { role: string; content: string }[];
  model: string;
  temperature?: number;
  max_tokens?: number;
  endpoint_id: string;
}

export interface CompletionResponse {
  content: string;
  model: string;
  usage?: { prompt_tokens: number; completion_tokens: number; total_tokens: number };
}

// ── AI 流式补全 ──

export interface StreamTokenEvent {
  stream_id: string;
  token?: string;
  done?: boolean;
  error?: string;
}

// ── Provider ──

export interface ProviderInfo {
  id: string;
  name: string;
  capabilities: string[];
}

// ── 存储 ──

export interface TranslationHistory {
  id: string;
  source_text: string;
  translated_text: string;
  source_lang: string;
  target_lang: string;
  provider: string;
  created_at: string;
}

export interface BatchTask {
  id: string;
  name: string;
  status: string;
  task_type: string;
  progress: number;
  created_at: string;
  updated_at: string;
  error?: string;
}

// ── 系统 ──

export interface AppInfo {
  version: string;
  platform: string;
  arch: string;
  data_dir: string;
}

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  类型安全的 invoke 封装
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

export const api = {
  // 配置
  getConfig: () => invoke<AppConfig>("get_config"),
  updateConfig: (config: AppConfig) => invoke<void>("update_config", { config }),
  addEndpoint: (endpoint: AiEndpoint) => invoke<void>("add_endpoint", { endpoint }),
  removeEndpoint: (endpointId: string) => invoke<void>("remove_endpoint", { endpointId }),
  updateEndpoint: (endpoint: AiEndpoint) => invoke<void>("update_endpoint", { endpoint }),

  // 翻译
  translateText: (request: TranslateRequest) => invoke<TranslateResponse>("translate_text", { request }),
  startRealtimeTranslation: (config: RealtimeSessionConfig) =>
    invoke<string>("start_realtime_translation", { config }),
  stopRealtimeTranslation: (sessionId: string) =>
    invoke<void>("stop_realtime_translation", { sessionId }),
  onRealtimeEvent: (cb: (e: RealtimeEvent) => void): Promise<UnlistenFn> =>
    listen<RealtimeEvent>("realtime-event", (event) => cb(event.payload)),

  // Provider
  listProviders: () => invoke<ProviderInfo[]>("list_providers"),
  refreshProviders: () => invoke<ProviderInfo[]>("refresh_providers"),

  // AI 媒体
  generateImage: (request: ImageGenRequest) => invoke<ImageGenResult[]>("generate_image", { request }),
  aiComplete: (request: CompletionRequest) => invoke<CompletionResponse>("ai_complete", { request }),

  // AI 流式补全
  aiCompleteStream: (request: CompletionRequest) =>
    invoke<string>("ai_complete_stream", { request }),
  onStreamToken: (cb: (e: StreamTokenEvent) => void): Promise<UnlistenFn> =>
    listen<StreamTokenEvent>("ai-stream-token", (event) => cb(event.payload)),

  // 端点测试
  testEndpoint: (endpointId: string) => invoke<EndpointTestReport>("test_endpoint", { endpointId }),
  getVendorProfiles: () => invoke<VendorProfile[]>("get_vendor_profiles"),
  discoverModels: (endpointId: string) => invoke<DiscoveredModel[]>("discover_models", { endpointId }),

  // 存储
  getTranslationHistory: (limit?: number) =>
    invoke<TranslationHistory[]>("get_translation_history", { limit }),
  getBatchTasks: (limit?: number) => invoke<BatchTask[]>("get_batch_tasks", { limit }),
  validateStorageConnection: (connectionString: string) =>
    invoke<void>("validate_storage_connection", { connectionString }),

  // 系统
  getAppInfo: () => invoke<AppInfo>("get_app_info"),
};
