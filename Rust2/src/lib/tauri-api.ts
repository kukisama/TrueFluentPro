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
  chunk_duration_ms: number;
  enable_auto_timeout: boolean;
  initial_silence_timeout_seconds: number;
  end_silence_timeout_seconds: number;
  enable_no_response_restart: boolean;
  no_response_restart_seconds: number;
  audio_activity_threshold: number;
  audio_level_gain: number;
  show_reconnect_marker: boolean;
}

// ── 实时翻译 ──

export interface TranslationSession {
  id: string;
  started_at: string;
  stopped_at: string | null;
  source_lang: string;
  target_langs: string;
  provider: string;
  status: string;
}

export interface TranslationSegment {
  id: string;
  session_id: string;
  sequence: number;
  original_text: string;
  translated_text: string;
  target_lang: string;
  started_at: string | null;
  ended_at: string | null;
  is_bookmarked: boolean;
  bookmark_note: string | null;
  audio_path: string | null;
  raw_event_json: string | null;
}

export interface SupportedLanguage {
  code: string;
  label: string;
  kind: "source" | "target" | "both";
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
  messages: { role: string; content: string | ContentPart[] }[];
  model: string;
  temperature?: number;
  max_tokens?: number;
  endpoint_id: string;
}

/** OpenAI 多模态 content part */
export type ContentPart =
  | { type: "text"; text: string }
  | { type: "image_url"; image_url: { url: string; detail?: string } };

export interface CompletionResponse {
  content: string;
  model: string;
  usage?: { prompt_tokens: number; completion_tokens: number; total_tokens: number };
}

// ── AI 流式补全 ──

