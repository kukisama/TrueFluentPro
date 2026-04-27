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
  /** 认证方式: "api_key" | "aad"（对齐 C# AiEndpoint.AuthMode） */
  auth_mode: string;
  /** AAD 租户 ID */
  azure_tenant_id: string;
  /** AAD 客户端 ID */
  azure_client_id: string;
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
  /** 是否支持 AAD 认证（对齐 C# defaults.supportsAad） */
  supports_aad: boolean;
  supports_model_discovery: boolean;
  model_discovery_urls: string[];
  test_url_templates: Record<string, string>;
  text_url_candidates: string[];
  image_url_candidates: string[];
  video_url_candidates: string[];
  audio_url_candidates: string[];
  speech_url_candidates: string[];
  text_protocol: string;
  /** 支持的认证模式 ["ApiKey"] 或 ["ApiKey","AAD"] */
  supported_auth_modes: string[];
  /** 完整的原始 JSON 资料包（可选） */
  raw_json?: Record<string, unknown>;
}

export interface EndpointTestReport {
  endpoint_id: string;
  endpoint_name: string;
  endpoint_type_name: string;
  items: EndpointTestItem[];
  duration_ms: number;
  total_count: number;
  success_count: number;
  failed_count: number;
  skipped_count: number;
}

export interface EndpointTestItem {
  model_id: string;
  capability: string;
  status: "pending" | "running" | "success" | "failed" | "skipped";
  summary: string;
  detail?: string;
  request_url?: string;
  request_summary?: string;
  duration_ms: number;
  test_branch?: string;
  urls_tried: string[];
}

export interface EndpointTestProgress {
  endpoint_id: string;
  endpoint_name: string;
  total_count: number;
  pending_count: number;
  running_count: number;
  success_count: number;
  failed_count: number;
  skipped_count: number;
  items: EndpointTestItem[];
  is_completed: boolean;
  started_at: string;
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
  width: number;
  height: number;
  model: string;
  quality?: string;
  output_format?: string;
  background?: string;
  n?: number;
  endpoint_id: string;
}

export interface ImageGenResult {
  url?: string;
  base64?: string;
  revised_prompt?: string;
}

export interface SaveImageRequest {
  base64: string;
  prompt: string;
  revised_prompt?: string;
  format: string;
  width?: number;
  height?: number;
  model_id?: string;
  endpoint_id?: string;
  generate_seconds?: number;
  source: string;
}

export interface SavedImage {
  id: string;
  prompt: string;
  revised_prompt?: string;
  file_path: string;
  file_size: number;
  width?: number;
  height?: number;
  model_id?: string;
  endpoint_id?: string;
  generate_seconds?: number;
  source: string;
  created_at: string;
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

// ── 系统 ──

export interface AppInfo {
  version: string;
  platform: string;
  arch: string;
  data_dir: string;
}

// ── 会话 & 消息（对齐 C# ChatSessionViewModel）──

export interface Session {
  id: string;
  title: string;
  session_type: "chat" | "image" | "mixed";
  created_at: string;
  updated_at: string;
  message_count: number;
  token_total: number;
}

export interface ChatMessage {
  id: string;
  session_id: string;
  role: "user" | "assistant" | "system";
  content: string;
  mode?: "text" | "image" | "search";
  reasoning_text?: string;
  prompt_tokens?: number;
  completion_tokens?: number;
  image_base64?: string;
  attachments?: string;
  created_at: string;
}

// ── 音频生命周期（对齐 C# AudioLifecycleStage + AudioProcessingSnapshot）──

export type LifecycleStage =
  | "Transcribed"
  | "Summarized"
  | "MindMap"
  | "Insight"
  | "Research"
  | "PodcastScript"
  | "PodcastAudio"
  | "Translated";

export type StageStatus = "Pending" | "Running" | "Completed" | "Failed" | "Stale";

export interface AudioLibraryItem {
  id: string;
  file_name: string;
  file_path: string;
  duration_ms: number;
  sample_rate: number;
  channels: number;
  source_lang: string;
  created_at: string;
  updated_at: string;
}

export interface AudioLifecycle {
  id: string;
  audio_item_id: string;
  stage: LifecycleStage;
  status: StageStatus;
  result_text?: string;
  result_json?: string;
  model_id?: string;
  token_used?: number;
  error?: string;
  started_at?: string;
  completed_at?: string;
}

// ── 任务引擎（对齐 C# AudioTaskRecord + TaskMonitorViewModel）──

export type TaskStatus = "Queued" | "Executing" | "Completed" | "Failed" | "Cancelled";
export type TaskType = "Transcription" | "AiCompletion" | "TTS" | "ImageGeneration" | "VideoGeneration" | "BatchTranslation";

export interface AudioTask {
  id: string;
  audio_item_id: string;
  stage: LifecycleStage;
  task_type: TaskType;
  status: TaskStatus;
  priority: number;
  retry_count: number;
  max_retries: number;
  progress: number;
  prompt_text?: string;
  result_text?: string;
  error?: string;
  submitted_at: string;
  started_at?: string;
  completed_at?: string;
}

export interface TaskExecution {
  id: string;
  task_id: string;
  attempt: number;
  status: TaskStatus;
  error?: string;
  prompt_tokens?: number;
  completion_tokens?: number;
  duration_ms?: number;
  started_at: string;
  completed_at?: string;
}

export interface TaskEngineStats {
  queued: number;
  executing: number;
  completed: number;
  failed: number;
  cancelled: number;
  total_tokens: number;
}

export interface TaskEvent {
  type: "TaskSubmitted" | "TaskStarted" | "TaskProgress" | "TaskCompleted" | "TaskFailed" | "TaskCancelled";
  task_id: string;
  audio_item_id?: string;
  stage?: LifecycleStage;
  progress?: number;
  error?: string;
}

// ── 计费记录 ──

export interface BillingRecord {
  id: string;
  task_id?: string;
  endpoint_id: string;
  model_id: string;
  prompt_tokens: number;
  completion_tokens: number;
  cost_usd?: number;
  created_at: string;
}

// ── 计费汇总 ──

export interface BillingSummary {
  total_prompt_tokens: number;
  total_completion_tokens: number;
  total_cost_usd: number;
  record_count: number;
  by_model: BillingByModel[];
}

export interface BillingByModel {
  model_id: string;
  prompt_tokens: number;
  completion_tokens: number;
  cost_usd: number;
  count: number;
}

// ── 图片管道 ──

export interface ImagePipelineRequest {
  prompt: string;
  negative_prompt?: string;
  model: string;
  width: number;
  height: number;
  quality?: string;
  style?: string;
  endpoint_id: string;
  optimize_prompt: boolean;
  upscale: boolean;
}

export interface ImagePipelineResult {
  original_prompt: string;
  optimized_prompt?: string;
  image_base64?: string;
  image_url?: string;
  revised_prompt?: string;
  steps_completed: string[];
}

export interface ModelCapabilityEntry {
  model_id: string;
  display_name: string;
  provider: string;
  capabilities: string[];
  supported_sizes: string[];
  supported_qualities: string[];
  supported_styles: string[];
  max_prompt_length: number;
  supports_negative_prompt: boolean;
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
  saveImage: (request: SaveImageRequest) => invoke<SavedImage>("save_image", { request }),
  listSavedImages: (limit?: number) => invoke<SavedImage[]>("list_saved_images", { limit }),
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
  validateStorageConnection: (connectionString: string) =>
    invoke<void>("validate_storage_connection", { connectionString }),

