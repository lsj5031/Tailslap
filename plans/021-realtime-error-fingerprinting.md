# Plan 021: Fingerprint realtime server error strings and surface DPAPI failures to the user

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**:
> `git diff --stat f3016ac..HEAD -- TailSlap/OpenAIRealtimeTranscriber.cs TailSlap/RealtimeTranscriber.cs TailSlap/RealtimeTranscriptionController.cs TailSlap/Dpapi.cs TailSlap.Tests/DpapiTests.cs`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P2
- **Effort**: S
- **Risk**: LOW
- **Depends on**: none (coordinate with 016/017 if executing concurrently — same files)
- **Category**: security
- **Planned at**: commit `f3016ac`, 2026-07-30

## Why this matters

The repo's logging policy (AGENTS.md "Security & Encryption") is: never log sensitive text, use SHA256 fingerprints. A prior hardening pass (plan 003, DONE) applied this to the HTTP clients — `TextRefiner` and `RemoteTranscriber` log error bodies as `len + sha256` only. But the realtime WebSocket paths still write **server-controlled error strings verbatim** into `%APPDATA%\TailSlap\logs\app.jsonl` and into balloon notifications. OpenAI-protocol error payloads can echo request context (auth errors commonly include a partially-redacted key identifier; session validation errors can echo the user's `RealtimeSessionPrompt`). Separately, `Dpapi.Protect`/`Unprotect` swallow all failures and return `""`, so a user whose DPAPI is broken (roaming profile, master-key corruption) silently loses their configured API key and sends unauthenticated requests with no signal that anything is wrong.

## Current state

### Verbatim server error strings

`TailSlap/OpenAIRealtimeTranscriber.cs:587-594` (inside the `error` event case of the receive loop):

```csharp
else if (errorObj.ValueKind == JsonValueKind.String)
{
    errorMessage = errorObj.GetString() ?? errorMessage;
}
...
Logger.Log($"OpenAIRealtimeTranscriber: Server error - {errorMessage}");
OnError?.Invoke(errorMessage);
```

`TailSlap/RealtimeTranscriber.cs:485-489`:

```csharp
if (!string.IsNullOrEmpty(msg.Error))
{
    Logger.Log($"RealtimeTranscriber: Server error - {msg.Error}");
    OnError?.Invoke(msg.Error);
}
```

`TailSlap/RealtimeTranscriptionController.cs` `HandleRealtimeError` (~908-915) — puts the raw string in a balloon:

```csharp
private async void HandleRealtimeError(string error)
{
    try
    {
        Logger.Log($"HandleRealtimeError: {error}");
        NotificationService.ShowError($"Real-time transcription error: {error}");
```

### The fingerprint convention to match

Same file, `RealtimeTranscriber.cs:492-493` (the non-error branch) shows the house style:

```csharp
Logger.Log(
    $"RealtimeTranscriber: Received text (final={msg.Final}, len={msg.Text?.Length ?? 0}, sha256={Hashing.Sha256Hex(msg.Text ?? string.Empty)})"
);
```

`Hashing.Sha256Hex(string)` is the existing helper — find it at `TailSlap/Hashing.cs` (verify with `grep -rn "static.*Sha256Hex" TailSlap/`).

### Silent DPAPI failure — `TailSlap/Dpapi.cs` (46 lines, static class)

```csharp
public static string Protect(string plaintext)
{
    try
    {
        var data = Encoding.UTF8.GetBytes(plaintext);
        var enc = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(enc);
    }
    catch (Exception ex)
    {
        try { Logger.Log($"DPAPI Protect failed: {ex.GetType().Name}"); } catch { }
        // Fail gracefully: caller will treat empty result as "no key"
        return string.Empty;
    }
}
```

`Unprotect` mirrors it. Callers: `LlmConfig.ApiKey` getter/setter (`ConfigService.cs` ~88-92) maps empty → null key; `HistoryService.EncryptString`/`DecryptString` (history entries get skipped/empty on failure — acceptable, already logged); `TranscriberConfig` likely has the same ApiKey pattern (verify with `grep -n "Dpapi" TailSlap/ConfigService.cs`).

