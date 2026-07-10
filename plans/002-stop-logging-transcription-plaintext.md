# Plan 002: Stop logging transcription text and raw API response bodies

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**:
> `git diff --stat 6d0b6ca..HEAD -- TailSlap/RemoteTranscriber.cs TailSlap/Hashing.cs`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P1
- **Effort**: S
- **Risk**: LOW
- **Depends on**: none
- **Category**: security
- **Planned at**: commit `6d0b6ca`, 2026-07-09

## Why this matters

TailSlap documents that sensitive text is not logged — only SHA256 fingerprints. The streaming success path already fingerprints chunks, but several `RemoteTranscriber` paths still write transcription text and raw HTTP/JSON bodies into `%APPDATA%\TailSlap\logs\` (unencrypted, share-readable). That leaves spoken content and provider payloads on disk after normal use. Fix by matching the existing fingerprint pattern everywhere content currently leaks.

## Current state

- `TailSlap/Hashing.cs` — `Hashing.Sha256Hex(string)` helper (use this; do not invent another hasher).
- `TailSlap/RemoteTranscriber.cs` — HTTP multipart + SSE transcription client.

**Good pattern already in the same file** (stream success path ~519–521):

```csharp
Logger.Log(
    $"Streaming chunk: len={text.Length}, sha256={Hashing.Sha256Hex(text)}"
);
```

**Leaking sites (must fix)**:

1. Success non-stream extract (~269–271):

```csharp
Logger.Log(
    $"Extracted text from response: {text.Substring(0, Math.Min(100, text.Length))}"
);
```

2. Parse failure attaches up to 500 chars of raw body to `TranscriberException` (~281–283):

```csharp
responseText: responseText.Length > 500
    ? responseText.Substring(0, 500)
    : responseText
```

3. Streaming error path logs and stores full/truncated body (~436–445):

```csharp
Logger.Log($"Streaming error response: {errorText}");
// ...
responseText: errorText.Length > 500 ? errorText.Substring(0, 500) : errorText
```

4. Unknown JSON structure logs up to 500 chars of `response.ToString()` (~770–772) and throws with full `response.ToString()` as `responseText` (~776–779).

5. Streaming error chunk path (~504–512) can put error `text` into exception message and `responseText` when line starts with `[Error:`.

Note: HTTP error path at ~250–260 already uses `responseFingerprint` for `responseText` in one branch — prefer that style everywhere.

`TranscriberException` exposes `ResponseText` (`RemoteTranscriber.cs` ~22–39). Callers/logs may surface it. Prefer storing **fingerprints or empty** in `responseText`, never raw body or transcript text.

**AGENTS.md convention**: “Never log sensitive text directly; use SHA256 fingerprints for debugging.”

## Commands you will need

| Purpose | Command | Expected on success |
|---------|---------|---------------------|
| Build | `dotnet build -c Release` | exit 0 |
| Test | `dotnet test -c Release` | exit 0 |
| Grep for leaks | see Done criteria | no plaintext log of extract/body |

## Scope

**In scope**:

- `TailSlap/RemoteTranscriber.cs`
- `TailSlap.Tests/*` only if you add a focused unit test (optional; preferred if you extract pure helpers)
- `plans/README.md` status row

**Out of scope**:

- `TextRefiner.cs` / refinement exception bodies — that is **plan 003**
- Changing log file location, encryption of logs, or `Logger` implementation
- Removing `TranscriberException.ResponseText` property itself (may still hold fingerprint)
- OpenAI / realtime WebSocket clients (different finding)

## Git workflow

- Branch: `advisor/002-transcription-log-hygiene`
- Commit message example: `Stop logging transcription plaintext in RemoteTranscriber`
- Do NOT push/PR unless asked.

## Steps

### Step 1: Replace success-path extract log with fingerprint

In `RemoteTranscriber.cs`, change the “Extracted text from response” log to the same shape as streaming:

```csharp
Logger.Log(
    $"Extracted text from response: len={text.Length}, sha256={Hashing.Sha256Hex(text)}"
);
```

**Verify**: search file for `Extracted text from response` — only fingerprint form remains.

### Step 2: Never put raw response bodies into logs or `TranscriberException.ResponseText`

For every throw/log site listed in Current state:

- Log: `len={n}, sha256={Hashing.Sha256Hex(body)}` and HTTP status when available.
- `responseText:` argument: pass fingerprint string (or short status token like `"fp:" + hash`), **not** substrings of the body.
- For `JsonException` parse failures: log exception type/message only; do not attach body substring.
- For unrecognized structure: log structure kind / property names if useful, **not** `response.ToString()` content. Fingerprint the serialized form if needed.
- For `[Error:` streaming lines: log fingerprint of the error line; exception message can stay user-safe (“Remote streaming error”) without echoing provider text if it may contain content.

Prefer a tiny private helper to avoid duplication, e.g.:

```csharp
private static string FingerprintPayload(string? s) =>
    string.IsNullOrEmpty(s)
        ? ""
        : $"len={s.Length}, sha256={Hashing.Sha256Hex(s)}";
```

Keep it in `RemoteTranscriber` (or reuse inline) — do not expand scope into new files unless necessary.

**Verify** (from repo root, PowerShell):

```powershell
# Should return NO matches for these leak patterns:
Select-String -Path TailSlap\RemoteTranscriber.cs -Pattern 'Extracted text from response: \{|Streaming error response: \{errorText\}|responseText: responseText|responseText: errorText|response\.ToString\(\)'
```

Adjust patterns if code reformatted; intent: no log interpolates raw extract substring; no `responseText:` assigned from raw body variables.

Also run:

```powershell
Select-String -Path TailSlap\RemoteTranscriber.cs -Pattern 'Substring\(0,\s*Math\.Min\(100|Substring\(0,\s*500\)'
```

**Expected**: no remaining body-truncation-for-logging patterns that feed Logger or `responseText` (other Substring uses for parsing are OK — report if ambiguous).

### Step 3: Build and test

```powershell
dotnet test -c Release
```

**Verify**: exit 0.

Optional (nice-to-have): if `ExtractTextFromResponse` is testable without HTTP, add a unit test that a known JSON fixture extracts text without throwing — only if low cost. Do not add network tests.

## Test plan

- No mandatory new tests (behavior is logging-only).
- Regression: full suite stays green.
- Manual spot-check if you run the app: after a transcription, `app.jsonl` must not contain spoken words from the session (only `len=` / `sha256=`).

## Done criteria

- [ ] Success extract log uses `len` + `Hashing.Sha256Hex` only
- [ ] No `Logger.Log` in `RemoteTranscriber.cs` writes raw transcript text or raw error JSON bodies
- [ ] `TranscriberException` constructed in this file does not receive raw response body substrings in `responseText`
- [ ] `dotnet test -c Release` exits 0
- [ ] No out-of-scope files modified
- [ ] `plans/README.md` status for 002 set to `DONE`

## STOP conditions

- Excerpts no longer match and leak sites moved to another file without a clear map.
- A caller **requires** raw `ResponseText` for user-facing UI and removing it breaks UX — then store fingerprint in logs only but keep UI path using a **user-friendly** message without full body; report the call site.
- You discover API keys inside URLs being logged elsewhere — note it, do not expand this plan into full URL redaction unless trivial in the same log lines you already touch.

## Maintenance notes

- Reviewers: grepping `Logger.Log` in `RemoteTranscriber.cs` for interpolated user/API content is the PR checklist.
- Plan 003 does the same class of fix for `TextRefiner` / refinement exceptions.
- Any new transcription client should copy the streaming-chunk fingerprint pattern, not the old substring pattern.
