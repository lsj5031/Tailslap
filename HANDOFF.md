# Handoff — TailSlap (T:\tailslap-lite)

**Date:** 2026-08-02 ~23:11 local. **App running:** TailSlap.exe PID 14108 (published exe timestamp 22:49:23, includes the single-delivery + window-guard fix; NOT the fixes below).

---

## 1. Current active bug (user's top priority)

User tested **push-to-talk (Ctrl+Win hold)** dictating one phrase in Chinese into a Chrome input box. Reported two failures:

1. **"Pasted twice"** — output was:
   `不用，我来看到底行不行。不用，我来看看到底行不行。`
   ⚠️ **CRITICAL CLUE: the two halves are NOT identical** — "我来看到底行不行" vs "我来看看到底行不行" (differ by one 看). This is NOT a double delivery; it is the **ASR server sending a REVISED full-transcript snapshot** (server corrected its own transcription mid-stream: 看到底 → 看看到底) and the merge logic appending it as a disjoint continuation.
2. **"Not pasted into my chrome input box"** — paste did not land in Chrome.

### Root-cause hypotheses (analysis done, code verified)

**A. "Pasted twice" → `TextTyper.MergeStreamChunk` (TailSlap/TextTyper.cs) can't recognize revised snapshots.**
Current logic handles: resent-suffix (`EndsWith`), full-snapshot superset (`chunk.StartsWith(accumulated)`), and boundary overlap (suffix/prefix loop), then falls through to blind append. A **revised snapshot** (long common prefix, diverges mid-string, similar length) is NOT caught → appended → duplication.
**Fix direction:** add a revision-detection step before the overlap loop: if common-prefix length ≥ some threshold (e.g. ≥ max(4, ~50% of shorter string)) AND the chunk is not much longer than accumulated (revision, not continuation), treat the chunk as the new full transcript and **replace** accumulated with it. Add a unit test using the user's exact pair:
`MergeStreamChunk("不用，我来看到底行不行。", "不用，我来看看到底行不行。") == "不用，我来看看到底行不行。"`
Also verify existing tests still pass (TextTyperTests.cs MergeStreamChunk suite, 10+ cases).