### Notification and DI conventions

`NotificationService.ShowError(string)` static; DPAPI failure notifications must be rate-limited or one-shot per session to avoid balloon spam (Unprotect is called on every config read — with plan 018's cache, still multiple times per session).

## Commands you will need

| Purpose | Command | Expected on success |
|---------|---------|---------------------|
| Build | `dotnet build -c Release` | exit 0 |
| Focused tests | `dotnet test -c Release --filter FullyQualifiedName~Dpapi` | all pass |
| Full suite | `dotnet test -c Release` | all pass |
| Leak check | `rg -n "Server error - \{" TailSlap/` | no matches |

## Scope

**In scope**:

- `TailSlap/OpenAIRealtimeTranscriber.cs` (error-event logging only)
- `TailSlap/RealtimeTranscriber.cs` (error logging only)
- `TailSlap/RealtimeTranscriptionController.cs` (`HandleRealtimeError` only)
- `TailSlap/Dpapi.cs`
- `TailSlap.Tests/DpapiTests.cs`
- `plans/README.md` (status row)

**Out of scope**:

- Send-loop/buffer logic in either transcriber — plans 016/017.
- `TextRefiner`/`RemoteTranscriber` logging — already compliant.
- History encryption failure UX — current skip-and-log behavior stays.
- Changing DPAPI scope/entropy — decided design.

## Git workflow

- Branch: `advisor/021-realtime-error-fingerprinting`
- Commit message example: `Fix: fingerprint realtime server errors and notify on DPAPI key failures`
- Do NOT push or open a PR unless the operator instructed it.

## Steps

### Step 1: Fingerprint server error strings in both realtime transcribers

In `OpenAIRealtimeTranscriber.cs` (~592) replace:

```csharp
Logger.Log($"OpenAIRealtimeTranscriber: Server error - {errorMessage}");
OnError?.Invoke(errorMessage);
```

with:

```csharp
Logger.Log(
    $"OpenAIRealtimeTranscriber: Server error (len={errorMessage.Length}, sha256={Hashing.Sha256Hex(errorMessage)})"
);
OnError?.Invoke("The transcription server reported an error. Check server logs for details.");
```

Preserve any structured, non-sensitive fields if the payload parse exposes them (e.g. if an error `type`/`code` field is parsed nearby, include `type={code}` in the log and the user message — read the surrounding parse block ~560-595 first; a short enum-like code is safe to log verbatim, a free-text `message` is not).

Same change in `RealtimeTranscriber.cs` (~487-488) with the `RealtimeTranscriber:` prefix.

**Verify**: `rg -n "Server error - \{" TailSlap/` → no matches; `dotnet build -c Release` → exit 0.

### Step 2: Genericize the balloon in HandleRealtimeError

In `RealtimeTranscriptionController.cs` `HandleRealtimeError` (~908-915): the `error` argument now arrives pre-sanitized from Step 1 (both transcribers), but connection-failure paths also invoke `OnError` with exception messages (e.g. `OnError?.Invoke($"Connection failed: {ex.Message}")` in both `ConnectAsync` methods — those are LOCAL exception messages, not server-controlled; they may stay). Change only the logging to note length when the string is long:

```csharp
Logger.Log($"HandleRealtimeError: {error}");
NotificationService.ShowError($"Real-time transcription error: {error}");
```

stays acceptable AFTER Step 1 because no server-controlled free text reaches it anymore. Add a defensive truncation anyway: `var safe = error.Length > 200 ? error[..200] + "…" : error;` and use `safe` in both lines (protects against future callers).

**Verify**: `dotnet build -c Release` → exit 0.

### Step 3: Make DPAPI failures visible without changing the API shape

In `Dpapi.cs`:

1. Add a one-shot notification latch: `private static int _protectFailureNotified;` / `private static int _unprotectFailureNotified;`
2. In `Protect`'s catch, after the existing log, add:

```csharp
if (Interlocked.Exchange(ref _protectFailureNotified, 1) == 0)
{
    try
    {
        NotificationService.ShowError(
            "Failed to encrypt the API key (Windows DPAPI error). The key was NOT saved — re-enter it after resolving the Windows profile issue."
        );
    }
    catch { }
}
```

3. In `Unprotect`'s catch — same pattern with message: `"Failed to decrypt the stored API key (Windows DPAPI error). Requests will be sent without authentication until the key is re-entered."`
4. IMPORTANT exclusion: `HistoryService.DecryptString` calls `Unprotect` for every history entry when the history form opens — a corrupted single entry must NOT trigger the API-key balloon. Check how `HistoryService` calls Dpapi: it calls `Dpapi.Unprotect(ciphertext)` directly. Solution: add overloads `Protect(string plaintext, bool notifyOnFailure)` / `Unprotect(string base64, bool notifyOnFailure)` where the existing single-arg methods delegate with `notifyOnFailure: true`, and change `HistoryService.EncryptString`/`DecryptString` to pass `notifyOnFailure: false`. That requires touching `TailSlap/HistoryService.cs` — this two-line call-site change is authorized as a scope exception; change nothing else there.
5. Add `using System.Threading;` for `Interlocked`.

**Verify**: `dotnet build -c Release` → exit 0.

### Step 4: Tests

In `TailSlap.Tests/DpapiTests.cs` (5 existing tests):

- `Unprotect_InvalidBase64_ReturnsEmpty` — likely exists; if not, add: `Dpapi.Unprotect("not-base64!")` → `""` (no throw).
- `Unprotect_ValidBase64InvalidCiphertext_ReturnsEmpty` — `Convert.ToBase64String(new byte[] { 1, 2, 3 })` → `""`.
- Notification side effects cannot be asserted (static NotificationService, and balloons are UI) — the latch logic is trivial enough that compilation + the empty-return contract tests suffice. Do not attempt to mock the static.

**Verify**: `dotnet test -c Release` → all pass.

## Test plan

- Dpapi failure-path tests above (return-contract only).
- `rg -n "Server error - \{" TailSlap/` as a machine-checkable leak gate.
- Full suite green.
- Manual smoke (optional): point `transcriber.baseUrl` at a server that returns an error event (or stop glm-asr-docker mid-session) — balloon shows the generic message; `app.jsonl` contains `len=`/`sha256=` for the server error, not its text.

## Done criteria

- [ ] `dotnet build -c Release` exits 0; `dotnet test -c Release` exits 0
- [ ] `rg -n "Server error - \{" TailSlap/` → no matches
- [ ] Both realtime transcribers log server errors as `len + sha256` and invoke `OnError` with a generic message
- [ ] DPAPI Protect/Unprotect failures notify once per session; history decryption failures do NOT trigger the balloon
- [ ] No files outside the in-scope list are modified (`git status` — HistoryService two-line exception noted above)
- [ ] `plans/README.md` status row for 021 updated

## STOP conditions

- The OpenAI error-event parse block extracts fields materially differently than described (drift) — re-read and adapt; if the error event structure has no string message at all anymore, STOP.
- `NotificationService.ShowError` from `Dpapi` creates a circular dependency or thread-affinity crash in tests (Dpapi is static and used early in startup) — if any test or startup path crashes, wrap the notification in `try/catch` (already specified) and if it still fails, STOP and report.
- Plans 016/017 are mid-flight on the same files in another worktree — coordinate; do not merge conflicting edits blindly.

## Maintenance notes

- Reviewers: confirm the user-facing realtime error message is actionable ("check server logs") since the detail is now only in fingerprint form; support workflows can correlate via sha256 with server-side logs.
- Any future `OnError?.Invoke(...)` call site in transcriber receive loops must pass sanitized text — grep `OnError?.Invoke` during review of future PRs.
- The one-shot latch resets only on process restart — intentional (a broken DPAPI won't heal mid-session).
