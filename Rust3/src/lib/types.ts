// ── Model reference ──

export interface ModelReference {
  endpoint_id: string;
  model_id: string;
}

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

export interface DiscoveredModel {
  id: string;
  display_name?: string;
  owned_by?: string;
}

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

export interface LanguageInfo {
  code: string;
  name: string;
  native_name: string;
}

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

export interface SupportedLanguage {
  code: string;
  label: string;
  kind: string;
}

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
}

export interface ImageGenResult {
  url?: string;
  base64?: string;
  revised_prompt?: string;
  response_id?: string;
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

export interface ProviderInfo {
  id: string;
  name: string;
  capabilities: string[];
}

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
  mode?: string;
  reasoning_text?: string;
  prompt_tokens?: number;
  completion_tokens?: number;
  image_base64?: string;
  attachments?: string;
  created_at: string;
}

// ── System ──

export interface AppInfo {
  version: string;
  platform: string;
  arch: string;
  data_dir: string;
}

// ── Audio device ──

export interface AudioDeviceInfo {
  id: string;
  name: string;
  device_type: string;
  is_default: boolean;
}

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
