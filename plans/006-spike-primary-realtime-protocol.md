# Plan 006: Spike — choose a primary realtime protocol and a deprecation path

> **Executor instructions**: This is a **design/spike plan**, not a full rewrite.
> Follow steps in order. Produce the decision artifact in Step 3. Implement only
> the thin optional slice in Step 4 if the spike decision is unambiguous.
> If any STOP condition occurs, stop and report. When done, update
> `plans/README.md` unless a reviewer maintains the index.
>
> **Drift check (run first)**:
> `git diff --stat 6d0b6ca..HEAD -- TailSlap/RealtimeTranscriberFactory.cs TailSlap/RealtimeTranscriber.cs TailSlap/OpenAIRealtimeTranscriber.cs TailSlap/ConfigService.cs TailSlap/SettingsForm.cs AGENTS.md README.md`
> On mismatch of key excerpts, STOP and re-baseline before deciding.

## Status

- **Priority**: P2
- **Effort**: L (spike M; full consolidation L)
- **Risk**: MED (protocol choice affects existing local backends)
- **Depends on**: none (recommended after plans 001–005 reliability work)
- **Category**: direction
- **Planned at**: commit `6d0b6ca`, 2026-07-09

## Why this matters

TailSlap ships two realtime WebSocket stacks (`custom` stream URL vs OpenAI `/v1/realtime?intent=transcription`). Defaults still favor `custom` while maintainer debug notes and scripts favor OpenAI-protocol + `glm-nano-2512`. Every reliability fix risks landing on only one path. A written primary-protocol decision (with a deprecation or “keep both forever” rationale) stops drift and scopes any shared-transport refactor.

## Current state

| File | Role |
|------|------|
| `TailSlap/RealtimeTranscriberFactory.cs` | Branches on `RealtimeProvider` |
| `TailSlap/RealtimeTranscriber.cs` | Custom WS client (heartbeat, drop-oldest channel) |
| `TailSlap/OpenAIRealtimeTranscriber.cs` | OpenAI-protocol client (session update events) |
| `TailSlap/ConfigService.cs` | Default `RealtimeProvider = "custom"`; `WebSocketUrl` builder |
| `TailSlap/SettingsForm.cs` | Provider dropdown |
| `scripts/Test-OpenAIRealtimeTranscription.ps1` | OpenAI-path smoke only |
| `AGENTS.md` Local Debug Notes | Documents `realtimeProvider = "openai"` |
| `README.md` | Documents both endpoints + glm-asr-docker |

Factory today:

```csharp
// RealtimeTranscriberFactory.cs
if (string.Equals(config.RealtimeProvider, "openai", StringComparison.OrdinalIgnoreCase))
    return new OpenAIRealtimeTranscriber(config);
return new RealtimeTranscriber(config.WebSocketUrl, /* timeouts/heartbeat */);
```

**Conventions**: sealed services, DI factories, fingerprint logging, user-facing `NotificationService`. Match existing Settings dropdown patterns.

## Commands you will need

| Purpose | Command | Expected |
|---------|---------|----------|
| Build | `dotnet build -c Release` | exit 0 |
| Tests | `dotnet test -c Release` | exit 0 |
| Grep clients | search factory + both transcriber files | — |

## Scope

**In scope**:

- Investigation notes (in this plan file’s Decision section or `plans/006-decision.md` if long)
- Optional thin slice only if Step 4 applies: docs default alignment + Settings help text + CHANGELOG “Future” note — **not** deleting a client in this plan
- `plans/README.md` status

**Out of scope**:

- Full shared WebSocket transport extraction (follow-up after decision)
- Deleting `RealtimeTranscriber` or `OpenAIRealtimeTranscriber` in this plan
- Changing glm-asr-docker itself
- Plans 007–011 features

## Git workflow

- Branch: `advisor/006-realtime-protocol-spike`
- Commit message example: `Document primary realtime protocol decision`
- Do not push/PR unless asked.

## Spike questions (must answer in Decision)

1. What does current [glm-asr-docker](https://github.com/lsj5031/glm-asr-docker) actually expose for local users — custom stream, OpenAI realtime, or both?
2. How many in-repo references assume `custom` vs `openai` (defaults, tests, scripts, AGENTS)?
3. Which client has more reliability investment (heartbeat, recovery, tests)?
4. Deprecation options: (A) primary OpenAI + soft-deprecate custom, (B) primary custom + document OpenAI as cloud-only, (C) keep both forever with shared transport only.
5. Migration: if default flips, how do existing `%APPDATA%\TailSlap\config.json` files behave (missing key keeps old default via property initializer)?

## Steps

### Step 1: Inventory protocol usage

Document in a short table (in the Decision section):

- Default value of `TranscriberConfig.RealtimeProvider`
- URL builders for both modes (`WebSocketUrl`, `BuildOpenAIRealtimeUrl`)
- Test coverage: `OpenAIRealtimeTranscriberTests` vs any custom `RealtimeTranscriber` tests
- Script coverage

**Verify**: table filled; no source change required.

### Step 2: Compare client capabilities (read-only)

Skim both clients for: auth headers, heartbeat/stale detection, backpressure, session config, reconnect, tests. Note gaps (e.g. custom path historically no Bearer — security finding from earlier audit).

**Verify**: bullet list of parity gaps exists in Decision.

### Step 3: Write the Decision (required deliverable)

Add a `## Decision` section at the bottom of this file (or create `plans/006-decision.md`) containing:

- **Primary protocol**: `openai` | `custom` | dual-forever
- **Rationale**: 3–6 sentences with evidence from Steps 1–2
- **Default config change?** yes/no and target default string
- **Deprecation**: none | Settings warning | one-release shim | remove in version X
- **Follow-up implementation plans** (names only): e.g. “007 shared WS transport”, “008 flip default + docs”

**Verify**: Decision section answers all spike questions; human could implement without re-spiking.

### Step 4 (optional thin slice — only if Decision is clear)

If Decision chooses a primary and docs currently disagree:

1. Align `AGENTS.md` / `README.md` “recommended” wording with Decision (do not flip code default unless Decision explicitly says to).
2. Add one line under Settings provider dropdown tooltip/help if a control already has a place for it — avoid large SettingsForm refactors.
3. `dotnet test -c Release` still green.

If Decision is “need more external backend testing,” **skip code** and mark plan DONE with Decision only.

**Verify**: `dotnet test -c Release` → exit 0; no client deletion.

## Test plan

- Spike: no new tests required for Decision-only completion.
- If docs-only thin slice: full suite still passes.

## Done criteria

- [ ] Decision section exists and picks A/B/C (or dual-forever) with rationale
- [ ] Parity gap list recorded
- [ ] Follow-up work items named (even if “none yet”)
- [ ] No out-of-scope client deletions
- [ ] `dotnet test -c Release` exit 0 if any code/docs-in-repo were touched
- [ ] `plans/README.md` status updated

## STOP conditions

- Backend capabilities cannot be inferred and Decision would be a coin-flip — STOP with open questions for the maintainer rather than inventing backend behavior.
- Thin slice starts expanding into transport merge — STOP; that is a separate plan.
- Drift rewrote factory/provider model.

## Maintenance notes

- Reviewer: reject any PR that deletes a protocol without a written Decision and migration note.
- After Decision, prefer one follow-up plan for shared transport *or* default flip — not both in one PR unless trivial.

## Decision

_(Executor fills this section.)_
