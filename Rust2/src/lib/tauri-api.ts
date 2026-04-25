import { invoke } from "@tauri-apps/api/core";
import { listen, type UnlistenFn } from "@tauri-apps/api/event";

// ── 配置 ──

export interface AiEndpoint {
  id: string;
  name: string;
  endpoint_type: string;
  url: string;
  api_key: string;
  region?: string;
  deployment?: string;
  enabled: boolean;
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
  testEndpoint: (endpointId: string) => invoke<string>("test_endpoint", { endpointId }),

  // 存储
  getTranslationHistory: (limit?: number) =>
    invoke<TranslationHistory[]>("get_translation_history", { limit }),
  getBatchTasks: (limit?: number) => invoke<BatchTask[]>("get_batch_tasks", { limit }),

  // 系统
  getAppInfo: () => invoke<AppInfo>("get_app_info"),
};
