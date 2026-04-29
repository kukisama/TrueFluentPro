# Batch-8 Work Order: TTS + Live Translation Enrichment

## Scope
Phase 2 final batch. Add OpenAI TTS provider, TTS Tauri commands, enhanced SSML,
and frontend TTS API wiring.

## Tasks

| ID | Description | Output |
|----|-------------|--------|
| T-001 | OpenAI TTS provider (openai_tts.rs) | New file, 5 tests |
| T-002 | Enhanced SSML builder with express-as/style | Update azure_tts.rs, 3 tests |
| T-003 | TTS Tauri commands (synthesize_speech, list_voices) | audio.rs addition |
| T-004 | Register OpenAI TTS for OpenAI-type endpoints | registration.rs update |
| T-005 | Frontend API wiring (synthesizeSpeech, listVoices) | api.ts, types.ts |
| T-006 | LiveTranslationView TTS playback button | View enhancement |
| T-007 | Module declarations + lib.rs | lib.rs update |
