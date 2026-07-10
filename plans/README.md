# Implementation Plans

Generated and extended by the **improve** skill.  
Last execution pass: **2026-07-09** — direction recommendations **006–011** implemented in working tree.

Executors / maintainers: update status when committing.

## Execution order & status

### Reliability / hygiene

| Plan | Title | Priority | Effort | Status |
|------|-------|----------|--------|--------|
| 001 | Run `dotnet test` in CI and align docs | P1 | S | DONE (verified) |
| 002 | Stop logging transcription text and response bodies | P1 | S | DONE (verified) |
| 003 | Stop embedding LLM error bodies in exceptions and logs | P1 | S | DONE (verified) |
| 004 | Advance TextTyper baseline only after successful delivery | P1 | S | DONE (verified) |
| 005 | Serialize realtime transcription processing and queue maps | P1 | M | DONE (verified; residual fixed) |

### Direction / product

| Plan | Title | Priority | Effort | Status |
|------|-------|----------|--------|--------|
| 006 | Spike: primary realtime protocol | P2 | M | DONE — see `006-decision.md`; default `openai` |
| 007 | Persist realtime sessions to transcription history | P1 | M | DONE |
| 008 | History search and export | P2 | M | DONE |
| 009 | Spike: prompt templates | P2 | M | DONE — see `009-decision.md`; presets UI |
| 010 | Realtime auto-enhance + StreamResults clarity | P2 | M | DONE — enhance B2 (clipboard); Settings/README |
| 011 | Realtime ASR language + session prompt | P2 | S–M | DONE |

## What landed (direction execution)

- **007/010**: `RealtimeTranscriptionController` takes `IHistoryService` + `ITextRefinerFactory`; cleanup persists raw session text; optional auto-enhance puts improved text on clipboard + refinement history pair.
- **008**: `HistoryQuery` helpers; search + confirmed plaintext export on both history forms.
- **011**: `TranscriberConfig.Language` / `RealtimeSessionPrompt`; OpenAI session payload; Settings fields.
- **006**: Decision doc; code default `RealtimeProvider = "openai"`; docs/AGENTS aligned.
- **009**: `PromptPresets` + Settings preset dropdown (schema unchanged).
- **010 Part A**: StreamResults checkbox + README clarify toggle-only HTTP streaming.

## Verification

```text
dotnet test -c Release  →  245 passed (as of implementation pass)
```

## Repo commands

| Purpose | Command |
|---------|---------|
| Build | `dotnet build -c Release` |
| Test | `dotnet test -c Release` |

Default branch: **`master`**.