**B. "Not pasted into Chrome" → Win key opens Start menu and steals focus.**
Push-to-talk is a **modifier-only hotkey (Ctrl+Win, Key=0)**. `KeyboardHook` (TailSlap/KeyboardHook.cs) uses WH_KEYBOARD_LL but **never suppresses the Win key** — `HookCallback` always `CallNextHookEx`. So holding Ctrl+Win pops the Start menu, which takes foreground focus.
Then the window guard in `TranscriptionResultSink.DeliverAsync` (TailSlap/TranscriptionResultSink.cs, `DeliverFinalText` case) compares `_getForegroundWindow()` (now Start menu / whatever regained focus) against `request.TargetWindow` (captured via `NativeMethods.GetForegroundWindow()` at key-up in TypelessController.HandleKeyUpAsync) → mismatch → text left on clipboard + "window changed" warning instead of pasting into Chrome. Exactly the user's symptom.
**Fix directions (choose):**
- In `KeyboardHook.HookCallback`, swallow (`return`) the Win key WM_KEYDOWN/WM_SYSKEYDOWN events when the pressed vk is VK_LWIN/VK_RWIN **and** the configured hotkey includes MOD_WIN and the combo is being held (prevents Start menu entirely). Must also ensure `GetAsyncKeyState(VK_LWIN)` still reports it held for `GetCurrentModifiers()` (it does — swallow only the hook return, not the async state).
- Optionally also capture `TargetWindow` at key-down rather than key-up (key-down fires before the user's hand settles) — but suppressing Win is the primary fix.
- Consider comparing top-level ancestor HWNDs (GetAncestor GA_ROOT) rather than raw HWND equality, since HWNDs can differ for the same window.

## 2. What was already fixed (do not redo — verify still intact)

- **Per-chunk delivery removed** for push-to-talk + toggle: TypelessController/TranscriptionController no longer call `TextTyper.TypeAsync` per SSE chunk. They accumulate via `TextTyper.MergeStreamChunk` and deliver **once** through `_resultSink.ProcessAsync(new TranscriptionResultRequest(text, cfg, durationMs, TranscriptionDeliveryPolicy.DeliverFinalText, ResultsAlreadyStreamed: false, TargetWindow: targetWindow))`.
- **Window guard** added in TranscriptionResultSink (injectable `Func<IntPtr> _getForegroundWindow`); if foreground changed → `SetTextAsync` + warning, no paste.
- Earlier session: failed-history recording, log-level sweep, "Recent Issues" diagnostic menu, UiTheme "Stockroom Tags" redesign (see PRODUCT.md + DESIGN.md), RealtimeTranscriptionController keeps live typing (user chose this).
- **339/339 tests pass** (dotnet test -c Release). Build clean.

## 3. Next-session plan (suggested order)

1. Reproduce from the user's exact log evidence first: `tail` `%APPDATA%\TailSlap\logs\app.jsonl` (filter `TypelessController|MergeStreamChunk|SSE chunk|sha256|Paste|window|Window|sink`) to confirm the chunk sequence and the guard decision. (Note: basher/tmux agents intermittently fail with "Insufficient credits" — retry; code-searcher agent works and can read project files, but the log lives OUTSIDE the project, so a terminal agent is required for it.)
2. Implement revision detection in `TextTyper.MergeStreamChunk` + unit tests (user's exact pair).
3. Implement Win-key suppression in `KeyboardHook.HookCallback` (+ unit tests in KeyboardHookTests.cs if any exist for hook logic).
4. Consider the GA_ROOT HWND comparison improvement in the sink guard.
5. Build, run full test suite, code-reviewer-deepseek-flash review.
6. Rebuild release exe + relaunch for the user to test.

## 4. Commands

- Build/test: `cd /t/tailslap-lite && dotnet build -c Release` then `dotnet test -c Release` (339 tests)
- Publish: `dotnet publish TailSlap/TailSlap.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true`
- Exe: `TailSlap/bin/Release/net10.0-windows/win-x64/publish/TailSlap.exe`
- Restart: `taskkill //IM TailSlap.exe //F`; relaunch via `cmd //c start "" "$(pwd -W)/TailSlap.exe"` — NOTE: launching in a basher that also prints output can hang (pipe quirk); verify PID in a separate command.
- Log: `%APPDATA%\TailSlap\logs\app.jsonl` (JSONL; grep `"level":"Warning"` etc.)
- **Check git status first** — the single-delivery + window-guard changes may be uncommitted; verify before treating them as committed baseline.

## 5. Key files

- `TailSlap/TextTyper.cs` — `MergeStreamChunk` (the merge fix), `TypeAsync`, baseline logic
- `TailSlap/KeyboardHook.cs` — WH_KEYBOARD_LL, modifier-only hotkey, Win-key suppression target
- `TailSlap/TypelessController.cs` — push-to-talk, TargetWindow capture at key-up
- `TailSlap/TranscriptionController.cs` — toggle mode, same pattern
- `TailSlap/TranscriptionResultSink.cs` — final delivery + window guard
- `TailSlap/MainForm.cs` — hotkey wiring, overlay animation events
- `TailSlap/RecordingOverlayForm.cs` — overlay (WS_EX_NOACTIVATE + ShowWithoutActivation, doesn't steal focus)
- Tests: `TailSlap.Tests/TextTyperTests.cs`, `TranscriptionResultSinkTests.cs`, `TypelessControllerTests.cs`, `TranscriptionControllerTests.cs`, `KeyboardHookTests.cs`
- Docs: `knowledge.md`, `PRODUCT.md`, `DESIGN.md`, `plans/*.md` (esp. 013-paste-delivery-verification, 014-unify-typing-paths, 015-keyboardhook-forcestop-and-race, 025-shared-result-sink)

## 6. UI alignment bug (separate task — needs a VISION-capable agent with Orca computer-use)

User reported after the "Stockroom Tags" redesign: "seems not much improved, as some of them was not aligned." Affected surfaces the user named: **Settings form, Refinement History, Transcription History, Recent Issues / Diagnostics, Message boxes, Hotkey capture dialog.** The user's 4 screenshots could NOT be viewed by the previous model (no vision) — the screenshots are the ground truth, so the next agent must **look at the actual rendered forms** and fix what is visibly off.

### How to look at the forms (Orca computer-use)

- Orca CLI: `/c/Users/lsj50/AppData/Local/Programs/Orca/resources/bin/orca` — run `orca skills get computer-use` for the full guide; prefer `--json`; screenshots land at `screenshot.path`.
- App is **running**: TailSlap.exe PID 14108; exe `T:\tailslap-lite\TailSlap\bin\Release\net10.0-windows\win-x64\publish\TailSlap.exe`. It is tray-only (no main window), so it may not appear in `list-apps` — use `orca computer get-app-state --app pid:14108 --json` or `list-windows --app pid:14108`.
- Open each surface via the **tray icon right-click menu** (Settings, History, Transcription History, Recent Issues, Diagnostics) and via Settings → hotkey field (HotkeyCaptureForm) and destructive actions / warnings (BrandedMessageBox). Use Orca click on the tray menu items, then `get-app-state` to screenshot each dialog. Compare with the code before editing.
- If the app window can't be reached/restored, `--restore-window` may help; screenshots may be occluded — trust the accessibility tree plus screenshot together.

### Known concrete alignment suspects (from code reading — verify visually)

1. **SettingsForm label-column widths are inconsistent across tabs**: General tab uses `ColumnStyle(Absolute, DpiHelper.Scale(140))`, LLM tab `DpiHelper.Scale(130)`, Recording + Advanced tabs `DpiHelper.Scale(150)`. Labels jump to different x positions when switching tabs → looks misaligned. Unify to one width constant (e.g. `Scale(150)`).
2. **HotkeyCaptureForm**: initial `Height = DpiHelper.Scale(340)` is SMALLER than `MinimumSize` height `DpiHelper.Scale(360)` → form snaps to min-size on open (layout jump). Also the buttons FlowLayoutPanel has `Padding = DpiHelper.Scale(new Padding(10))` while buttons have no Margin → check button row spacing/edges.
3. **Status/button bottom bars differ per form**: HistoryForm + TranscriptionHistoryForm use an AutoSize status row with a 10×10 lamp + label; RecentIssuesForm + DiagnosticsForm use a fixed-height `Height = DpiHelper.Scale(20)` label with no lamp. Bottom bars don't line up across the suite.
4. **BrandedMessageBox**: header lamp `Margin = new Padding(10, 5, 0, 0)` vs orange tag `Margin = (0, 4, 8, 0)` — lamp may sit off the caption baseline; logo 48×48 vs label text — check vertical centering.
5. **RecentIssuesForm/DiagnosticsForm** use `SystemColors.ControlText` for list text while History forms use `UiTheme.Ink` — minor, but check visible contrast.
6. **History/TranscriptionHistory ListView columns**: fixed widths (Time 118, State 64, Model 150, Duration 76) + a `-2` fill column — check the fill column actually stretches and headers align with rows.

### Fix rules (respect the design system)

- Follow `UiTheme.cs` tokens/fonts (Ground #FAF9F6, Ink #1F1F1F, Orange #FF6A00, quoted caps tags, mono data). Do NOT dispose cached fonts; do not introduce per-dialog `new Font` (use `UiTheme.BodyFont`/`MonoFont`/`CapsFont`).
- Keep light-only; tray menu + notification balloons are intentionally native (disclosed scope).
- Prefer `DpiHelper.Scale(...)` for all fixed sizes; verify at the user's actual DPI (ScaleFactor cached from `g.DpiX / 96f`).
- After fixing: build (`dotnet build -c Release`), run tests (`dotnet test -c Release`, 339), screenshot each fixed surface again to confirm alignment, then republish + relaunch for the user.

## 7. Suggested skills for next session

- **`computer-use`** — the ONLY way to see the UI bug (screenshot + a11y tree of the live dialogs).
- **`diagnose`** — disciplined repro → instrument → fix → regression-test loop, ideal for the paste/duplication bug.
- **`tdd`** — the MergeStreamChunk revision + KeyboardHook suppression changes are test-first natural.
- Docs: `DESIGN.md`, `PRODUCT.md` describe the intended Stockroom-Tags design — check them before "fixing" something that may be intentional.
