import { invoke } from "@tauri-apps/api/core";
import { listen, type UnlistenFn } from "@tauri-apps/api/event";

import type {
  AppConfig,
  AiEndpoint,
  CompletionRequest,
  CompletionResponse,
  DiscoveredModel,
  EndpointTestProgress,
  EndpointTestReport,
  ImageGenRequest,
  ImageGenResult,
  LanguageInfo,
  ProviderInfo,
  RealtimeEvent,
  SaveImageRequest,
  SavedImage,
  Session,
  SessionMessage,
  StreamTokenEvent,
  SupportedLanguage,
  TranslateRequest,
  TranslateResponse,
  TranslationSegment,
  TranslationSession,
  VendorProfile,
  VideoGenRequest,
  AppInfo,
} from "./types";

export type { UnlistenFn };

// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
//  Type-safe invoke wrappers (40 commands + 6 event listeners)
// ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

export const api = {
  // ── Config (5) ──
  getConfig: () => invoke<AppConfig>("get_config"),
  updateConfig: (config: AppConfig) => invoke<void>("update_config", { config }),
  addEndpoint: (endpoint: AiEndpoint) => invoke<void>("add_endpoint", { endpoint }),
  removeEndpoint: (endpointId: string) => invoke<void>("remove_endpoint", { endpointId }),
  updateEndpoint: (endpoint: AiEndpoint) => invoke<void>("update_endpoint", { endpoint }),

  // ── Provider (3) ──
  listProviders: () => invoke<ProviderInfo[]>("list_providers"),
  refreshProviders: () => invoke<void>("refresh_providers"),
  getVendorProfiles: () => invoke<VendorProfile[]>("get_vendor_profiles"),

  // ── System (1) ──
  getAppInfo: () => invoke<AppInfo>("get_app_info"),

  // ── Translation (2) ──
  translateText: (request: TranslateRequest) =>
    invoke<TranslateResponse>("translate_text", { request }),
  getSupportedLanguages: () => invoke<LanguageInfo[]>("get_supported_languages"),

  // ── Sessions (6) ──
  listSessions: (sessionType?: string) =>
    invoke<Session[]>("list_sessions", { sessionType }),
  createSession: (title: string, sessionType: string) =>
    invoke<Session>("create_session", { title, sessionType }),
  deleteSession: (sessionId: string) =>
    invoke<void>("delete_session", { sessionId }),
  renameSession: (sessionId: string, newTitle: string) =>
    invoke<void>("rename_session", { sessionId, newTitle }),
  getSessionMessages: (sessionId: string) =>
    invoke<SessionMessage[]>("get_session_messages", { sessionId }),
  addMessage: (message: Omit<SessionMessage, "id" | "created_at">) =>
    invoke<void>("add_session_message", { message }),

  // ── AI Completion (2) ──
  aiComplete: (request: CompletionRequest) =>
    invoke<CompletionResponse>("ai_complete", { request }),
  aiCompleteStream: (request: CompletionRequest) =>
    invoke<string>("ai_complete_stream", { request }),

  // ── Endpoint testing (2) ──
  testEndpoint: (endpointId: string) =>
    invoke<EndpointTestReport>("test_endpoint", { endpointId }),
  discoverModels: (endpointId: string) =>
    invoke<DiscoveredModel[]>("discover_models", { endpointId }),

  // ── Image (3) ──
  generateImage: (request: ImageGenRequest) =>
    invoke<ImageGenResult[]>("generate_image", { request }),
  saveImage: (request: SaveImageRequest) =>
    invoke<SavedImage>("save_image", { request }),
  listSavedImages: (limit?: number) =>
    invoke<SavedImage[]>("list_saved_images", { limit }),

  // ── Video (1) ──
  generateVideo: (request: VideoGenRequest) =>
    invoke<string>("generate_video", { request }),

  // ── Prompt (1) ──
  optimizePrompt: (prompt: string, endpointId?: string) =>
    invoke<string>("optimize_prompt", { prompt, endpointId }),

  // ── Live translation (9) ──
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
  liveListSessions: (limit?: number, offset?: number) =>
    invoke<TranslationSession[]>("live_list_sessions", { limit, offset }),
  liveGetSessionSegments: (sessionId: string) =>
    invoke<TranslationSegment[]>("live_get_session_segments", { sessionId }),
  liveExportSubtitles: (
    sessionId: string,
    format: "srt" | "vtt",
    includeTranslation: boolean,
    outputPath: string,
  ) => invoke<string>("live_export_subtitles", { sessionId, format, includeTranslation, outputPath }),
  liveClearSessionSegments: (sessionId: string) =>
    invoke<void>("live_clear_session_segments", { sessionId }),

  // ── Floating windows (5) ──
  liveShowFloatingSubtitle: () => invoke<void>("live_show_floating_subtitle"),
  liveHideFloatingSubtitle: () => invoke<void>("live_hide_floating_subtitle"),
  liveToggleFloatingSubtitle: () => invoke<void>("live_toggle_floating_subtitle"),
  liveShowFloatingInsight: () => invoke<void>("live_show_floating_insight"),
  liveHideFloatingInsight: () => invoke<void>("live_hide_floating_insight"),

  // ── Event listeners (6) ──
  onRealtimeEvent: (cb: (e: RealtimeEvent) => void): Promise<UnlistenFn> =>
    listen<RealtimeEvent>("realtime-event", (event) => cb(event.payload)),
  onStreamToken: (cb: (e: StreamTokenEvent) => void): Promise<UnlistenFn> =>
    listen<StreamTokenEvent>("ai-stream-token", (event) => cb(event.payload)),
  onSegmentUpdated: (
    cb: (e: { segment_id: string; is_bookmarked: boolean; bookmark_note: string | null }) => void,
  ): Promise<UnlistenFn> =>
    listen("segment-updated", (event) =>
      cb(event.payload as { segment_id: string; is_bookmarked: boolean; bookmark_note: string | null }),
    ),
  onFloatingWindowStateChanged: (
    cb: (e: { window: string; open: boolean }) => void,
  ): Promise<UnlistenFn> =>
    listen("floating-window-state-changed", (event) =>
      cb(event.payload as { window: string; open: boolean }),
    ),
  onVideoProgress: (
    cb: (e: {
      task_id: string;
      status: string;
      message?: string;
      error?: string;
      file_path?: string;
      elapsed_seconds?: number;
      video_id?: string;
    }) => void,
  ): Promise<UnlistenFn> =>
    listen("video-progress", (event) =>
      cb(
        event.payload as {
          task_id: string;
          status: string;
          message?: string;
          error?: string;
          file_path?: string;
          elapsed_seconds?: number;
          video_id?: string;
        },
      ),
    ),
  onTestProgress: (cb: (e: EndpointTestProgress) => void): Promise<UnlistenFn> =>
    listen<EndpointTestProgress>("endpoint-test-progress", (event) => cb(event.payload)),
};
