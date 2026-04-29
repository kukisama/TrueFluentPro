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
  FloatingWindowState,
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
  TaskEvent,
  MonitorSnapshot,
  MonitorTaskItem,
  MonitorExecutionRecord,
  MonitorSettings,
  MonitorUiState,
  AudioTask,
  TaskExecution,
  TaskStatus,
  DeviceCodeResponse,
  AadAuthResult,
  AadTenantSelectionEvent,
  SegmentUpdatePayload,
  RealtimeSessionConfig,
  AudioDeviceInfo,
  StudioSession,
  StudioMessage,
  StudioMediaRef,
  StudioTask,
  StudioReferenceImage,
  StudioSessionBundle,
  StudioTaskEvent,
  StudioMessageDelta,
  StudioSearchProgress,
  CenterWorkspace,
  CenterWorkspaceBundle,
  CanvasRound,
  CenterAssetDetail,
  ExportResult,
  VideoCapabilityEntry,
  CenterTaskEvent,
  AudioFile,
  AudioLabBundle,
  AudioPlaybackInfo,
  AudioStagePreset,
  AudioLabTaskEvent,
  AudioLabStageDelta,
  AudioAutoTag,
  AudioResearchTopic,
  AudioLibraryItem,
  AudioLifecycle,
  BillingRecord,
  BillingSummary,
  ImagePipelineRequest,
  ImagePipelineResult,
  ModelCapabilityEntry,
  TaskEngineStats,
  TranslationHistory,
  BatchPackage,
  BatchSubtaskView,
  BatchBucketNav,
  VoiceInfo,
  TranscriptSegment,
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

  // ── Billing (2) ──
  getBillingRecords: (limit?: number) => invoke<BillingRecord[]>("get_billing_records", { limit }),
  getBillingSummary: () => invoke<BillingSummary>("get_billing_summary"),

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

  // ── Image (4) ──
  uploadImageFile: (endpointId: string, filePath: string) =>
    invoke<string>("upload_image_file", { endpointId, filePath }),
  generateImage: (request: ImageGenRequest) =>
    invoke<ImageGenResult[]>("generate_image", { request }),
  saveImage: (request: SaveImageRequest) =>
    invoke<SavedImage>("save_image", { request }),
  listSavedImages: (limit?: number) =>
    invoke<SavedImage[]>("list_saved_images", { limit }),

  // ── Video (1) ──
  generateVideo: (request: VideoGenRequest) =>
    invoke<string>("generate_video", { request }),

  // ── Image pipeline (2) ──
  runImagePipeline: (request: ImagePipelineRequest) =>
    invoke<ImagePipelineResult>("run_image_pipeline", { request }),
  getImageModelCatalog: () => invoke<ModelCapabilityEntry[]>("get_image_model_catalog"),

  // ── Prompt (1) ──
  optimizePrompt: (prompt: string, endpointId?: string) =>
    invoke<string>("optimize_prompt", { prompt, endpointId }),

  // ── Realtime control (3) ──
  startRealtimeTranslation: (config: RealtimeSessionConfig) =>
    invoke<string>("start_realtime_translation", { config }),
  stopRealtimeTranslation: (sessionId: string) =>
    invoke<void>("stop_realtime_translation", { sessionId }),
  listAudioDevices: () =>
    invoke<AudioDeviceInfo[]>("list_audio_devices"),

  // ── Speech (3) ──
  synthesizeSpeech: (endpointId: string, text: string, voice: string, format: string, outputPath: string) =>
    invoke<string>("synthesize_speech", { endpointId, text, voice, format, outputPath }),
  listVoices: (endpointId: string, locale: string) =>
    invoke<VoiceInfo[]>("list_voices", { endpointId, locale }),
  transcribeAudio: (endpointId: string, audioPath: string, lang: string) =>
    invoke<TranscriptSegment[]>("transcribe_audio", { endpointId, audioPath, lang }),

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

  // ── Floating windows (8) ──
  liveShowFloatingSubtitle: () => invoke<void>("live_show_floating_subtitle"),
  liveHideFloatingSubtitle: () => invoke<void>("live_hide_floating_subtitle"),
  liveToggleFloatingSubtitle: () => invoke<void>("live_toggle_floating_subtitle"),
  liveShowFloatingInsight: () => invoke<void>("live_show_floating_insight"),
  liveHideFloatingInsight: () => invoke<void>("live_hide_floating_insight"),
  saveFloatingWindowState: (window: string, x: number, y: number, width: number, height: number, opacity: number) =>
    invoke<void>("save_floating_window_state", { window, x, y, width, height, opacity }),
  getFloatingWindowState: (window: string) =>
    invoke<FloatingWindowState | null>("get_floating_window_state", { window }),
  setFloatingWindowOpacity: (window: string, opacity: number) =>
    invoke<void>("set_floating_window_opacity", { window, opacity }),

  // ── Event listeners (6) ──
  onRealtimeEvent: (cb: (e: RealtimeEvent) => void): Promise<UnlistenFn> =>
    listen<RealtimeEvent>("realtime-event", (event) => cb(event.payload)),
  onStreamToken: (cb: (e: StreamTokenEvent) => void): Promise<UnlistenFn> =>
    listen<StreamTokenEvent>("ai-stream-token", (event) => cb(event.payload)),
  onSegmentUpdated: (cb: (e: SegmentUpdatePayload) => void): Promise<UnlistenFn> =>
    listen<SegmentUpdatePayload>("segment-updated", (event) => cb(event.payload)),
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

  // ── Config transfer (4) ──
  exportConfig: () => invoke<string>("export_config"),
  importConfig: (json: string) => invoke<void>("import_config", { json }),
  writeTextFile: (path: string, content: string) => invoke<void>("write_text_file", { path, content }),
  readTextFile: (path: string) => invoke<string>("read_text_file", { path }),

  // ── Storage (2) ──
  getTranslationHistory: (limit?: number) =>
    invoke<TranslationHistory[]>("get_translation_history", { limit }),
  validateStorageConnection: (connectionString: string) =>
    invoke<void>("validate_storage_connection", { connectionString }),

  // ── Cloud (1) ──
  cloudHealthCheck: (url: string) =>
    invoke<string>("cloud_health_check", { url }),

  // ── Audio library (5) ──
  listAudioItems: () => invoke<AudioLibraryItem[]>("list_audio_items"),
  addAudioItem: (item: Omit<AudioLibraryItem, "id" | "created_at" | "updated_at">) =>
    invoke<AudioLibraryItem>("add_audio_item", { item }),
  deleteAudioItem: (itemId: string) => invoke<void>("delete_audio_item", { itemId }),
  getAudioLifecycle: (audioItemId: string) =>
    invoke<AudioLifecycle[]>("get_audio_lifecycle", { audioItemId }),
  updateLifecycleStage: (lifecycle: AudioLifecycle) =>
    invoke<void>("update_lifecycle_stage", { lifecycle }),

  // ── Task engine (8) ──
  updateTaskEngineConfig: (concurrency: number, timeoutSecs: number) =>
    invoke<void>("update_task_engine_config", { concurrency, timeoutSecs }),
  cleanupExpiredTasks: (days: number) => invoke<number>("cleanup_expired_tasks", { days }),
  listTasks: (status?: TaskStatus, limit?: number) =>
    invoke<AudioTask[]>("list_tasks", { status, limit }),
  getTaskExecutions: (taskId: string) => invoke<TaskExecution[]>("get_task_executions", { taskId }),
  submitTask: (task: Omit<AudioTask, "id" | "submitted_at">) =>
    invoke<AudioTask>("submit_task", { task }),
  cancelTask: (taskId: string) => invoke<void>("cancel_task", { taskId }),
  retryTask: (taskId: string) => invoke<void>("retry_task", { taskId }),
  getTaskEngineStats: () => invoke<TaskEngineStats>("get_task_engine_stats"),

  // ── Monitor (16) ──
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
  monitorSaveUiState: (uiState: MonitorUiState) =>
    invoke<void>("monitor_save_ui_state", { uiState }),
  monitorLoadUiState: () =>
    invoke<MonitorUiState | null>("monitor_load_ui_state"),
  monitorGetArchivedSnapshot: (dateFrom: string, dateTo: string) =>
    invoke<MonitorSnapshot>("monitor_get_archived_snapshot", { dateFrom, dateTo }),

  // ── Monitor events (2) ──
  onTaskEvent: (cb: (e: TaskEvent) => void): Promise<UnlistenFn> =>
    listen<TaskEvent>("task-event", (event) => cb(event.payload)),
  onMonitorSnapshotUpdate: (cb: (e?: MonitorSnapshot | null) => void): Promise<UnlistenFn> =>
    listen<MonitorSnapshot | null>("monitor-snapshot-update", (event) => cb(event.payload)),

  // ── AAD auth (5) ──
  aadStartDeviceCodeFlow: (endpointId: string, tenantId: string, clientId: string, scope?: string) =>
    invoke<DeviceCodeResponse>("aad_start_device_code_flow", { endpointId, tenantId, clientId, scope }),
  aadSelectTenant: (endpointId: string, tenantId: string, clientId: string, scope?: string) =>
    invoke<void>("aad_select_tenant", { endpointId, tenantId, clientId, scope }),
  aadRefreshToken: (endpointId: string) =>
    invoke<void>("aad_refresh_token", { endpointId }),
  aadLogout: (endpointId: string) =>
    invoke<void>("aad_logout", { endpointId }),
  onAadAuthResult: (cb: (e: AadAuthResult) => void): Promise<UnlistenFn> =>
    listen<AadAuthResult>("aad-auth-result", (event) => cb(event.payload)),
  onAadTenantSelection: (cb: (e: AadTenantSelectionEvent) => void): Promise<UnlistenFn> =>
    listen<AadTenantSelectionEvent>("aad-tenant-selection", (event) => cb(event.payload)),

  // ── 创作工坊 ──
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
  studioChatStream: (sessionId: string, text: string, endpointId: string, model: string, enableImageGen?: boolean, imageModelDeployment?: string, maxTurns?: number, enableWebSearch?: boolean, webSearchProviderId?: string) =>
    invoke<string>("studio_chat_stream", { sessionId, text, endpointId, model, enableImageGen, imageModelDeployment, maxTurns, enableWebSearch, webSearchProviderId }),
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
    listen<{ session_id: string; message: StudioMessage; media_refs?: StudioMediaRef[] }>("studio-message-new", (event) => cb(event.payload)),
  onStudioMessageDelta: (cb: (e: StudioMessageDelta) => void): Promise<UnlistenFn> =>
    listen<StudioMessageDelta>("studio-message-delta", (event) => cb(event.payload)),
  onStudioSearchProgress: (cb: (e: StudioSearchProgress) => void): Promise<UnlistenFn> =>
    listen<StudioSearchProgress>("studio-search-progress", (event) => cb(event.payload)),
  studioEditMessage: (messageId: string, newText: string) =>
    invoke<void>("studio_edit_message", { messageId, newText }),
  studioDeleteMessage: (messageId: string) =>
    invoke<void>("studio_delete_message", { messageId }),
  studioSendEdit: (sessionId: string, messageId: string, newText: string, endpointId: string, model: string, enableImageGen?: boolean, imageModelDeployment?: string) =>
    invoke<string>("studio_send_edit", { sessionId, messageId, newText, endpointId, model, enableImageGen, imageModelDeployment }),
  studioForkFromMessage: (sessionId: string, messageId: string) =>
    invoke<StudioSession>("studio_fork_from_message", { sessionId, messageId }),
  studioCountMessages: (sessionId: string) =>
    invoke<number>("studio_count_messages", { sessionId }),

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
  centerUpdateWorkspaceMode: (id: string, canvasMode: string, mediaKind: string) =>
    invoke<void>("center_update_workspace_mode", { id, canvasMode, mediaKind }),
  centerDeriveWorkspace: (sourceWorkspaceId: string, sourceAssetId: string, kind: string, name: string, referenceFilePath: string) =>
    invoke<CenterWorkspace>("center_derive_workspace", { sourceWorkspaceId, sourceAssetId, kind, name, referenceFilePath }),
  centerGetAllAssets: (workspaceId: string, limit?: number) =>
    invoke<CenterAssetDetail[]>("center_get_all_assets", { workspaceId, limit }),
  centerOpenFile: (path: string) =>
    invoke<void>("center_open_file", { path }),
  centerRevealInExplorer: (path: string) =>
    invoke<void>("center_reveal_in_explorer", { path }),
  centerExportWorkspace: (workspaceId: string, destDir: string, includeMetadata: boolean) =>
    invoke<ExportResult>("center_export_workspace", { workspaceId, destDir, includeMetadata }),
  videoGetCapabilities: () =>
    invoke<VideoCapabilityEntry[]>("video_get_capabilities"),
  onCenterTaskUpdate: (cb: (e: CenterTaskEvent) => void): Promise<UnlistenFn> =>
    listen<CenterTaskEvent>("center-task-update", (event) => cb(event.payload)),

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

  // ── Batch processing ──
  batchCreatePackage: (sessionId: string, audioFileId: string, displayName: string, includeSubtitle: boolean) =>
    invoke<BatchPackage>("batch_create_package", { sessionId, audioFileId, displayName, includeSubtitle }),
  batchStart: (packageIds: string[], includeSubtitle: boolean) =>
    invoke<number>("batch_start", { packageIds, includeSubtitle }),
  batchStop: (packageIds: string[]) =>
    invoke<void>("batch_stop", { packageIds }),
  batchPausePackage: (packageId: string) =>
    invoke<void>("batch_pause_package", { packageId }),
  batchResumePackage: (packageId: string) =>
    invoke<void>("batch_resume_package", { packageId }),
  batchRemovePackage: (packageId: string) =>
    invoke<void>("batch_remove_package", { packageId }),
  batchRestorePackage: (packageId: string) =>
    invoke<void>("batch_restore_package", { packageId }),
  batchGetBucketNav: () =>
    invoke<BatchBucketNav[]>("batch_get_bucket_nav"),
  batchGetPackages: (bucketKey: string) =>
    invoke<BatchPackage[]>("batch_get_packages", { bucketKey }),
  batchGetSubtasks: (packageId: string) =>
    invoke<BatchSubtaskView[]>("batch_get_subtasks", { packageId }),
  batchRegeneratePackage: (packageId: string) =>
    invoke<void>("batch_regenerate_package", { packageId }),
  batchRegenerateSubtask: (queueItemId: string) =>
    invoke<void>("batch_regenerate_subtask", { queueItemId }),
  validateBlobConnection: (connectionString: string) =>
    invoke<boolean>("validate_blob_connection", { connectionString }),
  batchSpeechTranscribe: (audioFilePath: string, locale: string) =>
    invoke<string>("batch_speech_transcribe", { audioFilePath, locale }),

  // ── Batch events ──
  onBatchPackageUpdate: (cb: (e: { package_id: string }) => void): Promise<UnlistenFn> =>
    listen<{ package_id: string }>("batch-package-update", (event) => cb(event.payload)),
};
