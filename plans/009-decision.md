# Decision: Prompt templates (plan 009)

**Date**: 2026-07-09  
**Status**: Accepted (v1 = option A)

## Choice

**Option A** — keep single `Llm.RefinementPrompt` string in config; Settings offers **built-in presets** (`PromptPresets`) that overwrite the textbox. No schema migration.

## Rationale

- Lowest risk: no `TailSlapJsonContext` / Clone / migration work.
- Users already edit the prompt; presets speed common workflows (email, technical).
- Auto-enhance continues to share the active prompt (acceptable for v1).

## Deferred

- **Option B** multi-prompt list + `activeId` when users need concurrent templates without overwriting.
- Hotkey-bound profiles (separate product plan).

## Implemented thin slice

- `PromptPresets.cs` + Settings dropdown “Prompt preset”.
