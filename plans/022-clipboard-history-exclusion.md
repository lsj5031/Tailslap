# Plan 022: Exclude TailSlap clipboard writes from Windows clipboard history and cloud clipboard

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**:
> `git diff --stat f3016ac..HEAD -- TailSlap/ClipboardService.cs TailSlap/ConfigService.cs TailSlap/SettingsForm.cs TailSlap.Tests/ClipboardServiceTests.cs`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition. Note: plans 013/018 also touch
> ClipboardService/ConfigService — their diffs are expected; land this after 013.

## Status

- **Priority**: P2
- **Effort**: M
- **Risk**: MED (clipboard format ordering can affect paste targets — verify broadly)
- **Depends on**: plans/013-paste-delivery-verification.md (same file; avoid conflicts)
- **Category**: security
- **Planned at**: commit `f3016ac`, 2026-07-30

## Why this matters

TailSlap's whole privacy story (DPAPI-encrypted history, fingerprint-only logs) is undermined by the OS clipboard: every refined text and every transcript is written to the clipboard for paste delivery, and on default Windows 11 configurations that content is captured by **Win+V clipboard history** and can sync via **cloud clipboard** to the user's Microsoft account and other devices — persisting sensitive dictation far outside the app's encrypted store. Windows provides opt-out clipboard formats exactly for this (`ExcludeClipboardContentFromMonitorProcessing`, `CanIncludeInClipboardHistory`, `CanUploadToCloudClipboard`); password managers set them routinely. TailSlap sets none of them anywhere (verified by repo-wide search).

## Current state

### The single clipboard write chokepoint — `TailSlap/ClipboardService.cs` `SetTextCoreAsync` (~1012-1046)

```csharp
private static async System.Threading.Tasks.Task<bool> SetTextCoreAsync(string text)
{
    int retries = 3;
    while (retries-- > 0)
    {
        try
        {
            Clipboard.SetText(text, TextDataFormat.UnicodeText);
            try { Logger.Log($"SetText ok, len={text?.Length ?? 0}"); } catch { }
            return true;
        }
        catch (Exception ex)
        {
            ...retry with 50ms delay, notify on final failure...
        }
    }
    return false;
}
```

All delivery-side writes funnel here: `SetTextAsync` (UI-thread marshaled) → `SetTextCoreAsync`; callers are `TextTyper`, `ClipboardHelper`, `RealtimeTranscriptionController`, `TranscriptionAutoEnhancer` etc. — every one goes through `IClipboardService.SetTextAsync`/`SetTextAndPasteAsync`.

Note: the CAPTURE side (Ctrl+C fallback in `CaptureSelectionOrClipboardAsync`, ~1490-1660) puts the user's own selected text on the clipboard via the target app's copy — that content is produced by the target app, not by TailSlap; excluding it is not possible from our side (the source app owns that clipboard write). Out of scope.

### The exclusion formats (Windows-documented contract)

A clipboard write opts out by placing these additional formats in the SAME clipboard update as the text:

- `"ExcludeClipboardContentFromMonitorProcessing"` — presence alone excludes from monitors (any data).
- `"CanIncludeInClipboardHistory"` — 4-byte DWORD `0` forbids history.
- `"CanUploadToCloudClipboard"` — 4-byte DWORD `0` forbids cloud sync.

With WinForms this is done via a `DataObject` carrying the text plus the custom formats, then `Clipboard.SetDataObject(obj, copy: true)`.

### Config + Settings conventions

- `AppConfig` (top of `ConfigService.cs`, ~10-33) holds top-level bools like `AutoPaste`, `UseClipboardFallback` with `Clone()` copying each. JSON is camelCase source-gen via `TailSlapJsonContext`.
- `SettingsForm.cs` builds checkboxes for such flags — find an existing top-level checkbox (search `AutoPaste` in SettingsForm) and mirror its wiring.

## Commands you will need

| Purpose | Command | Expected on success |
|---------|---------|---------------------|
| Build | `dotnet build -c Release` | exit 0 |
| Full suite | `dotnet test -c Release` | all pass |

## Scope

**In scope**:

- `TailSlap/ClipboardService.cs` (`SetTextCoreAsync` only)
- `TailSlap/ConfigService.cs` (one new `AppConfig` bool + `Clone`)
- `TailSlap/TailSlapJsonContext.cs` — only if the source-gen context needs regeneration hints (it shouldn't; `AppConfig` is already registered)
- `TailSlap/SettingsForm.cs` (one checkbox)
- `TailSlap/README.md` update is NOT in scope here (plan 026 does docs)
- `TailSlap.Tests/ClipboardServiceTests.cs`
- `plans/README.md` (status row)

**Out of scope**:

- The capture-side clipboard content (owned by the source app).
- Restoring previous clipboard contents after paste — separate product decision.
- Paste chain internals — plan 013.

## Git workflow

- Branch: `advisor/022-clipboard-history-exclusion`
- Commit message example: `Add: exclude delivered text from Win+V history and cloud clipboard (configurable)`
- Do NOT push or open a PR unless the operator instructed it.

## Steps

### Step 1: Add the config flag

In `AppConfig` (ConfigService.cs ~10-18) add:

```csharp
/// <summary>
/// When true (default), text TailSlap places on the clipboard is marked with the
/// Windows exclusion formats so it is not captured by Win+V clipboard history
/// or synced by cloud clipboard.
/// </summary>
public bool ExcludeFromClipboardHistory { get; set; } = true;
```

Add `ExcludeFromClipboardHistory = ExcludeFromClipboardHistory,` to `AppConfig.Clone()` (~20-33). Default TRUE — privacy-preserving by default; users who want transcripts in Win+V can opt out.

**Verify**: `dotnet build -c Release` → exit 0 (source-gen picks up the new property automatically; if a `TailSlapJsonContext` build error appears, the context needs no change for a plain bool — investigate before touching it).

### Step 2: Write exclusion formats in SetTextCoreAsync

`SetTextCoreAsync` is `static` and has no config access. Change the signature to `SetTextCoreAsync(string text, bool excludeFromHistory)` and thread the flag from the instance callers: `SetTextAsync` reads it once via the service's config access — check how `ClipboardService` gets config (grep `_config` / `IConfigService` in ClipboardService.cs; if the service has NO config dependency, add a `public bool ExcludeFromClipboardHistory { get; set; } = true;` property on `ClipboardService` that `MainForm` sets from config on startup and on `ConfigChanged` — choose whichever matches the existing structure; prefer the existing config dependency if present).

Replace the `Clipboard.SetText` call:

```csharp
if (excludeFromHistory)
{
    var data = new DataObject();
    data.SetData(DataFormats.UnicodeText, text);
    // Presence of this format excludes the update from clipboard monitors (incl. history):
    data.SetData("ExcludeClipboardContentFromMonitorProcessing", new byte[] { 1, 0, 0, 0 });
    // DWORD 0 = explicitly forbid history / cloud sync:
    data.SetData("CanIncludeInClipboardHistory", BitConverter.GetBytes(0));
    data.SetData("CanUploadToCloudClipboard", BitConverter.GetBytes(0));
    Clipboard.SetDataObject(data, copy: true);
}
else
{
    Clipboard.SetText(text, TextDataFormat.UnicodeText);
}
```

Notes for correctness:

- WinForms may wrap raw `byte[]` as serialized data with a managed header instead of raw bytes. If testing (Step 4 manual check) shows history still captures the text, switch the custom-format payloads to `new MemoryStream(BitConverter.GetBytes(0))` (WinForms writes MemoryStream contents as raw bytes), or fall back to raw Win32 (`OpenClipboard`/`RegisterClipboardFormat`/`SetClipboardData`) — the file already has extensive P/Invoke; prefer the managed route first and only escalate if verification fails.
- Keep the retry loop and logging exactly as-is around the new code.

**Verify**: `dotnet build -c Release` → exit 0.

### Step 3: Settings checkbox

In `SettingsForm.cs`, add a checkbox "Exclude TailSlap text from Windows clipboard history (Win+V)" bound to `ExcludeFromClipboardHistory`, wired identically to the existing `AutoPaste` checkbox (find it via `grep -n "AutoPaste" TailSlap/SettingsForm.cs` and copy its load/save pattern precisely, including layout placement in the same group).

**Verify**: `dotnet build -c Release` → exit 0.

### Step 4: Manual verification (mandatory — this is the actual done-signal)

On a Windows 11 machine with clipboard history enabled (Settings → System → Clipboard → history ON):

1. Run the app, perform a refinement or transcription so text is delivered via clipboard.
2. Press Win+V — the delivered text must NOT appear in history.
3. Paste with Ctrl+V into Notepad — must still work (the current-clipboard content is unaffected by the exclusion formats).
4. Toggle the setting off, repeat — text SHOULD appear in Win+V.
5. Paste into at least: Notepad, a browser text field, VS Code, Microsoft Word if available (Office has its own clipboard handling — the highest-risk target for format-ordering issues).

Record the results in the commit message. If step 2 fails with the `byte[]` payloads, apply the MemoryStream variant from Step 2's notes and re-verify.

### Step 5: Unit tests (limited by clipboard being a global resource)

In `ClipboardServiceTests.cs`, clipboard-touching tests are risky in parallel CI; add only a formats-construction test if you extracted a helper (recommended): extract `internal static DataObject BuildExcludedDataObject(string text)` and assert it `GetDataPresent("CanIncludeInClipboardHistory")` etc. without touching the real clipboard.

**Verify**: `dotnet test -c Release` → all pass.

## Test plan

- `BuildExcludedDataObject_ContainsExclusionFormats` (new, no real clipboard).
- Manual matrix from Step 4 (recorded in commit message).
- Full suite green.

## Done criteria

- [ ] `dotnet build -c Release` exits 0; `dotnet test -c Release` exits 0
- [ ] `rg -n "CanIncludeInClipboardHistory" TailSlap/` matches in ClipboardService.cs
- [ ] `AppConfig.ExcludeFromClipboardHistory` exists, defaults true, cloned, and settable in SettingsForm
- [ ] Manual Step 4 matrix executed and recorded (Win+V exclusion confirmed, paste still works in all tested apps)
- [ ] No files outside the in-scope list are modified (`git status`)
- [ ] `plans/README.md` status row for 022 updated

## STOP conditions

- Paste breaks in ANY Step 4 target app with exclusion enabled — STOP, report which app; do not ship with a paste regression (the feature is worthless if delivery breaks).
- WinForms `DataObject` cannot produce raw-byte custom formats even via MemoryStream AND the Win32 fallback would exceed ~80 lines — STOP and report; the raw-Win32 path needs its own review.
- `ClipboardService` has no clean way to receive config and the property-injection approach conflicts with how plan 013 restructured the file — coordinate/rebase, then proceed.

## Maintenance notes

- Reviewers: confirm the exclusion formats are set in the SAME `SetDataObject` call as the text (a second clipboard open would create a separate history entry of the bare text first).
- Plan 026's docs pass should mention the new setting in README's settings table — flag it there.
- If a future "restore previous clipboard after paste" feature lands, it must also apply exclusion formats when re-writing the restored content is TailSlap-originated text.