export interface StreamTokenEvent {
  stream_id: string;
  token?: string;
  reasoning?: string;
  usage?: { prompt_tokens: number; completion_tokens: number };
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

// ── 任务监控（PR-1~PR-4 对齐 C# TaskQueueMonitorViewModel）──

export interface MonitorBucket {
  key: string;
  title: string;
  icon: string;
  count: number;
  is_danger: boolean;
}

export interface MonitorTaskItem {
  id: string;
  short_task_id: string;
  audio_item_id: string;
  audio_file_name: string;
  stage: string;
  stage_display_name: string;
  stage_color: string;
  task_type: string;
  status: string;
  status_display_name: string;
  priority: number;
  retry_count: number;
  progress: number;
  error_message?: string;
  progress_message?: string;
  submitted_at: string;
  started_at?: string;
  finished_at?: string;
  elapsed_time: string;
  params_snapshot_json?: string;
}

export interface MonitorExecutionRecord {
  id: string;
  task_id: string;
  status: string;
  status_display_name: string;
  billable: boolean;
  billable_display: string;
  model_name?: string;
  tokens_in?: number;
  tokens_out?: number;
  tokens_display: string;
  duration_ms?: number;
  duration_display: string;
  error_message?: string;
  cancel_reason?: string;
  started_at: string;
  finished_at?: string;
  time_display: string;
  has_debug_data: boolean;
  debug_prompt?: string;
  debug_response?: string;
}

export interface MonitorGlobalStats {
  total_executions: number;
  billable_executions: number;
  billable_tokens_in: number;
  billable_tokens_out: number;
}

export interface MonitorSettings {
  max_transcription_concurrency: number;
  max_ai_concurrency: number;
  transcription_timeout_minutes: number;
}

export interface MonitorSnapshot {
  buckets: MonitorBucket[];
  current_bucket: string;
  current_bucket_tasks: MonitorTaskItem[];
  global_stats: MonitorGlobalStats;
}

// PR-2.14: UI 状态持久化
export interface MonitorUiState {
  active_bucket: string;
  sort_column: string;
  sort_ascending: boolean;
  selected_task_id: string | null;
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

// 音频设备枚举
export interface AudioDeviceInfo {
  id: string;
  name: string;
  device_type: "Input" | "Output" | "Loopback";
  is_default: boolean;
}

// AAD 设备代码流
export interface DeviceCodeResponse {
  user_code: string;
  verification_uri: string;
  message: string;
  expires_in: number;
  interval: number;
}

export interface AadAuthResult {
  endpoint_id: string;
  success: boolean;
  error?: string;
  warning?: string;
  token?: {
    access_token: string;
    token_type: string;
    expires_in: number;
    scope: string;
  };
}

/// 租户选择事件（多租户时由后端发出）
export interface AadTenantSelectionEvent {
  endpoint_id: string;
  tenants: Array<{ tenant_id: string; display_name: string; default_domain: string }>;
  client_id: string;
  scope: string;
}

// ── 创作工坊（对齐 C# StorageModels.cs）──

export interface StudioSession {
  id: string;
  session_type: string;
  name: string;
  directory_path: string;
  canvas_mode: string;
  media_kind: string;
  is_deleted: boolean;
  created_at: string;
  updated_at: string;
  last_accessed_at?: string;
  source_session_id?: string;
  source_session_name?: string;
  source_session_directory_name?: string;
  source_asset_id?: string;
  source_asset_kind?: string;
  source_asset_file_name?: string;
  source_asset_path?: string;
  source_preview_path?: string;
  source_reference_role?: string;
  message_count: number;
  task_count: number;
  asset_count: number;
  latest_message_preview?: string;
  legacy_source_path?: string;
  import_batch_id?: string;
  imported_at?: string;
  is_legacy_import: boolean;
}

export interface StudioMessage {
  id: string;
  session_id: string;
  sequence_no: number;
  role: string;
  content_type: string;
  text: string;
  reasoning_text: string;
  prompt_tokens?: number;
  completion_tokens?: number;
  generate_seconds?: number;
  download_seconds?: number;
  search_summary?: string;
  timestamp: string;
  is_deleted: boolean;
}

export interface StudioMediaRef {
  id: number;
  message_id: string;
  media_path: string;
  media_kind: string;
  sort_order: number;
  preview_path?: string;
}

export interface StudioCitation {
  id: number;
  message_id: string;
  citation_number: number;
  title: string;
  url: string;
  snippet: string;
  hostname: string;
}

export interface StudioAttachment {
  id: number;
  message_id: string;
  attachment_type: string;
  file_name: string;
  file_path: string;
  file_size: number;
  sort_order: number;
}

export interface StudioTask {
  id: string;
  session_id: string;
  task_type: string;
  status: string;
  prompt: string;
  progress: number;
  result_file_path?: string;
  error_message?: string;
  has_reference_input: boolean;
  remote_video_id?: string;
  remote_video_api_mode?: string;
  remote_generation_id?: string;
  remote_download_url?: string;
  generate_seconds?: number;
  download_seconds?: number;
  created_at: string;
  updated_at: string;
}

export interface StudioReferenceImage {
  id: string;
  session_id: string;
  file_path: string;
  sort_order: number;
  width?: number;
  height?: number;
  created_at: string;
}

export interface StudioSessionBundle {
  messages: StudioMessage[];
  media_refs: Record<string, StudioMediaRef[]>;
  citations: Record<string, StudioCitation[]>;
  attachments: Record<string, StudioAttachment[]>;
}

export interface StudioTaskEvent {
  task_id: string;
  session_id: string;
  status: string;
  progress?: number;
  error?: string;
  result_paths?: string[];
  result_path?: string;
}

export interface StudioMessageDelta {
  session_id: string;
  message_id: string;
  token?: string;
  reasoning?: string;
  done?: boolean;
}

// ── 媒体中心（对齐 Rust CenterWorkspace / CanvasRound 等）──

export interface CenterWorkspace {
  id: string;
  session_type: string;
  name: string;
  is_deleted: boolean;
  created_at: string;
  updated_at: string;
  last_accessed_at?: string;
  current_round_id?: string;
  round_count: number;
  asset_count: number;
  has_running_task: boolean;
}

export interface CanvasRound {
  id: string;
  session_id: string;
  round_index: number;
  prompt: string;
  params_json: string;
  model_ref: string;
  created_at: string;
  status: string;
}

export interface CenterAssetDetail {
  id: string;
  round_id: string;
  asset_id: string;
  sequence: number;
  is_selected: boolean;
  file_path: string;
  preview_path: string;
  kind: string;
  width?: number;
  height?: number;
  duration_ms?: number;
  created_at: string;
}

export interface CenterWorkspaceBundle {
  workspace: CenterWorkspace;
  rounds: CanvasRound[];
  current_round_assets: CenterAssetDetail[];
  reference_images: StudioReferenceImage[];
  running_tasks: StudioTask[];
}

export interface VideoCapabilityEntry {
  aspect_ratio: string;
  resolution: string;
  duration_seconds: number[];
  max_count: number;
}

export interface ExportResult {
  copied: number;
  failed: number;
}

export interface CenterTaskEvent {
  task_id: string;
  session_id: string;
  round_id: string;
  status: string;
  progress?: number;
  error?: string;
  asset_ids?: string[];
  elapsed_seconds?: number;
}

// ── 听析中心 AudioLab（对齐 Rust AudioFile / AudioLabBundle 等）──

export interface AudioFile {
  id: string;
  display_name: string;
  source_path: string;
  mp3_path: string | null;
  sample_rate: number;
  channels: number;
  duration_ms: number;
  file_size_bytes: number;
  sha256: string;
  imported_at: string;
  last_opened_at: string | null;
  is_legacy_import: boolean;
  legacy_source_path: string | null;
  import_batch_id: string | null;
  /** 关联 studio_session id（JOIN 获得） */
  session_id: string | null;
}

export interface AudioTranscript {
  id: string;
  session_id: string;
  audio_file_id: string;
  language: string;
  raw_json: string | null;
  parser_kind: string;
  created_at: string;
}

export interface AudioSegment {
  id: string;
  transcript_id: string;
  sequence: number;
  speaker: string;
  speaker_index: number;
  start_ms: number;
  end_ms: number;
  text: string;
  confidence: number | null;
}

export interface AudioStageOutput {
  id: string;
  session_id: string;
  stage_key: string;
  content_markdown: string;
  status: string;
  error_message: string | null;
  model_ref: string | null;
  generated_at: string | null;
  custom_stage_key: string | null;
  custom_is_mindmap: boolean | null;
}

export interface AudioResearchTopic {
  id: string;
  session_id: string;
  title: string;
  description: string;
  status: string;
  report_markdown: string | null;
  created_at: string;
}

export interface AudioAutoTag {
  id: string;
  session_id: string;
  tag: string;
  source: string;
  created_at: string;
}

export interface AudioStagePreset {
  id: string;
  stage: string;
  display_name: string;
  system_prompt: string;
  show_in_tab: boolean;
  include_in_batch: boolean;
  is_enabled: boolean;
  display_mode: string;
  sort_order: number;
}

export interface AudioLabBundle {
  file: AudioFile;
  transcript: AudioTranscript | null;
  segments: AudioSegment[];
  auto_tags: AudioAutoTag[];
  stage_outputs: AudioStageOutput[];
  research_topics: AudioResearchTopic[];
  custom_presets: AudioStagePreset[];
}

export interface AudioPlaybackInfo {
  file_id: string;
  playback_path: string;
  duration_ms: number;
  display_name: string;
}

/** AudioLab Tab 枚举（对齐 C# AudioLabTabKind） */
export type AudioLabTabKind =
  | "Summary"
  | "Transcript"
  | "MindMap"
  | "Insight"
  | "Research"
  | "Podcast"
  | "Translation"
  | "Custom";

/** 阶段任务更新事件 */
export interface AudioLabTaskEvent {
  task_id: string;
  session_id: string;
  stage_key: string;
  status: string;
  progress?: number;
  error?: string;
}

/** 阶段流式增量事件 */
export interface AudioLabStageDelta {
  session_id: string;
  stage_key: string;
  delta: string;
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
  renameSession: (sessionId: string, newTitle: string) => invoke<void>("rename_session", { sessionId, newTitle }),
  optimizePrompt: (prompt: string, endpointId?: string) => invoke<string>("optimize_prompt", { prompt, endpointId }),
  // O-34: 获取后端支持的语言列表
  getSupportedLanguages: () => invoke<[string, string][]>("get_supported_languages"),
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
  // O-08: 更新引擎配置
  updateTaskEngineConfig: (concurrency: number, timeoutSecs: number) =>
    invoke<void>("update_task_engine_config", { concurrency, timeoutSecs }),
  // O-48: 清理过期任务
  cleanupExpiredTasks: (days: number) => invoke<number>("cleanup_expired_tasks", { days }),
  listTasks: (status?: TaskStatus, limit?: number) =>
    invoke<AudioTask[]>("list_tasks", { status, limit }),
  getTaskExecutions: (taskId: string) => invoke<TaskExecution[]>("get_task_executions", { taskId }),
  onTaskEvent: (cb: (e: TaskEvent) => void): Promise<UnlistenFn> =>
    listen<TaskEvent>("task-event", (event) => cb(event.payload)),
  // O-40: 图片管线进度事件
  onImagePipelineProgress: (cb: (e: { step: string; progress: number }) => void): Promise<UnlistenFn> =>
    listen<{ step: string; progress: number }>("image-pipeline-progress", (event) => cb(event.payload)),

