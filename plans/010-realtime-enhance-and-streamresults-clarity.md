# Plan 010: Realtime auto-enhance parity + StreamResults Settings/docs clarity

> **Executor instructions**: Two related product-parity items. Prefer small
> docs/Settings fixes first, then optional enhance-on-stop. Update
> `plans/README.md` when done.
>
> **Drift check (run first)**:
> `git diff --stat 6d0b6ca..HEAD -- TailSlap/RealtimeTranscriptionController.cs TailSlap/TranscriptionAutoEnhancer.cs TailSlap/TypelessController.cs TailSlap/SettingsForm.cs TailSlap/ConfigService.cs README.md CHANGELOG.md`

## Status

- **Priority**: P2
- **Effort**: M
- **Risk**: MED (rewriting already-typed realtime text is UX-sensitive)
- **Depends on**: 007 recommended if enhance should also write refinement history pairs
- **Category**: direction
- **Planned at**: commit `6d0b6ca`, 2026-07-09

## Why this matters

Auto-enhance runs for toggle + typeless (`TranscriptionAutoEnhancer`, CHANGELOG 3.0.5) but not realtime. Separately, `StreamResults` still appears in Settings/README while CHANGELOG 2.0.9 narrowed “standard” paths so live incremental streaming is owned by the realtime hotkey — users enable a checkbox that may not mean what they think.

## Current state

- `TranscriptionAutoEnhancer.MaybeEnhanceAsync` — shared gate: `EnableAutoEnhance`, threshold chars, LLM enabled, safety `ShouldUseEnhancedText`.
- Typeless applies enhanced text via `ApplyEnhancedTextAsync` (replace draft in place).
- Realtime: no enhancer calls; session text lives in `_typedText` / `_realtimeTranscriptionText`.
- Settings: `_transcriberStreamResults` checkbox bound to `Transcriber.StreamResults`.
- README still documents StreamResults as toggle streaming behavior.

## Commands you will need

| Purpose | Command | Expected |
|---------|---------|----------|
| Build | `dotnet build -c Release` | exit 0 |
| Tests | `dotnet test -c Release` | exit 0 |

## Scope

**In scope**:

### Part A — clarity (required)

- `README.md` StreamResults wording
- `SettingsForm.cs` checkbox label and/or adjacent help label describing **actual** behavior (read `TranscriptionController` to see when `StreamResults` is honored)
- Optional `CHANGELOG` Unreleased note

### Part B — realtime enhance-on-stop (required unless STOP on UX)

- `RealtimeTranscriptionController.cs` (+ DI for `ITextRefinerFactory` if missing)
- Reuse `TranscriptionAutoEnhancer` only
- Tests with Moq
- `plans/README.md`

**Out of scope**:

- Removing `StreamResults` property (breaking config) — clarify first; removal is a later decision
- Dual prompt for enhance (plan 009)
- Enhancing every interim partial

## Git workflow

- Branch: `advisor/010-realtime-enhance-streamresults`
- Commits: prefer two logical commits (docs/settings, then enhance)
- Do not push/PR unless asked.

## Product rules for enhance-on-stop

1. Run **once** when stopping/cleanup has a non-empty session transcript (same composition rules as plan 007).
2. Only if `EnableAutoEnhance` and length ≥ threshold and LLM enabled — pure call to `TranscriptionAutoEnhancer.MaybeEnhanceAsync`.
3. If enhanced text differs and passes safety:
   - **Preferred UX (match typeless spirit)**: attempt to replace on-screen text via existing typing/backspace path **only if** still focused on streaming target window; else put enhanced text on clipboard and notify user.
   - If replacement is too risky given controller complexity, **STOP Part B implementation** and ship Part A only + Decision note “enhance deferred: needs design for in-place replace.”
4. Do not block cleanup forever — use existing timeouts/cancellation patterns; on cancel keep original.
5. Log fingerprints/lengths only.

## Steps

### Step 1: Document actual StreamResults behavior

Read `TranscriptionController` (and any README lies). Update:

- Settings checkbox text to a precise label, e.g. “Stream HTTP transcription chunks (toggle mode)” or disable+tooltip if effectively unused.
- README advanced settings bullet.

**Verify**: grep StreamResults in README/Settings matches code paths.

### Step 2: Spike decision on enhance UX (short)

In PR notes or bottom of this plan, state chosen approach:

- **B1** in-place replace via backspace+type full enhanced  
- **B2** clipboard + notification only  
- **B3** defer enhance (Part A only)

**Verify**: choice recorded.

### Step 3: Implement Part B if B1 or B2

- Inject `ITextRefinerFactory` (and config already present).
- After final session text known (stop/cleanup), `await MaybeEnhanceAsync(...)`.
- Apply B1 or B2.
- If plan 007 history exists, persist raw transcription always; if enhanced differs, `Append` refinement pair like Typeless.

**Verify**: unit tests with mock refiner factory returning enhanced string; assert apply path side effects (clipboard mock / history mock).

### Step 4: Full suite

```powershell
dotnet test -c Release
```

## Test plan

| Case | Expected |
|------|----------|
| Enhance disabled | no refiner call |
| Below threshold | no refiner call |
| Enhanced accepted (B2) | clipboard set / notification path |
| Refiner throws | original kept; cleanup ok |

## Done criteria

- [ ] StreamResults user-facing text matches code
- [ ] Part B implemented **or** explicit deferral written in plan Decision with Part A done
- [ ] No interim enhance spam
- [ ] `dotnet test -c Release` passes
- [ ] `plans/README.md` updated

## STOP conditions

- In-place replace would require rewriting half of realtime typing — choose B2 or defer; do not hack.
- StreamResults still drives a path you cannot find — STOP and report call sites before changing labels.

## Maintenance notes

- Reviewer: ensure enhance cannot run twice on double-stop.
- Revisit when prompt templates (009) allow a dictation-specific enhance prompt.

## Decision (enhance UX)

_(Executor: B1 / B2 / B3 + one sentence.)_
