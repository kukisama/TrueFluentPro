// ── Model reference ──
export interface ModelReference { endpoint_id: string; model_id: string; }
// ── Endpoint & model ──
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
  auth_mode: string;
  azure_tenant_id: string;
  azure_client_id: string;
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

// ── Config sections ──
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
  task_engine_concurrency?: number;
  task_engine_timeout_secs?: number;
}
// ── Endpoint testing ──
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
export interface DiscoveredModel { id: string; display_name?: string; owned_by?: string; }
// ── Translation ──
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
export interface LanguageInfo { code: string; name: string; native_name: string; }
// ── Realtime translation ──
export interface RealtimeSessionConfig {
  source_lang: string;
  target_langs: string[];
  endpoint_id: string;
  enable_partial: boolean;
  profanity_filter: boolean;
  initial_silence_timeout_seconds?: number;
  end_silence_timeout_seconds?: number;
}
export interface RealtimeEvent {
  type: "SessionStarted" | "Recognizing" | "Recognized" | "Translated" | "SessionStopped" | "Error";
  data: Record<string, unknown>;
}
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
export interface SupportedLanguage { code: string; label: string; kind: string; }
// ── AI media ──
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
  text_model?: string;
  image_model?: string;
  previous_response_id?: string;
  reference_image_path?: string;
  image_edit_mode?: 'v1_multipart' | 'v2_responses_api';
  uploaded_file_ids?: string[];
}
export interface ImageGenResult {
  url?: string;
  base64?: string;
  revised_prompt?: string;
  response_id?: string;
  request_url: string;
  attempted_urls: string[];
  generate_seconds: number;
  download_seconds: number;
  actual_input_tokens?: number;
  actual_output_tokens?: number;
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
export interface VideoGenRequest {
  prompt: string;
  model: string;
  endpoint_id: string;
  size?: string;
  duration_seconds?: number;
  api_mode?: string;
  reference_image_path?: string;
  n?: number;
}
export interface VideoGenResult {
  video_id: string;
  status: string;
  download_url?: string;
  file_path?: string;
  generate_seconds?: number;
}
// ── AI completion ──
export interface CompletionRequest {
  messages: ChatMessagePayload[];
  model: string;
  temperature?: number;
  max_tokens?: number;
  endpoint_id: string;
}
export interface ChatMessagePayload {
  role: string;
  content: string | ContentPart[];
}
export type ContentPart =
  | { type: "text"; text: string }
  | { type: "image_url"; image_url: { url: string; detail?: string } };

export interface CompletionResponse {
  content: string;
  model: string;
  usage?: { prompt_tokens: number; completion_tokens: number; total_tokens: number };
}
export interface StreamTokenEvent {
  stream_id: string;
  token?: string;
  reasoning?: string;
  usage?: { prompt_tokens: number; completion_tokens: number };
  done?: boolean;
  error?: string;
}
// ── Provider ──
export interface VendorProfile {
  endpoint_type: string;
  label: string;
  badge: string;
  subtitle: string;
  glyph: string;
  default_auth_header: string;
  default_api_version: string;
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
  supported_auth_modes: string[];
  raw_json?: Record<string, unknown>;
}
export interface ProviderInfo { id: string; name: string; capabilities: string[]; }
// ── Sessions & messages ──
export interface Session {
  id: string;
  title: string;
  session_type: string;
  created_at: string;
  updated_at: string;
  message_count: number;
  token_total: number;
}
export interface SessionMessage {
  id: string;
  session_id: string;
  role: string;
  content: string;
  mode: string;
  reasoning_text?: string;
  prompt_tokens?: number;
  completion_tokens?: number;
  image_base64?: string;
  attachments?: string;
  created_at: string;
}
// ── System ──
export interface AppInfo { version: string; platform: string; arch: string; data_dir: string; }
// ── Audio device ──
export interface AudioDeviceInfo { id: string; name: string; device_type: string; is_default: boolean; }
// ── Translation history ──
export interface TranslationHistory {
  id: string;
  source_text: string;
  translated_text: string;
  source_lang: string;
  target_lang: string;
  provider: string;
  created_at: string;
}
// ── Lifecycle + task engine ──
export type LifecycleStage = "Transcribed" | "Summarized" | "MindMap" | "Insight" | "Research" | "PodcastScript" | "PodcastAudio" | "Translated";

export type StageStatus = "Pending" | "Running" | "Completed" | "Failed" | "Stale";
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
// ── Task monitor ──
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
export interface MonitorGlobalStats { total_executions: number; billable_executions: number; billable_tokens_in: number; billable_tokens_out: number; }
export interface MonitorSettings { max_transcription_concurrency: number; max_ai_concurrency: number; transcription_timeout_minutes: number; }
export interface MonitorSnapshot {
  buckets: MonitorBucket[];
  current_bucket: string;
  current_bucket_tasks: MonitorTaskItem[];
  global_stats: MonitorGlobalStats;
}
export interface MonitorUiState { active_bucket: string; sort_column: string; sort_ascending: boolean; selected_task_id: string | null; }

// ── Segment update event ──
export interface SegmentUpdatePayload {
  segment_id: string;
  is_bookmarked: boolean;
}

// ── AAD authentication ──
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
  reauth?: boolean;
  token?: {
    access_token: string;
    token_type: string;
    expires_in: number;
    scope: string;
  };
}
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

// ── Audio library ──
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

// ── Billing ──
export interface BillingRecord {
  id: string;
  task_id?: string;
  endpoint_id: string;
  model_id: string;
  prompt_tokens: number;
  completion_tokens: number;
  cost_usd?: number;
  created_at: string;
  status: string;
}

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

// ── Image pipeline ──
export interface ImagePipelineRequest {
  prompt: string;
  model: string;
  width: number;
  height: number;
  quality?: string;
  output_format?: string;
  background?: string;
  endpoint_id: string;
  optimize_prompt: boolean;
  reference_image_paths?: string[];
  mask_image_path?: string;
  previous_response_id?: string;
  output_directory?: string;
  text_model?: string;
  image_model?: string;
}

export interface ImagePipelineResult {
  original_prompt: string;
  optimized_prompt?: string;
  image_base64?: string;
  image_url?: string;
  revised_prompt?: string;
  steps_completed: string[];
  response_id?: string;
  result_file_paths: string[];
  error_message?: string;
}

// ── Model capability catalog ──
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
  supports_transparent_background: boolean;
  supports_input_fidelity: boolean;
  resolution_mode: string;
}

// ── TTS / STT ──
export interface VoiceInfo {
  id: string;
  name: string;
  locale: string;
  gender: string;
}

export interface TranscriptSegment {
  text: string;
  start_ms: number;
  end_ms: number;
  confidence: number;
  speaker?: string;
}
