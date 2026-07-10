# Plan 008: Add search and export to encrypted history UIs

> **Executor instructions**: Design-first, then a thin vertical slice. Do not
> redesign HistoryService encryption. On STOP, report. Update `plans/README.md`
> when done unless reviewer maintains the index.
>
> **Drift check (run first)**:
> `git diff --stat 6d0b6ca..HEAD -- TailSlap/HistoryForm.cs TailSlap/TranscriptionHistoryForm.cs TailSlap/HistoryService.cs TailSlap/IHistoryService.cs`
> Re-read forms if UI layout drifted.

## Status

- **Priority**: P2
- **Effort**: M
- **Risk**: MED (export is intentional plaintext declassification)
- **Depends on**: none (benefits from any HistoryForm decrypt cache work if present)
- **Category**: direction
- **Planned at**: commit `6d0b6ca`, 2026-07-09

## Why this matters

`CHANGELOG.md` Future Considerations explicitly lists “Refinement history with search and export capabilities.” Today both history forms only list, copy, refresh, and clear. Users cannot find an old snippet or take history off-box for backup/migration without manual copy-one-by-one.

## Current state

- `HistoryForm.cs` — refinement history: `ListBox` + Original/Refined/Diff tabs; Copy buttons; timer refresh calling `ReadAll()`.
- `TranscriptionHistoryForm.cs` — transcription history: list + copy + clear.
- `HistoryService` — DPAPI JSONL, `MaxEntries = 50`, `ReadAll` / `ReadAllTranscriptions`.
- No search TextBox, no SaveFileDialog export.

**Convention**: WinForms dialog patterns in existing history forms; `NotificationService` for errors; DPI via `DpiHelper.Scale`.

## Commands you will need

| Purpose | Command | Expected |
|---------|---------|----------|
| Build | `dotnet build -c Release` | exit 0 |
| Tests | `dotnet test -c Release` | exit 0 (add unit tests for filter helpers if extracted) |

## Scope

**In scope**:

- `TailSlap/HistoryForm.cs`
- `TailSlap/TranscriptionHistoryForm.cs`
- Optional small pure helper e.g. `HistoryExport.cs` / filter static methods for testability
- `TailSlap.Tests/*` for pure filter/export formatting
- `CHANGELOG.md` under Unreleased/Added if repo practice requires (optional)
- `plans/README.md`

**Out of scope**:

- Raising MaxEntries beyond 50
- Cloud sync
- Changing DPAPI format
- Full-text index files on disk
- Export without user confirmation

## Git workflow

- Branch: `advisor/008-history-search-export`
- Commit example: `Add history search and export`
- Do not push/PR unless asked.

## Product rules

1. **Search**: case-insensitive substring over decrypted fields the list already shows (refinement: original+refined+model; transcription: text). Filter the in-memory list after `ReadAll` — do not change on-disk format.
2. **Export**: user picks path via `SaveFileDialog`. Formats: plain text or JSON lines **plaintext** (document in dialog title). Before write, `MessageBox`/`BrandedMessageBox` confirm: data will be stored unencrypted.
3. **Empty selection export**: export **filtered** list (all visible rows), not only selected row — unless implementing both “export all visible” is clearer as one button “Export…” .
4. Never write export path into logs with file contents.

## Steps

### Step 1: Extract pure filter helpers (TDD-friendly)

```csharp
// Example shape — names flexible
static bool MatchesRefinement(string query, string original, string refined, string model);
static bool MatchesTranscription(string query, string text);
```

Empty/whitespace query ⇒ match all.

**Verify**: unit tests for empty query, case-insensitivity, no-match.

### Step 2: Refinement HistoryForm UI

- Add `TextBox` “Search” above the list; on TextChanged (debounce optional; 50 entries so immediate filter is OK) rebind list from cached/`ReadAll` results.
- Prefer **cache list after load** in a field to avoid re-decrypting every keystroke if `ReadAll` is still heavy (align with prior perf finding). If cache already exists in working tree, use it.
- Add `Export…` button next to Copy/Clear. Confirm → `SaveFileDialog` → write UTF-8 text.

Export text format (suggested):

```text
# TailSlap refinement history export
# Exported: <ISO timestamp>
---
[<timestamp>] model=<model>
ORIGINAL:
...
REFINED:
...
```

**Verify**: build; manual not required in CI — unit tests for formatter if extracted.

### Step 3: TranscriptionHistoryForm UI

Same search + export pattern with simpler records `(Timestamp, Text, DurationMs)`.

**Verify**: `dotnet build -c Release`.

### Step 4: Tests + suite

```powershell
dotnet test -c Release
```

## Test plan

| Case | Expected |
|------|----------|
| Empty query | all entries match |
| Query hits refined only | match |
| Query no hit | no match |
| Export formatter | stable headers / separators |

WinForms click tests not required.

## Done criteria

- [ ] Both history forms have search filtering visible list
- [ ] Both have Export with explicit unencrypted confirmation
- [ ] Filter helpers unit-tested
- [ ] `dotnet test -c Release` passes
- [ ] No change to on-disk encryption format
- [ ] `plans/README.md` updated

## STOP conditions

- Export is requested as still-encrypted blobs — different feature; STOP for product clarification.
- Forms were heavily refactored in working tree so controls cannot be found — re-map UI then continue or STOP.
- Implementing search “requires” server/index — reject; keep in-memory filter.

## Maintenance notes

- Reviewer: confirm confirm-dialog wording is scary enough for DPAPI export.
- Follow-up: optional export selected row only; raise MaxEntries later separately.
