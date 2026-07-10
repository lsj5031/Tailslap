# Plan 003: Stop embedding full LLM error bodies in exceptions, logs, and notifications

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**:
> `git diff --stat 6d0b6ca..HEAD -- TailSlap/TextRefiner.cs TailSlap/RefinementController.cs TailSlap/NotificationService.cs`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P1
- **Effort**: S
- **Risk**: LOW
- **Depends on**: none (pair style with plan 002 fingerprints)
- **Category**: security
- **Planned at**: commit `6d0b6ca`, 2026-07-09

## Why this matters

On non-success LLM HTTP responses, `TextRefiner` already computes a **user-friendly** error via `GetUserFriendlyError`, shows it in a notification, then throws `Exception` whose message embeds the **entire** `errorBody`. Retry logging and `RefinementController` re-log / re-show `ex.Message`, so provider payloads (often echoed prompts, partial completions, or auth detail) land in `%APPDATA%\TailSlap\logs\` and sometimes in tray balloons. Success paths already fingerprint outputs with `Hashing.Sha256Hex`. Error paths must match that hygiene.

## Current state

### `TailSlap/TextRefiner.cs` (~167–172) — primary leak

```csharp
var errorBody = await resp
    .Content.ReadAsStringAsync(timeoutCts.Token)
    .ConfigureAwait(false);