  // ── 任务监控 API（PR-1~PR-4）──
  monitorGetSnapshot: (bucket?: string, sortColumn?: string, sortAscending?: boolean) =>
    invoke<MonitorSnapshot>("monitor_get_snapshot", { bucket, sortColumn, sortAscending }),
  monitorSetBucket: (bucketKey: string, sortColumn?: string, sortAscending?: boolean) =>
    invoke<MonitorTaskItem[]>("monitor_set_bucket", { bucketKey, sortColumn, sortAscending }),
  monitorListExecutions: (taskId: string) =>
    invoke<MonitorExecutionRecord[]>("monitor_list_executions", { taskId }),
  monitorGetExecutionDetail: (executionId: string) =>
    invoke<MonitorExecutionRecord>("monitor_get_execution_detail", { executionId }),
  monitorCancelTask: (taskId: string, reason?: string) =>
    invoke<void>("monitor_cancel_task", { taskId, reason }),
  monitorGetSettings: () => invoke<MonitorSettings>("monitor_get_settings"),
  monitorUpdateSettings: (maxTranscriptionConcurrency?: number, maxAiConcurrency?: number, transcriptionTimeoutMinutes?: number) =>
    invoke<void>("monitor_update_settings", { maxTranscriptionConcurrency, maxAiConcurrency, transcriptionTimeoutMinutes }),
  monitorCleanupCompleted: (olderThanDays?: number) =>
    invoke<number>("monitor_cleanup_completed", { olderThanDays }),
  monitorRefresh: () => invoke<MonitorSnapshot>("monitor_refresh"),
  monitorRetryTask: (taskId: string) => invoke<string>("monitor_retry_task", { taskId }),
  monitorBatchCancel: (taskIds: string[]) => invoke<number>("monitor_batch_cancel", { taskIds }),
  monitorBatchDelete: (taskIds: string[]) => invoke<number>("monitor_batch_delete", { taskIds }),
  monitorExportCsv: (filePath: string, statusFilter?: string, includeDebug?: boolean) =>
    invoke<string>("monitor_export_csv", { filePath, statusFilter, includeDebug }),
  monitorGetArchivedSnapshot: (dateFrom: string, dateTo: string) =>
    invoke<MonitorSnapshot>("monitor_get_archived_snapshot", { dateFrom, dateTo }),
  // PR-2.14: UI 状态持久化
  monitorSaveUiState: (uiState: MonitorUiState) =>
    invoke<void>("monitor_save_ui_state", { uiState }),
  monitorLoadUiState: () =>
    invoke<MonitorUiState | null>("monitor_load_ui_state"),
  onMonitorSnapshotUpdate: (cb: (e?: MonitorSnapshot | null) => void): Promise<UnlistenFn> =>
    listen<MonitorSnapshot | null>("monitor-snapshot-update", (event) => cb(event.payload)),

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

