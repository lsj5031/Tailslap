# Decision: Primary realtime protocol (plan 006)

**Date**: 2026-07-09  
**Status**: Accepted

## Choice

**Primary protocol: `openai`** (OpenAI-compatible `/v1/realtime?intent=transcription`).

**Deprecation**: Soft — keep `custom` factory branch and Settings option for one major line; document OpenAI-protocol as recommended for glm-asr-docker and local debug.

## Rationale

1. `AGENTS.md` local debug and `scripts/Test-OpenAIRealtimeTranscription.ps1` already assume OpenAI-protocol + `glm-nano-2512`.
2. `OpenAIRealtimeTranscriber` has dedicated unit tests; custom `RealtimeTranscriber` has none.
3. OpenAI path supports Bearer auth and richer session config (language/prompt now exposed).
4. Dual permanent stacks double reliability cost (CHANGELOG 3.0.x fixes often protocol-specific).

## Defaults

- New installs: `Transcriber.RealtimeProvider = "openai"` (code default updated).
- Existing configs with explicit `"custom"` keep custom via JSON.
- Missing property on old configs: deserializes to new default `openai` — acceptable; users on custom can re-select.

## Follow-ups

- Shared WebSocket transport extraction (optional tech plan).
- Settings tooltip already distinguishes providers; README should say OpenAI-protocol recommended.

## Rejected

- Dual-forever without primary: leaves onboarding ambiguous.
- Hard-delete custom in this release: breaks stacks still on stream URL.
