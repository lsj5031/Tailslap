# Plan 027: Forward the configured ASR language hint in HTTP transcription requests

> **Executor instructions**: Follow each step and verification gate. STOP on
> any condition listed below rather than guessing provider behavior. Update the
> status row in `plans/README.md` when complete unless a reviewer owns it.
>
> **Drift check (run first)**:
> `git diff --stat f3016ac..HEAD -- TailSlap/RemoteTranscriber.cs TailSlap/ConfigService.cs TailSlap/SettingsForm.cs TailSlap.Tests/RemoteTranscriberTests.cs`
> Plan 020 is expected to add `RemoteTranscriberTests.cs`. Compare changed
> request-building code with the excerpts below before proceeding.

## Status

- **Priority**: P2
- **Effort**: S
- **Risk**: LOW
- **Depends on**: plan 020 (reuse its HTTP request-capture tests)
- **Category**: direction
- **Planned at**: commit `f3016ac`, 2026-07-30

## Why this matters

Settings expose one `TranscriberConfig.Language` field as an ASR language hint,
but only the OpenAI-protocol realtime WebSocket session consumes it. Toggle and
typeless modes use `RemoteTranscriber` HTTP multipart requests and silently
ignore the setting. A user choosing a language therefore gets mode-dependent
recognition quality from the same visible configuration.

OpenAI-compatible transcription endpoints conventionally accept a multipart
`language` field. Forward the nonblank configured value in connection-test,
ordinary, and streaming HTTP requests.

## Current state

`TailSlap/ConfigService.cs:154-160`:

```csharp
public string RealtimeProvider { get; set; } = "openai";

/// <summary>BCP-47 language hint for OpenAI-protocol realtime (empty = provider auto-detect).</summary>
public string Language { get; set; } = "";

/// <summary>Optional vocabulary / domain prompt for OpenAI-protocol realtime session.</summary>
public string RealtimeSessionPrompt { get; set; } = "";
```

`Language` is cloned and edited in Settings. The summary incorrectly describes
it as realtime-only, although the UI labels it as an ASR language/session hint.

`RemoteTranscriber` constructs multipart bodies independently in three methods:

- `TestConnectionAsync` at lines 80-86: file + optional model;
- `TranscribeAudioAsync` at lines 213-220: file + optional model;
- `TranscribeStreamingAsync` at lines 396-405: file + optional model +
  `stream=true`.

None adds `language`.

Plan 020 creates `TailSlap.Tests/RemoteTranscriberTests.cs` with a
`StubHandler` that captures multipart requests. Extend that test seam rather
than adding another HTTP fake.

## Scope

**In scope**:

- `TailSlap/RemoteTranscriber.cs`
- `TailSlap/ConfigService.cs` (XML summary only)
- `TailSlap/SettingsForm.cs` (label/help text only if it says realtime-only)
- `TailSlap.Tests/RemoteTranscriberTests.cs`
- `plans/README.md`

**Out of scope**:

- Realtime WebSocket session payload, which already forwards `Language`
- Adding a separate HTTP language setting
- Forwarding `RealtimeSessionPrompt` as HTTP `prompt` (provider semantics and
  privacy require a separate decision)
- Changing language validation or building a language picker
- Provider-specific fallback/retry behavior

## Git workflow

- Branch: `advisor/027-language-hint-http`
- Commit example: `Add ASR language hint to HTTP transcription requests`
- Do not push or open a PR unless instructed.

## Steps

### Step 1: Add one common multipart-field helper

In `RemoteTranscriber`, add a private helper used by all three request paths:

```csharp
private void AddCommonFormFields(MultipartFormDataContent formData)
{
    if (!string.IsNullOrWhiteSpace(_config.Model))
    {
        formData.Add(new StringContent(_config.Model.Trim()), "model");
    }

    if (!string.IsNullOrWhiteSpace(_config.Language))
    {
        formData.Add(new StringContent(_config.Language.Trim()), "language");
    }
}
```

Replace each duplicated optional-model block with the helper. Keep the
streaming-only `stream=true` field after the helper. Preserve existing logging,
but do not add the language value to logs.

If trimming the model would be a behavior change relative to existing
validation, omit `.Trim()` for model and trim only language. Do not broaden this
task into config normalization.

**Verify**:

```powershell
dotnet build -c Release
rg -n 'StringContent\(_config\.Language|AddCommonFormFields' TailSlap/RemoteTranscriber.cs
```

Expected: one language-add implementation and three helper calls.

### Step 2: Correct the configuration description

Change the XML summary on `TranscriberConfig.Language` to:

```csharp
/// <summary>Optional BCP-47 ASR language hint for HTTP and OpenAI-protocol realtime transcription (empty = provider auto-detect).</summary>
```

Inspect the Settings label/tooltips. If any says the field applies only to
realtime, change it to `"ASR language hint (optional; blank = auto-detect)"`.
Do not redesign the form.

**Verify**: `dotnet build -c Release`.

### Step 3: Extend plan 020's request-shape tests

In `RemoteTranscriberTests.cs`, add:

1. `TranscribeAudioAsync_LanguageConfigured_AddsLanguagePart`
2. `TranscribeAudioAsync_LanguageBlank_OmitsLanguagePart`
3. `TranscribeStreamingAsync_LanguageConfigured_AddsLanguagePart`
4. `TestConnectionAsync_LanguageConfigured_AddsLanguagePart`

Capture and read multipart content inside the handler before the request is
disposed. Prefer parsing multipart headers/content over a fragile boundary
string assertion if the existing test helper supports it. At minimum, assert
the body contains `name="language"` and the exact configured test value.

Use a harmless value such as `en` or `en-US`. Do not touch real network
services, credentials, clipboard, or `%APPDATA%`.

Also preserve plan 020's model and stream-field assertions, proving the helper
did not remove them.

### Step 4: Full verification

```powershell
dotnet test -c Release --filter FullyQualifiedName~RemoteTranscriber
dotnet build -c Release
dotnet test -c Release
```

All commands must exit 0.

## Done criteria

- [ ] All three HTTP multipart request paths add nonblank `language`
- [ ] Blank/whitespace language omits the field
- [ ] Existing model and streaming fields remain unchanged
- [ ] Config and Settings descriptions no longer imply realtime-only behavior
- [ ] Four language request-shape tests pass
- [ ] Full build and test suite pass
- [ ] Only in-scope files changed
- [ ] Plan 027 status updated in `plans/README.md`

## STOP conditions

- Plan 020 has not landed or its RemoteTranscriber tests are failing.
- The target HTTP backend used by this repository rejects unknown multipart
  fields despite claiming OpenAI compatibility. Capture the response and report
  it; do not add provider-name branching without a design decision.
- `Language` has been split into provider-specific settings since this plan was
  written. Follow the live model and revise this plan first.

## Maintenance notes

Any future common HTTP transcription field should be added through the shared
form-field helper and covered in all three request paths. Keep
`RealtimeSessionPrompt` separate until HTTP prompt semantics and sensitive-data
handling are explicitly reviewed.