  // 视频生成（完整 create → poll → download，进度通过 video-progress 事件推送）
  generateVideo: (request: {
    prompt: string; model: string; endpoint_id: string;
    size?: string; duration_seconds?: number; n?: number;
    api_mode?: "sora_jobs" | "videos"; reference_image_path?: string;
  }) => invoke<string>("generate_video", { request }),
  onVideoProgress: (cb: (e: {
    task_id: string; status: string; message?: string;
    error?: string; file_path?: string; elapsed_seconds?: number;
    video_status?: string; video_id?: string;
  }) => void): Promise<UnlistenFn> =>
    listen<any>("video-progress", (event) => cb(event.payload)),

  // 音频设备枚举
  listAudioDevices: () => invoke<AudioDeviceInfo[]>("list_audio_devices"),

  // AAD 认证
  aadStartDeviceCodeFlow: (endpointId: string, tenantId: string, clientId: string, scope?: string) =>
    invoke<DeviceCodeResponse>("aad_start_device_code_flow", { endpointId, tenantId, clientId, scope }),
  aadSelectTenant: (endpointId: string, tenantId: string, clientId: string, scope?: string) =>
    invoke<void>("aad_select_tenant", { endpointId, tenantId, clientId, scope }),
  aadRefreshToken: (endpointId: string) =>
    invoke<void>("aad_refresh_token", { endpointId }),
  onAadAuthResult: (cb: (e: AadAuthResult) => void): Promise<UnlistenFn> =>
    listen<AadAuthResult>("aad-auth-result", (event) => cb(event.payload)),
  onAadTenantSelection: (cb: (e: AadTenantSelectionEvent) => void): Promise<UnlistenFn> =>
    listen<AadTenantSelectionEvent>("aad-tenant-selection", (event) => cb(event.payload)),