var userFriendlyError = GetUserFriendlyError(resp.StatusCode, errorBody);
NotificationService.ShowError($"LLM request failed: {userFriendlyError}");
throw new Exception($"LLM error {resp.StatusCode}: {errorBody}");
```

### Retry path logs full exception message (~228–233)

```csharp
catch (Exception ex) when (attempts > 0)
{
    try
    {
        Logger.Log("LLM exception: " + ex.Message + "; retrying in 1s");
    }
    catch { }
    DiagnosticsEventSource.Log.RefinementRetry(
        2 - attempts,
        ex.Message ?? "Unknown error",
        1000
    );
```

### Success fingerprint pattern to mirror (~211–215)

```csharp
Logger.Log(
    $"LLM output fingerprint: len={result.Length}, sha256={Hashing.Sha256Hex(result)}"
);
```

### `TailSlap/RefinementController.cs` (~167–171)

```csharp
catch (Exception ex)
{
    NotificationService.ShowError("Refinement failed: " + ex.Message);
    Logger.Log("Error: " + ex.Message);
    return false;
}
```

### `NotificationService` behavior

Balloon messages are also written to the log (`NotificationService` logs the message text). Therefore **notification text must stay user-friendly**, never raw provider bodies.

**Convention**: AGENTS.md — log fingerprints, not sensitive text; show user-friendly notifications.

## Commands you will need

| Purpose | Command | Expected on success |
|---------|---------|---------------------|
| Build | `dotnet build -c Release` | exit 0 |
| Test | `dotnet test -c Release` | exit 0 |
| Filter tests | `dotnet test -c Release --filter FullyQualifiedName~TextRefiner` | pass |

## Scope

**In scope**:

- `TailSlap/TextRefiner.cs`
- `TailSlap/RefinementController.cs`
- `TailSlap.Tests/TextRefinerTests.cs` (update/add if assertions depend on exception message content)
- `plans/README.md` status

**Out of scope**:

- `RemoteTranscriber.cs` (plan 002)
- Changing `GetUserFriendlyError` heuristics beyond ensuring they do not return full bodies
- Redesigning DiagnosticsEventSource event schemas (sanitize string args only)
- Unhandled exception handlers in `Program.cs` (related deferred finding)

## Git workflow

- Branch: `advisor/003-llm-error-body-hygiene`
- Commit message example: `Avoid logging full LLM error bodies`
- Do NOT push/PR unless asked.

## Steps

### Step 1: Throw and log without raw `errorBody` in `TextRefiner`

At the non-success HTTP branch:

1. Keep reading `errorBody` only to feed `GetUserFriendlyError` (and optional fingerprint log).
2. Log something like:

```csharp
Logger.Log(
    $"LLM error response: status={(int)resp.StatusCode}, {Fingerprint(errorBody)}"
);
```

where `Fingerprint` is `len=` + `Hashing.Sha256Hex` (private local helper OK).

3. Keep: `NotificationService.ShowError($"LLM request failed: {userFriendlyError}");`
4. Throw **without** embedding `errorBody`:

```csharp
throw new InvalidOperationException(
    $"LLM error {resp.StatusCode}: {userFriendlyError}"
);
```

Prefer `InvalidOperationException` (or a small dedicated type) over bare `Exception` if tests allow; matching existing `InvalidOperationException` for short-output is fine. Do **not** put raw body in the exception message.

**Verify**:

```powershell
Select-String -Path TailSlap\TextRefiner.cs -Pattern 'errorBody\}|errorBody\)'
```

Uses of `errorBody` should be limited to `GetUserFriendlyError`, fingerprint logging, and similar non-message sinks — **not** string interpolation into `throw` or user-visible strings beyond friendly mapping.

### Step 2: Sanitize retry logging

In the `catch (Exception ex) when (attempts > 0)` block:

- Log: exception **type** + a safe summary (e.g. `ex.GetType().Name` and, if message is already user-friendly, OK; never re-introduce bodies).
- For `DiagnosticsEventSource.Log.RefinementRetry`, pass a short safe reason (`ex.GetType().Name` or status code), not a body-sized string.

If `ex.Message` might still be large from **other** code paths, prefer:

```csharp
Logger.Log($"LLM exception: type={ex.GetType().Name}; retrying in 1s");
```

**Verify**: no `Logger.Log("LLM exception: " + ex.Message` pattern remains.

### Step 3: Make `RefinementController` notifications safe

In `RefinementController` catch:

- Show a **stable** user message for failures, e.g. `"Refinement failed. Check Settings and logs for details."` **or** use `ex.Message` only if you are certain all `TextRefiner` throws are now friendly (step 1).
- Log: `type={ex.GetType().Name}` plus friendly message / fingerprint — not unbounded content.

Recommended pattern:

```csharp
catch (Exception ex)
{
    var msg = ex.Message; // now friendly after step 1
    // Guard length defensively:
    if (msg.Length > 200)
        msg = msg.Substring(0, 200) + "…";
    NotificationService.ShowError("Refinement failed: " + msg);
    Logger.Log($"Refinement error: type={ex.GetType().Name}, msgLen={ex.Message?.Length ?? 0}");
    return false;
}
```

Even better: always show a fixed short string and log only type + fingerprint of message.

**Verify**: controller does not concatenate unbounded `ex.ToString()`.

### Step 4: Update tests if needed

Open `TailSlap.Tests/TextRefinerTests.cs`. If any test asserts exception message contains raw JSON/body fragments, update to assert status-friendly text or exception type only.

```powershell
dotnet test -c Release --filter FullyQualifiedName~TextRefiner
dotnet test -c Release --filter FullyQualifiedName~RefinementController
dotnet test -c Release
```

**Verify**: all exit 0.

## Test plan

- Existing `TextRefinerTests` / `RefinementControllerTests` remain green.
- Optional new test: mock HTTP 500 with a body containing a unique marker string; assert thrown exception message does **not** contain that marker; assert it does contain status or friendly fragment. Model after existing `TextRefinerTests` HTTP mocking if present; if tests lack HttpMessageHandler fakes and adding them is large, skip optional test (logging-only change) and note in PR.

## Done criteria

- [ ] `throw` paths in `TextRefiner` for HTTP errors do not include raw `errorBody`
- [ ] Retry logs do not append full `ex.Message` when it could be a body
- [ ] `RefinementController` user notification stays short / friendly
- [ ] `dotnet test -c Release` exits 0
- [ ] No out-of-scope files modified
- [ ] `plans/README.md` status for 003 set to `DONE`

## STOP conditions

- `GetUserFriendlyError` itself returns large raw bodies for some status codes — then fix that method to cap/sanitize (still in scope as part of TextRefiner) or STOP if behavior is intentionally dumping body for debugging flags you cannot find.
- Tests require matching exact historical exception strings that include bodies — update tests; do not keep body-in-message for compatibility.
- Drift moved LLM client to another type — re-locate equivalent sites or STOP.

## Maintenance notes

- Reviewers: search `TextRefiner.cs` for `ReadAsStringAsync` error paths and ensure messages stay friendly.
- Pair with plan 002 for transcription; same fingerprint vocabulary (`len`, `sha256`).
- Deferred: `Program.cs` unhandled exception logging still stringifies full exceptions — separate finding.
