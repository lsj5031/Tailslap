# TailSlap improvement plans

Generated and maintained by the **improve** skill. Executors should update a
plan's status when its commit is verified. The latest audit was planned against
commit `f3016ac` on **2026-07-30**.

Default branch: **`master`**.

## Reconciliation snapshot (2026-07-30)

- Plans **013–028** are DONE and reviewed on branch
  `improve/execute-all-plans` at commit `d795793` in worktree
  `T:\tailslap-lite-execute-all`. That branch contains 15 commits after
  `f3016ac`; none has been merged into the current branch
  `improve/direction-and-hygiene`.
- The cumulative worktree passed `dotnet build -c Release` and
  `dotnet test -c Release`: **304 tests passed on .NET 10**.
- The current branch remains at `f3016ac`. Its independently spot-checked
  baseline is **256 tests passed on .NET 9**; therefore the DONE labels below
  describe reviewed work awaiting integration, not behavior available on the
  current branch.
- Plan **029** remains BLOCKED because the operator did not authorize the paid
  hosted OpenAI live test. Its implementation gate still requires live HTTP
  and realtime evidence; documentation review alone is insufficient.
- Plan **012** remains a strategic roadmap. Execution requires macOS hardware,
  Apple signing/notarization credentials, and the accepted plan 028 config
  transfer decision.

**Executable now:** integrate or review `improve/execute-all-plans` in the
commit order recorded below. Do not re-execute plans 013–028 from scratch unless
that branch is discarded. Plan 029 is not executable without new operator
authorization; plan 012 is not executable in the current Windows environment.

## Recommended execution order

Use separate branches or worktrees per plan. The arrows are hard dependencies;
items on separate lines can proceed independently unless their plan warns about
overlapping files.

```text
013 paste verification → 014 delivery-result handling → 022 clipboard-history exclusion
015 keyboard ForceStop/race
016 OpenAI realtime buffers → 017 custom realtime send path
016 + 017 → 021 realtime error fingerprinting
018 config off hook thread
019 HistoryService seam/locking
020 transcription tests → 025 shared result sink
020 transcription tests → 027 HTTP language hint
016 + 021 → 029 hosted OpenAI live spike/preset
028 config export/import decision → prerequisite for strategic plan 012
024 WebRtcVad build guard
all applicable code plans → 023 .NET 10 migration → 026 final docs correction
```

Notes:

- **013 then 014** is the primary response to unreliable paste-at-cursor
  delivery.
- **020 before 025/027** provides characterization and request-capture tests.
- **023 runs last** among code plans to avoid TFM/workflow churn and overlapping
  drift checks. Run **026 after 023** so docs preserve the migrated runtime.
- Plans **016, 017, and 021** touch the realtime transcribers. Execute them
  sequentially even though only some dependencies are semantic.
- Plan **012** is a strategic macOS roadmap, not part of the immediate Windows
  hardening sequence. Its config migration phase should use plan 028's accepted
  decision.

## Current audit plans

All plans below are self-contained executor plans.

| Plan | Title | Priority | Effort | Risk | Status |
|------|-------|----------|--------|------|--------|
| [013](013-paste-delivery-verification.md) | Verify paste delivery and eliminate known silent-failure classes | P1 | M | MED | DONE on execution branch; unmerged |
| [014](014-unify-typing-paths.md) | Surface delivery failures and unify controller typing helpers | P1 | S-M | MED | DONE on execution branch; unmerged |
| [015](015-keyboardhook-forcestop-and-race.md) | Close KeyboardHook ForceStop gaps and atomicity races | P1 | S-M | LOW | DONE on execution branch; unmerged |
| [016](016-openai-realtime-buffer-fixes.md) | Fix OpenAI realtime ArrayPool and bounded-channel ownership | P1 | S | LOW | DONE on execution branch; unmerged |
| [017](017-custom-realtime-send-path.md) | Serialize custom realtime WebSocket sends and buffer ownership | P2 | S | LOW | DONE on execution branch; unmerged |
| [018](018-config-off-hook-thread.md) | Keep config disk I/O off the low-level keyboard hook thread | P1 | S-M | MED | DONE on execution branch; unmerged |
| [019](019-historyservice-test-seam.md) | Isolate HistoryService tests and serialize file access | P1 | M | LOW | DONE on execution branch; revised for Windows trim replacement bug; unmerged |
| [020](020-transcription-mode-tests.md) | Cover RemoteTranscriber, toggle state machine, and TextRefiner HTTP behavior | P2 | M | LOW | DONE on execution branch; revised with recording seam; unmerged |
| [021](021-realtime-error-fingerprinting.md) | Fingerprint realtime errors and surface DPAPI key failures | P2 | S | LOW | DONE on execution branch; unmerged |
| [022](022-clipboard-history-exclusion.md) | Exclude delivered text from Win+V and cloud clipboard | P2 | M | MED | DONE on execution branch; Win+V toggle and Notepad paste verified; unmerged |
| [023](023-dotnet10-migration.md) | Migrate from unsupported .NET 9 STS to .NET 10 LTS | P2 | M | MED | DONE on execution branch; automated gates verified; unmerged |
| [024](024-webrtcvad-build-guard.md) | Fail builds that omit the WebRtcVad native DLL | P2 | S | LOW | DONE on execution branch; unmerged |
| [025](025-shared-result-sink.md) | Extract shared transcription enhancement, delivery, and persistence | P2 | M | MED | DONE on execution branch; unmerged |
| [026](026-docs-correction-pass.md) | Correct logs, branch, dependency, P/Invoke, and release docs | P2 | S | LOW | DONE on execution branch; unmerged |
| [027](027-language-hint-http.md) | Forward ASR language hints in HTTP transcription | P2 | S | LOW | DONE on execution branch; unmerged |
| [028](028-config-export-import-design.md) | Design secure portable config export/import | P2 | S spike | LOW | DONE; accepted [decision](028-decision.md) is unmerged |
| [029](029-hosted-openai-quickstart-spike.md) | Verify hosted OpenAI and add a no-Docker preset/quickstart | P2 | M | MED | BLOCKED, operator declined paid live test; [decision](029-decision.md) needs live evidence |