  // 创作工坊
  studioListSessions: (limit?: number, offset?: number) =>
    invoke<StudioSession[]>("studio_list_sessions", { limit, offset }),
  studioGetSession: (sessionId: string) =>
    invoke<StudioSession | null>("studio_get_session", { sessionId }),
  studioCreateSession: (sessionType: string, name: string) =>
    invoke<StudioSession>("studio_create_session", { sessionType, name }),
  studioRenameSession: (sessionId: string, newName: string) =>
    invoke<void>("studio_rename_session", { sessionId, newName }),
  studioSoftDeleteSession: (sessionId: string) =>
    invoke<void>("studio_soft_delete_session", { sessionId }),
  studioGetSessionBundle: (sessionId: string) =>
    invoke<StudioSessionBundle>("studio_get_session_bundle", { sessionId }),
  studioAppendMessage: (args: {
    sessionId: string; role: string; text: string;
    contentType?: string; reasoningText?: string;
    promptTokens?: number; completionTokens?: number;
    generateSeconds?: number; downloadSeconds?: number;
    searchSummary?: string;
  }) => invoke<StudioMessage>("studio_append_message", {
    sessionId: args.sessionId, role: args.role, text: args.text,
    contentType: args.contentType, reasoningText: args.reasoningText,
    promptTokens: args.promptTokens, completionTokens: args.completionTokens,
    generateSeconds: args.generateSeconds, downloadSeconds: args.downloadSeconds,
    searchSummary: args.searchSummary,
  }),
  studioGetMessagesBefore: (sessionId: string, beforeSequence: number, limit?: number) =>
    invoke<StudioMessage[]>("studio_get_messages_before", { sessionId, beforeSequence, limit }),
  studioListRunningTasks: (sessionId: string) =>
    invoke<StudioTask[]>("studio_list_running_tasks", { sessionId }),
  studioChatStream: (sessionId: string, text: string, endpointId: string, model: string) =>
    invoke<string>("studio_chat_stream", { sessionId, text, endpointId, model }),
  studioStartImageTask: (args: {
    sessionId: string; prompt: string; params: Record<string, unknown>;
    referencePaths: string[];
  }) => invoke<string>("studio_start_image_task", {
    sessionId: args.sessionId, prompt: args.prompt,
    params: args.params, referencePaths: args.referencePaths,
  }),
  studioStartVideoTask: (args: {
    sessionId: string; prompt: string; params: Record<string, unknown>;
    referencePath?: string;
  }) => invoke<string>("studio_start_video_task", {
    sessionId: args.sessionId, prompt: args.prompt,
    params: args.params, referencePath: args.referencePath,
  }),
  studioCancelTask: (taskId: string) =>
    invoke<void>("studio_cancel_task", { taskId }),
  studioAddReferenceImage: (sessionId: string, filePath: string, width?: number, height?: number) =>
    invoke<StudioReferenceImage>("studio_add_reference_image", { sessionId, filePath, width, height }),
  studioDeleteReferenceImage: (id: string) =>
    invoke<void>("studio_delete_reference_image", { id }),
  studioListReferenceImages: (sessionId: string) =>
    invoke<StudioReferenceImage[]>("studio_list_reference_images", { sessionId }),
  onStudioTaskUpdate: (cb: (e: StudioTaskEvent) => void): Promise<UnlistenFn> =>
    listen<StudioTaskEvent>("studio-task-update", (event) => cb(event.payload)),
  onStudioMessageNew: (cb: (e: { session_id: string; message: StudioMessage; media_refs?: StudioMediaRef[] }) => void): Promise<UnlistenFn> =>
    listen<any>("studio-message-new", (event) => cb(event.payload)),
  onStudioMessageDelta: (cb: (e: StudioMessageDelta) => void): Promise<UnlistenFn> =>
    listen<StudioMessageDelta>("studio-message-delta", (event) => cb(event.payload)),

