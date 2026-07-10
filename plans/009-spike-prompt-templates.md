# Plan 009: Spike — prompt templates (multi-prompt) design

> **Executor instructions**: Design/spike only unless Step 4 thin slice is
> explicitly justified. Fill the Decision section. Do not build hotkey profiles
> in this plan. Update `plans/README.md` when done.
>
> **Drift check (run first)**:
> `git diff --stat 6d0b6ca..HEAD -- TailSlap/ConfigService.cs TailSlap/SettingsForm.cs TailSlap/TextRefiner.cs TailSlap/TranscriptionAutoEnhancer.cs TailSlap/TailSlapJsonContext.cs`

## Status

- **Priority**: P2
- **Effort**: M–L (spike M; full UI L)
- **Risk**: MED (config shape migration)
- **Depends on**: none
- **Category**: direction
- **Planned at**: commit `6d0b6ca`, 2026-07-09

## Why this matters

Users already customize one `Llm.RefinementPrompt` (Settings + config). CHANGELOG Future Considerations asks for “template management” and “multiple hotkey profiles.” Auto-enhance and refine share that single prompt. Without a template model, context switching (email vs code vs meeting notes) means overwriting the prompt string.

## Current state

```csharp
// ConfigService.cs LlmConfig
public string RefinementPrompt { get; set; } = DefaultRefinementPrompt;
public string GetEffectiveRefinementPrompt() =>
    string.IsNullOrWhiteSpace(RefinementPrompt) ? DefaultRefinementPrompt : RefinementPrompt.Trim();
```

- `TextRefiner` uses `_cfg.GetEffectiveRefinementPrompt()` as system message.
- `TranscriptionAutoEnhancer` clones `cfg.Llm` and calls `RefineAsync` — same prompt.
- Settings: one multiline `_refinementPrompt` TextBox.
- Serialization: `TailSlapJsonContext` source-generated — **new types must be registered** if added.

**Convention**: camelCase JSON config; DPAPI only for secrets; clone methods on config types for mutation safety.

## Commands you will need

| Purpose | Command | Expected |
|---------|---------|----------|
| Build | `dotnet build -c Release` | exit 0 |
| Tests | `dotnet test -c Release` | exit 0 |
| Config tests | `dotnet test -c Release --filter FullyQualifiedName~ConfigService` | pass |

## Scope

**In scope**:

- Decision write-up in this file
- Optional thin slice **only if Decision picks “minimal presets”**: hard-coded named presets in UI (not full CRUD), still storing the chosen text in `RefinementPrompt` — **no** breaking config schema
- `plans/README.md`

**Out of scope**:

- Hotkey profiles binding different prompts to different hotkeys (future plan)
- Cloud prompt marketplace
- Changing auto-enhance safety heuristics

## Git workflow

- Branch: `advisor/009-prompt-templates-spike`
- Commit example: `Document prompt template design`
- Do not push/PR unless asked.

## Spike questions

1. Schema options:
   - **A.** Keep single `refinementPrompt`; UI only offers insertable presets (no schema change).
   - **B.** `refinementPrompts: [{ id, name, body }]` + `activeRefinementPromptId` with migration from old string.
   - **C.** B + separate `autoEnhancePromptId`.
2. How does source-gen JSON (`TailSlapJsonContext`) force registration cost for B/C?
3. Migration: existing configs with only `refinementPrompt` must round-trip.
4. Minimum UI for v1: dropdown of presets vs full editor list?

## Steps

### Step 1: Read serialization path

Confirm how `AppConfig` is deserialized and whether unknown properties are ignored. Note `Clone()` methods that must copy new fields.

**Verify**: notes in Decision.

### Step 2: Propose schema + migration

Write recommended option A/B/C with:

- Example JSON snippet
- Load algorithm (if missing templates → seed Default)
- Save algorithm
- Risk

**Verify**: Decision includes explicit recommendation.

### Step 3: Decision deliverable

Fill `## Decision` with:

- Chosen option
- v1 UI scope
- Whether auto-enhance shares prompt (default: yes)
- Follow-up plan names for implementation

### Step 4 (optional thin slice — option A only)

If Decision = A:

- Add a dropdown of 2–3 built-in preset names in Settings near the prompt box (“Dictation polish”, “Concise email”, “Preserve technical terms”) that **replace** TextBox content from constants (user can still edit).
- Do not add new config properties.
- Tests: optional — constants non-empty.
- `dotnet test -c Release`.

If Decision = B or C: **do not implement** in this plan; stop after Decision.

## Test plan

- Spike Decision-only: no tests.
- Option A thin slice: full suite green.

## Done criteria

- [ ] Decision answers all spike questions
- [ ] Migration story for existing configs documented
- [ ] No half-migrated schema in code without tests
- [ ] `plans/README.md` updated

## STOP conditions

- Implementing B/C without JsonContext + Clone + Settings + tests — STOP mid-flight; finish Decision only.
- Coupling templates to hotkey profiles in same PR — out of scope.

## Maintenance notes

- Implementation plan after B should be separate and include ConfigServiceTests round-trip.

## Decision

_(Executor fills.)_