## Strategic roadmap

| Plan | Title | Status | Dependency |
|------|-------|--------|------------|
| [012](012-macos-port.md) | Port TailSlap to macOS | PLANNED, multi-phase | Use accepted plan 028 decision before config migration |

## Completed plans

### Reliability and hygiene

| Plan | Title | Status |
|------|-------|--------|
| [001](001-run-tests-in-ci.md) | Run `dotnet test` in CI and align docs | DONE, verified |
| [002](002-stop-logging-transcription-plaintext.md) | Stop logging transcription text and response bodies | DONE, verified |
| [003](003-stop-llm-error-body-in-exceptions.md) | Stop embedding LLM error bodies in exceptions and logs | DONE, verified |
| [004](004-texttyper-baseline-on-success-only.md) | Advance TextTyper baseline only after successful delivery | DONE, verified |
| [005](005-serialize-realtime-transcription-processing.md) | Serialize realtime processing and queue maps | DONE, verified |

### Product and direction

| Plan | Title | Status |
|------|-------|--------|
| [006](006-spike-primary-realtime-protocol.md) | Choose primary realtime protocol | DONE, `openai` protocol selected |
| [007](007-realtime-transcription-history.md) | Persist realtime sessions to transcription history | DONE |
| [008](008-history-search-and-export.md) | Add history search and confirmed plaintext export | DONE |
| [009](009-spike-prompt-templates.md) | Choose and add prompt presets | DONE |
| [010](010-realtime-enhance-and-streamresults-clarity.md) | Realtime auto-enhance and StreamResults clarity | DONE |
| [011](011-realtime-language-and-session-prompt.md) | Realtime ASR language and session prompt | DONE |

## Considered and deferred

These findings were reviewed in the 2026-07-30 audit and deliberately not
turned into plans. Re-audit only when the deferral trigger changes.

| Finding | Evidence / concern | Decision |
|---------|--------------------|----------|
| Split large “god files” (DEBT-01) | `ClipboardService`, `SettingsForm`, `AudioRecorder`, and `MainForm` are very large | Defer until behavior around each target has stronger characterization tests; broad splits have low immediate user leverage |
| Inject all notifications (DEBT-05) | Controllers mix static `NotificationService` with `INotificationService` adapters | Defer; useful for testing, but plans 020/025 should first reveal the smallest seam |
| CI formatting gate and NuGet cache (DX-01/02) | CI tests/builds but does not enforce formatting or explicitly cache packages | Defer; current CI is fast and correctness/security work is higher leverage |
| Reduce VAD diagnostic log frequency (PERF-01) | Hot audio paths can emit frequent VAD diagnostics | Defer until profiling or field logs show measurable I/O/CPU impact |
| UI Automation stderr disclosure (SEC-03) | Selection fallback can interact with external UIA tooling/error output | Defer pending a reproducible sensitive-output path; keep logs fingerprint-only |
| Single-instance mutex squatting (SEC-04) | Named mutex can theoretically be acquired by another local process | Defer; local same-user denial of service has low impact and mitigation adds complexity |
| Latent `OperationCanceledException` fall-through (CORRECT-06) | A cancellation path may be treated as a generic failure | Defer until a reproducible mode/state sequence is identified |
| Config hot-reload event skip (CORRECT-07) | Save timestamps/watcher debounce can coalesce or skip a change | Defer after plan 018; reassess against its cache invalidation semantics |
| `SafeWaveInHandle` edge case (CORRECT-08) | Rare native-handle cleanup ordering concern | Defer without a failing test or WinMM reproduction |
| Full legacy `RealtimeTranscriber` protocol suite | Legacy `custom` provider remains supported but is not default | Do not build a broad suite now; plan 017 requires narrow queue/send/stop smoke tests |

## What previously landed

- **007/010**: realtime cleanup persists raw sessions; optional enhancement puts
  the improved draft on clipboard and stores the refinement pair.
- **008**: history query, search, and confirmed plaintext export.
- **011**: `Language` and `RealtimeSessionPrompt` in config, Settings, and
  OpenAI-protocol session payload.
- **006**: `openai` protocol is the default; `custom` remains for legacy stream
  endpoints.
- **009**: prompt presets without a config-schema change.
- **010**: StreamResults clarified as toggle-only HTTP streaming.

## Verification baselines

The isolated cumulative implementation branch recorded:

```text
dotnet test -c Release  →  304 passed on .NET 10
```

The current branch was spot-checked during reconciliation:

```text
dotnet test -c Release --no-restore  →  256 passed on .NET 9
```

Every implementation plan requires the live suite to pass without reducing the
test count. Use:

| Purpose | Command |
|---------|---------|
| Build | `dotnet build -c Release` |
| Test | `dotnet test -c Release` |