  // ── 媒体中心 ──
  centerListWorkspaces: (limit?: number, offset?: number) =>
    invoke<CenterWorkspace[]>("center_list_workspaces", { limit, offset }),
  centerCreateWorkspace: (kind: string, name: string) =>
    invoke<CenterWorkspace>("center_create_workspace", { kind, name }),
  centerRenameWorkspace: (id: string, name: string) =>
    invoke<void>("center_rename_workspace", { id, name }),
  centerSoftDeleteWorkspace: (id: string) =>
    invoke<void>("center_soft_delete_workspace", { id }),
  centerGetWorkspaceBundle: (id: string) =>
    invoke<CenterWorkspaceBundle>("center_get_workspace_bundle", { id }),
  centerListRounds: (workspaceId: string) =>
    invoke<CanvasRound[]>("center_list_rounds", { workspaceId }),
  centerGetRound: (roundId: string) =>
    invoke<CanvasRound | null>("center_get_round", { roundId }),
  centerSetActiveRound: (workspaceId: string, roundId: string) =>
    invoke<void>("center_set_active_round", { workspaceId, roundId }),
  centerStartImageRound: (args: {
    workspaceId: string; prompt: string; params: Record<string, unknown>;
    referencePaths: string[];
  }) => invoke<{ task_id: string; round_id: string }>("center_start_image_round", {
    workspaceId: args.workspaceId, prompt: args.prompt,
    params: args.params, referencePaths: args.referencePaths,
  }),
  centerStartVideoRound: (args: {
    workspaceId: string; prompt: string; params: Record<string, unknown>;
    referencePath?: string;
  }) => invoke<{ task_id: string; round_id: string }>("center_start_video_round", {
    workspaceId: args.workspaceId, prompt: args.prompt,
    params: args.params, referencePath: args.referencePath,
  }),
  centerSelectAssets: (roundId: string, assetIds: string[], selected: boolean) =>
    invoke<void>("center_select_assets", { roundId, assetIds, selected }),
  centerDeleteAssets: (assetIds: string[]) =>
    invoke<void>("center_delete_assets", { assetIds }),
  centerExportAssets: (assetIds: string[], destDir: string) =>
    invoke<ExportResult>("center_export_assets", { assetIds, destDir }),
  centerListRunningTasks: (workspaceId: string) =>
    invoke<StudioTask[]>("center_list_running_tasks", { workspaceId }),
  centerGetRoundAssets: (roundId: string) =>
    invoke<CenterAssetDetail[]>("center_get_round_assets", { roundId }),
  videoGetCapabilities: () =>
    invoke<VideoCapabilityEntry[]>("video_get_capabilities"),
  onCenterTaskUpdate: (cb: (e: CenterTaskEvent) => void): Promise<UnlistenFn> =>
    listen<CenterTaskEvent>("center-task-update", (event) => cb(event.payload)),