  // 会话 & 消息
  listSessions: (sessionType?: string) => invoke<Session[]>("list_sessions", { sessionType }),
  createSession: (title: string, sessionType: string) =>
    invoke<Session>("create_session", { title, sessionType }),
  deleteSession: (sessionId: string) => invoke<void>("delete_session", { sessionId }),
  getSessionMessages: (sessionId: string) => invoke<ChatMessage[]>("get_session_messages", { sessionId }),
  addMessage: (msg: Omit<ChatMessage, "id" | "created_at">) =>
    invoke<ChatMessage>("add_message", { msg }),

  // 音频库 & 生命周期
  listAudioItems: () => invoke<AudioLibraryItem[]>("list_audio_items"),
  addAudioItem: (item: Omit<AudioLibraryItem, "id" | "created_at" | "updated_at">) =>
    invoke<AudioLibraryItem>("add_audio_item", { item }),
  deleteAudioItem: (itemId: string) => invoke<void>("delete_audio_item", { itemId }),
  getAudioLifecycle: (audioItemId: string) => invoke<AudioLifecycle[]>("get_audio_lifecycle", { audioItemId }),
  updateLifecycleStage: (lifecycle: AudioLifecycle) =>
    invoke<void>("update_lifecycle_stage", { lifecycle }),

  // 任务引擎
  submitTask: (task: Omit<AudioTask, "id" | "submitted_at">) =>
    invoke<AudioTask>("submit_task", { task }),
  cancelTask: (taskId: string) => invoke<void>("cancel_task", { taskId }),
  retryTask: (taskId: string) => invoke<void>("retry_task", { taskId }),
  getTaskEngineStats: () => invoke<TaskEngineStats>("get_task_engine_stats"),
  listTasks: (status?: TaskStatus, limit?: number) =>
    invoke<AudioTask[]>("list_tasks", { status, limit }),
  getTaskExecutions: (taskId: string) => invoke<TaskExecution[]>("get_task_executions", { taskId }),
  onTaskEvent: (cb: (e: TaskEvent) => void): Promise<UnlistenFn> =>
    listen<TaskEvent>("task-event", (event) => cb(event.payload)),

  // 系统
  getAppInfo: () => invoke<AppInfo>("get_app_info"),

  // 配置导入/导出
  exportConfig: () => invoke<string>("export_config"),
  importConfig: (json: string) => invoke<void>("import_config", { json }),
  writeTextFile: (path: string, content: string) => invoke<void>("write_text_file", { path, content }),
  readTextFile: (path: string) => invoke<string>("read_text_file", { path }),

  // 计费
  getBillingRecords: (limit?: number) => invoke<BillingRecord[]>("get_billing_records", { limit }),
  getBillingSummary: () => invoke<BillingSummary>("get_billing_summary"),

  // 图片管道
  runImagePipeline: (request: ImagePipelineRequest) =>
    invoke<ImagePipelineResult>("run_image_pipeline", { request }),
  getImageModelCatalog: () => invoke<ModelCapabilityEntry[]>("get_image_model_catalog"),

  // 视频（预留）
  generateVideo: (prompt: string, model: string, endpointId: string) =>
    invoke<string>("generate_video", { prompt, model, endpointId }),
};
