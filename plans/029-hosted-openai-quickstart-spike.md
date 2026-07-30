# Plan 029: Verify hosted OpenAI transcription and add a no-Docker quickstart preset

> **Executor instructions**: This plan begins with a live compatibility spike.
> Do not implement the preset or publish a quickstart until hosted HTTP and
> realtime transcription both work against the current official API, or the
> protocol delta is understood and covered by tests. Never place an API key in
> source, commands, screenshots, logs, test fixtures, or plan documents. Update
> the status row in `plans/README.md` when complete unless a reviewer owns it.
>
> **Drift check (run first)**:
> `git diff --stat f3016ac..HEAD -- TailSlap/OpenAIRealtimeTranscriber.cs TailSlap/RemoteTranscriber.cs TailSlap/ConfigService.cs TailSlap/SettingsForm.cs TailSlap.Tests/OpenAIRealtimeTranscriberTests.cs README.md`
> Plans 016, 021, 023, and 027 can legitimately change these files. Reconcile
> their ownership, buffer, error-sanitization, runtime-version, and language
> behavior before proceeding.

## Status

- **Priority**: P2
- **Effort**: M (spike plus small implementation)
- **Risk**: MED (live paid API, evolving protocol, settings handling)
- **Depends on**: plans 016 and 021 recommended before live realtime testing
- **Category**: direction
- **Planned at**: commit `f3016ac`, 2026-07-30

## Why this matters

TailSlap documentation currently presents local `glm-asr-docker` as the normal
backend. The code already appears capable of hosted OpenAI:

- `https://api.openai.com/v1` maps to
  `wss://api.openai.com/v1/realtime?intent=transcription`;
- `OpenAIRealtimeTranscriber` sends a Bearer API key;
- default hosted fallback model is `gpt-4o-transcribe`;
- the same Base URL supports ordinary `/audio/transcriptions`.

But this path has not been proven live, the Realtime transcription protocol can
drift, and Settings provides no safe one-click way to choose the matching URL,
model, and provider. Verify the path, correct only evidenced protocol drift,
then add an explicit hosted preset and concise no-Docker README flow. Keep the
local backend as the new-install default.

## Official references to validate at execution time

Open the current official docs, not cached examples:

- [Realtime transcription guide](https://developers.openai.com/api/docs/guides/realtime-transcription)
- [Realtime client events reference](https://developers.openai.com/api/reference/resources/realtime/client-events)
- [Transcription guide](https://developers.openai.com/api/docs/guides/transcription)
- [GPT-4o Transcribe model](https://developers.openai.com/api/docs/models/gpt-4o-transcribe)

These links were current on 2026-07-30. Record the access date and relevant
event/schema version in the implementation notes because this API evolves.

## Current state

`OpenAIRealtimeTranscriber.ConnectAsync`:

```csharp
if (!string.IsNullOrEmpty(_config.ApiKey))
{
    _ws.Options.SetRequestHeader("Authorization", $"Bearer {_config.ApiKey}");
}

var model = string.IsNullOrEmpty(_config.Model) ? "gpt-4o-transcribe" : _config.Model;
var wsUrl = BuildWebSocketUrl();
```

`BuildWebSocketUrl` falls back to:

```text
wss://api.openai.com/v1/realtime?intent=transcription
```

and maps `https://api.openai.com/v1` to that URL.

`ConfigureSessionAsync` currently sends:

```csharp
new
{
    type = "transcription_session.update",
    input_audio_format = "pcm16",
    input_audio_transcription = new
    {
        model,
        prompt = _config.RealtimeSessionPrompt ?? string.Empty,
        language = _config.Language ?? string.Empty,
    },
    turn_detection = new
    {
        type = "server_vad",
        threshold = 0.5,
        prefix_padding_ms = 300,
        silence_duration_ms = 500,
    },
    input_audio_noise_reduction = new { type = "near_field" },
};
```

Audio is resampled from 16 kHz to 24 kHz PCM16 and sent as
`input_audio_buffer.append`; stopping enqueues commit/clear. Receive handling
accepts both official-looking
`conversation.item.input_audio_transcription.{delta,completed}` and local
`transcript.text.{delta,done}` events.

`TranscriberConfig` new-install defaults remain local:

```csharp
BaseUrl = "http://localhost:18000/v1";
Model = "glm-nano-2512";
RealtimeProvider = "openai"; // means OpenAI protocol, not necessarily hosted
```

Settings exposes Base URL, model, API key, and realtime provider separately.
README explicitly notes that `"openai"` means an OpenAI-compatible protocol and
currently has no hosted quickstart.

Existing `OpenAIRealtimeTranscriberTests` cover URL construction, provider
selection, clone behavior, and server-event parsing, but no session payload or
live connection.

## Scope

**In scope**:

- Live hosted HTTP and realtime compatibility spike
- `TailSlap/OpenAIRealtimeTranscriber.cs`, only evidenced protocol changes and
  extraction of payload/URL helpers for tests
- `TailSlap/ConfigService.cs`, only a hosted preset helper if chosen
- `TailSlap/SettingsForm.cs`, one hosted OpenAI preset control
- `TailSlap.Tests/OpenAIRealtimeTranscriberTests.cs`
- `TailSlap.Tests/RemoteTranscriberTests.cs` if plan 020/027 created it
- `README.md`, one no-Docker hosted OpenAI quickstart
- New `plans/029-decision.md`
- `plans/README.md`

**Out of scope**:

- Changing the local default backend
- LLM/refinement OpenAI presets
- Azure OpenAI or organization/project header support
- OAuth or browser-based key management
- Storing keys outside existing DPAPI config
- Replacing the realtime abstraction or custom provider
- General Settings redesign

## Git workflow

- Branch: `advisor/029-hosted-openai-quickstart`
- Commit example: `Add verified hosted OpenAI transcription preset and quickstart`
- Do not push or open a PR unless instructed.

## Steps

### Step 1: Produce a protocol compatibility checklist

Compare live code with the official references and record in
`plans/029-decision.md`:

- WebSocket URL and query parameters;
- authentication headers and whether any beta/version header is still required;
- session-update event name and exact nested schema;
- accepted input audio format and required sample rate;
- append, commit, and clear client events;
- delta/completed server events and ordering fields;
- supported realtime transcription models;
- HTTP multipart URL/fields/model;
- rate-limit, billing, and data-retention caveats worth surfacing to users.

Classify every item as `matches`, `compatible extension`, `drift`, or
`unverified`. Do not alter code based only on an old blog post or SDK example.

### Step 2: Add test seams before protocol changes

Extract pure/internal helpers only as needed:

- one canonical hosted/configured realtime URL builder (avoid preserving
  divergent copies in `ConfigService` and `OpenAIRealtimeTranscriber`);
- a session-update JSON builder that accepts model, language, and prompt.

Extend tests to assert the exact officially required shape without connecting:

1. hosted Base URL maps to the official WSS URL;
2. existing local OpenAI-protocol URL still maps correctly;
3. existing query parameters are preserved without duplicate
   `intent=transcription`;
4. session event type and audio format match current docs;
5. model, nonblank language, and prompt land in their documented locations;
6. blank optional values are represented or omitted exactly as docs require;
7. turn detection/noise reduction shape remains accepted;
8. server delta/completed events from current docs produce ordered updates.

Do not test or expose the Authorization header value.

### Step 3: Run the live hosted spike

Obtain explicit operator permission because this contacts a paid third-party
service and sends microphone audio. The operator enters a restricted OpenAI API
key through TailSlap Settings so existing DPAPI storage is used. Never ask them
to paste it into chat or a shell command.

Configure:

```text
Base URL: https://api.openai.com/v1
Model: gpt-4o-transcribe (or the current documented realtime-compatible transcription model)
Realtime Provider: openai
API Key: entered in Settings
```

Perform:

1. `"Test ASR Connection"` (HTTP path);
2. one short toggle transcription;
3. one 3-5 second realtime transcription;
4. language blank, then one supported language hint;
5. stop/commit and verify final text arrives once;
6. inspect `%APPDATA%\TailSlap\logs\app.jsonl` for HTTP status, event types,
   and errors without transcript or key leakage.

Record only status codes, event type names, timings, model, and pass/fail in
`029-decision.md`. Do not include spoken/transcribed text, key identifiers,
request bodies containing prompts, or account data.

If no key or network permission is available, mark the decision
`NEEDS LIVE EVIDENCE` and STOP before adding the preset/README claim.

### Step 4: Fix only demonstrated protocol drift

If Step 1 or 3 identifies drift, make the smallest compatible change in
`OpenAIRealtimeTranscriber`:

- preserve local glm-asr OpenAI-protocol compatibility where the server accepts
  both shapes;
- if official and local schemas conflict, add an explicit capability/provider
  distinction rather than guessing from hostname, then STOP for product review
  if this exceeds the current `openai`/`custom` model;
- retain plan 016's buffer ownership/channel fixes and plan 021's sanitized
  errors;
- update pure payload tests before rerunning live.

Do not add fallback retries that resend audio or commit events, because they can
duplicate transcripts and bill twice.

### Step 5: Add the hosted OpenAI preset

After live success, add a clearly labeled `"Use hosted OpenAI preset"` button
near the transcription Base URL/model fields. On click, after confirmation if
it would overwrite nondefault text:

- set Base URL to `https://api.openai.com/v1`;
- set model to the live-verified model;
- set realtime provider to `openai`;
- leave API-key text and stored key unchanged;
- leave hotkeys, VAD, auto-paste, auto-enhance, language, prompt, timeout, and
  microphone settings unchanged;
- show inline guidance: `"Enter an OpenAI API key, then Test ASR Connection.
  Usage is billed by OpenAI."`

Prefer an internal `TranscriberPresets.ApplyHostedOpenAi` helper or immutable
preset values that can be unit-tested. Do not infer hosted mode solely from
`RealtimeProvider == "openai"` because local glm-asr uses the same protocol.
Do not change new-install defaults.

Add tests proving the preset changes exactly the three intended fields and
preserves an existing key and all unrelated values. If SettingsForm tests are
impractical, test the pure preset helper.

### Step 6: Add the no-Docker quickstart

In README, add a short `"Hosted OpenAI (no Docker)"` subsection:

1. create/retrieve an API key from the official OpenAI platform;
2. open TailSlap Settings and click the hosted preset;
3. enter the key (stored locally with Windows DPAPI);
4. click Test ASR Connection;
5. save and use toggle, push-to-talk, or realtime hotkeys;
6. mention that audio is sent to OpenAI and usage may incur charges;
7. link to official pricing/data-control documentation without asserting terms
   that were not verified in Step 1.

Keep the local glm-asr path first/default and clarify that the realtime provider
label `"openai"` denotes protocol mode for both local and hosted backends.

### Step 7: Final verification

```powershell
dotnet test -c Release --filter "FullyQualifiedName~OpenAIRealtimeTranscriber|FullyQualifiedName~RemoteTranscriber"
dotnet build -c Release
dotnet test -c Release
git diff --check
```

Repeat one HTTP and one realtime live smoke after the final build. Record the
date and pass/fail, not sensitive content.

## Done criteria

- [ ] `plans/029-decision.md` maps current official protocol to live code
- [ ] Hosted HTTP and realtime paths both pass a live smoke with operator consent
- [ ] Any protocol drift is fixed and covered by pure tests
- [ ] Local OpenAI-protocol glm-asr URL and event compatibility remains tested
- [ ] Hosted preset changes only Base URL, model, and realtime provider
- [ ] Preset never displays, clears, logs, or replaces an existing API key
- [ ] New-install defaults remain local
- [ ] README has a verified no-Docker quickstart with privacy/billing notice
- [ ] Full build and tests pass
- [ ] Plan 029 status updated in `plans/README.md`

## STOP conditions

- No explicit permission/key/network is available for the paid live test.
- Official docs and live events disagree materially.
- Hosted and local protocol schemas conflict in a way that requires hostname
  sniffing or a third provider mode.
- Live logs contain API key material, transcript plaintext, prompts, or raw
  server error content. Stop, secure/delete the exposed local log as directed
  by the operator, and address the leak before continuing.
- The live model is not supported by both HTTP and realtime paths. Do not hide
  two model settings behind one preset without a product decision.

## Maintenance notes

Re-run the protocol checklist before changing the hosted preset's model or
claiming compatibility in a release. Keep the official-doc access date and live
smoke date in the decision document. Hosted quickstarts are promises backed by
live evidence, not just URL-shape tests.