  // ── 实时翻译 (PR-1) ──
  liveGetActiveSession: () =>
    invoke<TranslationSession | null>("live_get_active_session"),
  liveGetRecentSegments: (sessionId: string, limit?: number) =>
    invoke<TranslationSegment[]>("live_get_recent_segments", { sessionId, limit }),
  liveBookmarkSegment: (segmentId: string, note?: string) =>
    invoke<void>("live_bookmark_segment", { segmentId, note }),
  liveUnbookmarkSegment: (segmentId: string) =>
    invoke<void>("live_unbookmark_segment", { segmentId }),
  liveListSupportedLanguages: (provider: string) =>
    invoke<SupportedLanguage[]>("live_list_supported_languages", { provider }),
  onSegmentUpdated: (cb: (e: { segment_id: string; is_bookmarked: boolean; bookmark_note: string | null }) => void): Promise<UnlistenFn> =>
    listen<any>("segment-updated", (event) => cb(event.payload)),
  // PR-3: 悬浮窗口
  liveShowFloatingSubtitle: () => invoke<void>("live_show_floating_subtitle"),
  liveHideFloatingSubtitle: () => invoke<void>("live_hide_floating_subtitle"),
  liveToggleFloatingSubtitle: () => invoke<void>("live_toggle_floating_subtitle"),
  liveShowFloatingInsight: () => invoke<void>("live_show_floating_insight"),
  liveHideFloatingInsight: () => invoke<void>("live_hide_floating_insight"),
  onFloatingWindowStateChanged: (cb: (e: { window: string; open: boolean }) => void): Promise<UnlistenFn> =>
    listen<any>("floating-window-state-changed", (event) => cb(event.payload)),
  // PR-4: 历史浏览与导出
  liveListSessions: (limit?: number, offset?: number) =>
    invoke<TranslationSession[]>("live_list_sessions", { limit, offset }),
  liveGetSessionSegments: (sessionId: string) =>
    invoke<TranslationSegment[]>("live_get_session_segments", { sessionId }),
  liveExportSubtitles: (sessionId: string, format: "srt" | "vtt", includeTranslation: boolean, outputPath: string) =>
    invoke<string>("live_export_subtitles", { sessionId, format, includeTranslation, outputPath }),
  liveClearSessionSegments: (sessionId: string) =>
    invoke<void>("live_clear_session_segments", { sessionId }),

  // ── 听析中心 AudioLab (PR-1) ──
  audiolabImportFiles: (paths: string[]) =>
    invoke<string[]>("audiolab_import_files", { paths }),
  audiolabListFiles: (limit?: number, offset?: number, search?: string, sort?: string) =>
    invoke<AudioFile[]>("audiolab_list_files", { limit, offset, search, sort }),
  audiolabGetFile: (fileId: string) =>
    invoke<AudioFile | null>("audiolab_get_file", { fileId }),
  audiolabRemoveFile: (fileId: string, deleteSource: boolean) =>
    invoke<void>("audiolab_remove_file", { fileId, deleteSource }),
  audiolabGetBundle: (sessionId: string) =>
    invoke<AudioLabBundle>("audiolab_get_bundle", { sessionId }),
  audiolabStartTranscription: (sessionId: string, audioFileId: string, parserKind: string, modelRef?: string) =>
    invoke<string>("audiolab_start_transcription", { sessionId, audioFileId, parserKind, modelRef }),
  audiolabListRunningTasks: (sessionId: string) =>
    invoke<StudioTask[]>("audiolab_list_running_tasks", { sessionId }),
  audiolabListStagePresets: () =>
    invoke<AudioStagePreset[]>("audiolab_list_stage_presets"),
  audiolabUpsertStagePreset: (preset: AudioStagePreset) =>
    invoke<void>("audiolab_upsert_stage_preset", { preset }),
  audiolabDeleteStagePreset: (stage: string) =>
    invoke<void>("audiolab_delete_stage_preset", { stage }),

