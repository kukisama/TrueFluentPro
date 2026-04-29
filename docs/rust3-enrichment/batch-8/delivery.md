# Batch-8 Delivery: TTS + Live Translation Enrichment

## Tasks Completed

| ID | Description | Files | Tests |
|----|-------------|-------|-------|
| T-001 | OpenAI TTS Provider | openai_tts.rs | 5 |
| T-002 | Enhanced SSML with express-as/style | azure_tts.rs | 3 |
| T-003 | TTS Tauri commands (synthesize_speech, list_voices) | audio.rs, lib.rs | 0 (cmd) |
| T-004 | Register OpenAI TTS for OpenAI-type endpoints | registration.rs | 0 (existing) |
| T-005 | Frontend API wiring (synthesizeSpeech, listVoices, transcribeAudio) | api.ts, types.ts | 0 (FE) |
| T-006 | LiveTranslationView TTS playback button | LiveTranslationView.tsx | 0 (FE) |
| T-007 | Module declarations + exports | lib.rs | 0 |

## New Tests: 8

- openai_tts: 5 (meta, urls_openai, urls_azure, list_voices_static, tts_model_default)
- azure_tts: 3 (styled_ssml_with_style, styled_ssml_with_role, styled_ssml_no_style)

## Code Stats

| Area | Lines | Delta |
|------|-------|-------|
| crates/ | 19,501 | +361 |
| src-tauri/ | 7,200 | +44 |
| frontend/ | 9,229 | +34 |
| **Total** | **35,930** | **+439** |
| Tests | 422 | +8 |

## Key Design Decisions

1. **OpenAI TTS static voice list** — no API endpoint exists, returns hardcoded 10 voices
2. **Candidate URL pattern** reused for TTS provider
3. **express-as SSML** — mstts namespace for style/styledegree/role, prosody for rate/pitch
4. **synthesize_speech command** writes to output_path — frontend decides file location
5. **Speaker button in LiveTranslationView** speaks the translation (or source if no translation)

## Phase 2 Complete

With batch-8, Phase 2 (Speech SDK + Audio) is fully delivered:
- Batch-6: Realtime translation + WebSocket + reconnect + subtitles
- Batch-7: OpenAI Whisper STT + Fast/Batch parsers + subtitle split
- Batch-8: TTS (Azure + OpenAI) + frontend integration