  // ── 听析中心 AudioLab (PR-2: 播放) ──
  audiolabPlaybackOpen: (sessionId: string) =>
    invoke<AudioPlaybackInfo>("audiolab_playback_open", { sessionId }),
  onAudiolabTaskUpdate: (cb: (e: AudioLabTaskEvent) => void): Promise<UnlistenFn> =>
    listen<AudioLabTaskEvent>("audiolab-task-update", (event) => cb(event.payload)),
  onAudiolabStageDelta: (cb: (e: AudioLabStageDelta) => void): Promise<UnlistenFn> =>
    listen<AudioLabStageDelta>("audiolab-stage-delta", (event) => cb(event.payload)),

  // ── 听析中心 AudioLab (PR-3: 阶段生成 + AutoTags + Research) ──
  audiolabStartStage: (sessionId: string, stageKey: string, modelRef?: string) =>
    invoke<string>("audiolab_start_stage", { sessionId, stageKey, modelRef }),
  audiolabUpdateStageContent: (sessionId: string, stageKey: string, content: string) =>
    invoke<void>("audiolab_update_stage_content", { sessionId, stageKey, content }),
  audiolabStartPodcastTts: (sessionId: string, voiceLibRef?: string) =>
    invoke<string>("audiolab_start_podcast_tts", { sessionId, voiceLibRef }),
  audiolabGenerateAutoTags: (sessionId: string, modelRef?: string) =>
    invoke<string>("audiolab_generate_auto_tags", { sessionId, modelRef }),
  audiolabAddManualTag: (sessionId: string, tag: string) =>
    invoke<AudioAutoTag>("audiolab_add_manual_tag", { sessionId, tag }),
  audiolabRemoveAutoTag: (tagId: string) =>
    invoke<void>("audiolab_remove_auto_tag", { tagId }),
  audiolabAddResearchTopic: (sessionId: string, title: string, description: string) =>
    invoke<AudioResearchTopic>("audiolab_add_research_topic", { sessionId, title, description }),
  audiolabStartResearch: (topicId: string, modelRef?: string) =>
    invoke<string>("audiolab_start_research", { topicId, modelRef }),
  audiolabRemoveResearchTopic: (topicId: string) =>
    invoke<void>("audiolab_remove_research_topic", { topicId }),

  // ── 听析中心 AudioLab (PR-4: 段落编辑 + 导出 + 实时桥接) ──
  audiolabRenameSpeaker: (transcriptId: string, oldIndex: number, newLabel: string) =>
    invoke<void>("audiolab_rename_speaker", { transcriptId, oldIndex, newLabel }),
  audiolabUpdateSegment: (segmentId: string, text?: string, speaker?: string, startMs?: number, endMs?: number) =>
    invoke<void>("audiolab_update_segment", { segmentId, text, speaker, startMs, endMs }),
  audiolabExport: (sessionId: string, target: "srt" | "vtt" | "txt" | "json" | "markdown", outputPath: string, stageKey?: string) =>
    invoke<void>("audiolab_export", { sessionId, target, stageKey, outputPath }),
  audiolabImportFromRealtime: (realtimeSessionId: string) =>
    invoke<string>("audiolab_import_from_realtime", { realtimeSessionId }),
};
