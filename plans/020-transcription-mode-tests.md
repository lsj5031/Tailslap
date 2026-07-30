# Plan 020: Real tests for TranscriptionController, RemoteTranscriber, and TextRefiner's HTTP behavior

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**:
> `git diff --stat f3016ac..HEAD -- TailSlap/TranscriptionController.cs TailSlap/RemoteTranscriber.cs TailSlap/TextRefiner.cs TailSlap.Tests/TranscriptionControllerTests.cs TailSlap.Tests/RemoteTranscriberTests.cs TailSlap.Tests/TextRefinerTests.cs`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P2
- **Effort**: M
- **Risk**: LOW (tests only; at most `internal` visibility tweaks in production code)
- **Depends on**: none (015 recommended first if it lands nearby — it may adjust TypelessController timing assertions, not these files)
- **Category**: tests
- **Planned at**: commit `f3016ac`, 2026-07-30

## Why this matters

Two of the four hotkey modes (toggle transcription and push-to-talk) route all audio through `RemoteTranscriber` (815 lines: multipart POST, SSE stream parsing, retry/timeout/error mapping) — which has **zero tests**. `TranscriptionController`'s public toggle state machine (`TriggerTranscribeAsync` press/press-again) has no direct coverage: its 3 existing tests reach private methods via reflection, which is brittle and skips the state machine entirely. `TextRefiner`'s 6 tests cover only constructor null checks while its retry logic (2 attempts, 1s backoff), response parsing, and error mapping go unverified. The "~245 tests passing" figure materially overstates protection on these paths; regressions here surface only as field bugs.

## Current state

### `TailSlap/RemoteTranscriber.cs` (815 lines) — untested

```csharp
public sealed class RemoteTranscriber : IRemoteTranscriber
{
    private readonly TranscriberConfig _config;
    private readonly IHttpClientFactory _httpClientFactory;

    public RemoteTranscriber(TranscriberConfig config, IHttpClientFactory httpClientFactory)
```

Interface (`TailSlap/IRemoteTranscriber.cs`):

```csharp
public interface IRemoteTranscriber
{
    Task<string> TestConnectionAsync(CancellationToken ct = default);
    Task<string> TranscribeAudioAsync(string audioFilePath, CancellationToken ct = default);
    IAsyncEnumerable<string> TranscribeStreamingAsync(string audioFilePath, CancellationToken ct = default);
}
```

SSE parsing lives in the streaming path (~449-530): detects `text/event-stream` content type, parses `data: <text>` lines (with a `data:`-without-space fallback), terminates on `data: [DONE]`. There is also a typed error model at the top of the file (`TranscriberErrorType`, `NetworkTimeout`/`ConnectionFailed` retryable classification, ~40-47).

### `TailSlap/TextRefiner.cs` (~382 lines)

```csharp
public TextRefiner(LlmConfig cfg, IHttpClientFactory httpClientFactory)
```

Retry/response-parsing/error-mapping around lines 160-249; logs error bodies as `len + sha256` fingerprints only (keep it that way in any assertions). `ShortOutputErrorMessage` const at line ~22 signals the truncated-output guard.

### `TailSlap.Tests/TranscriptionControllerTests.cs` (3 tests) — reflection-based

The file defines a good reusable `TestableStreamingTextTyper : TextTyper` (records `TypedTexts`, no-ops keystrokes) and a `CreateController(...)` helper that constructs the real controller with all-Moq dependencies:

```csharp
return new TranscriptionController(
    new Mock<IConfigService>().Object,
    new ClipboardHelper(clipboardService.Object),
    new Mock<IRemoteTranscriberFactory>().Object,
    new Mock<IAudioRecorderFactory>().Object,
    new Mock<IHistoryService>().Object,
    new Mock<ITextRefinerFactory>().Object,
    textTyper
);
```

But the actual tests invoke `TranscribeRecordedAudioStreamingAsync` and `ApplyFinalTextAsync` via `GetMethod(..., BindingFlags.NonPublic)` — never `TriggerTranscribeAsync`.

### `TailSlap/TranscriptionController.cs` public surface (lines 30-120)

- Ctor takes: `IConfigService, ClipboardHelper, IRemoteTranscriberFactory, IAudioRecorderFactory, IHistoryService, ITextRefinerFactory, TextTyper`.
- `TriggerTranscribeAsync()`: reads `_config.CreateValidatedCopy()`; if `!cfg.Transcriber.Enabled` → warn + return false; if `IsRecording` → `StopRecording()` (cancels `_recordingCts`) + return false; if `_isTranscribing` → warn + return false; else `_isTranscribing = true`, `OnStarted`, runs `TranscribeSelectionAsync(cfg)`, finally `_isTranscribing = false` + `OnCompleted`.
- Events: `OnStarted`, `OnProcessingStarted`, `OnCompleted`, `OnRmsLevel`.

### Reference pattern for controller tests

`TailSlap.Tests/TypelessControllerTests.cs` (36 tests) — mocks `IAudioRecorderFactory`/`IRemoteTranscriberFactory` to drive full public flows. Model new TranscriptionController tests on it: look at how it fakes the recorder factory to return a controllable recorder and how it asserts event sequences.

### HttpClientFactory faking pattern (new to this repo's tests)

No existing test fakes HTTP. Use this shape (no new NuGet packages — Moq is already referenced):

```csharp
private sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
    public List<HttpRequestMessage> Requests { get; } = new();
    public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(_responder(request));
    }
}

private static IHttpClientFactory CreateFactory(StubHandler handler)
{
    var mock = new Mock<IHttpClientFactory>();
    mock.Setup(f => f.CreateClient(It.IsAny<string>()))
        .Returns(() => new HttpClient(handler, disposeHandler: false));
    return mock.Object;
}
```

Caution: request CONTENT must be captured eagerly if asserted after disposal — read `await request.Content.ReadAsStringAsync()` inside the responder if needed.

## Commands you will need

| Purpose | Command | Expected on success |
|---------|---------|---------------------|
| Build | `dotnet build -c Release` | exit 0 |
| Focused | `dotnet test -c Release --filter "FullyQualifiedName~RemoteTranscriber|FullyQualifiedName~TranscriptionController|FullyQualifiedName~TextRefiner"` | all pass |
| Full suite | `dotnet test -c Release` | all pass |

## Scope

**In scope**:

- `TailSlap.Tests/RemoteTranscriberTests.cs` (create)
- `TailSlap.Tests/TranscriptionControllerTests.cs` (extend; delete reflection helpers only if their scenarios are re-covered publicly)
- `TailSlap.Tests/TextRefinerTests.cs` (extend)
- `TailSlap/TranscriptionController.cs` — add the same internal recording
  delegate seam already used by `TypelessController`; no behavioral changes
- `TailSlap/RemoteTranscriber.cs`, `TailSlap/TextRefiner.cs` — ONLY `private` → `internal` visibility changes if a parse helper genuinely cannot be reached through the public API (prefer public-API testing; expect zero production changes)
- `plans/README.md` (status row)

**Out of scope**:

- Behavior changes of any kind in production code.
- `RealtimeTranscriber` (legacy) test suite — deliberately rejected for now (see plans/README.md).
- `TypelessControllerTests` — already substantial.
- Sending a `language` field — plan 027 (but see Test plan: 027 will extend the request-shape test added here).

## Git workflow

- Branch: `advisor/020-transcription-mode-tests`
- Commit message example: `Test: cover RemoteTranscriber HTTP/SSE, TranscriptionController toggle, TextRefiner retry`
- Do NOT push or open a PR unless the operator instructed it.

## Steps

### Step 1: RemoteTranscriberTests — request shape and happy paths

Create `TailSlap.Tests/RemoteTranscriberTests.cs` with the StubHandler pattern above. Write a temp WAV fixture helper (`Path.GetTempFileName()` + a few bytes; the transcriber does not validate WAV headers client-side — confirm by reading `TranscribeAudioAsync`'s file handling first; if it does validate, write a minimal valid RIFF header: `RIFF....WAVEfmt ` + data chunk).

Tests:

1. `TranscribeAudioAsync_SendsMultipartWithFileAndModel` — responder captures content; assert content type is `multipart/form-data`, the captured body contains the model name and a file part; return `200 OK` with the JSON the parser expects (read `TranscribeAudioAsync`'s response parsing first to construct it — likely `{"text":"hello"}` OpenAI-compatible; confirm in code) → returned string equals `"hello"`.
2. `TranscribeAudioAsync_SetsBearerHeader_WhenApiKeyConfigured` — config with an API key via `TranscriberConfig` (check how the key is stored — if DPAPI-backed like `LlmConfig.ApiKey`, set the plaintext property so `Dpapi.Protect` runs); assert `Authorization: Bearer` header present. And the inverse: no key → no header.
3. `TranscribeAudioAsync_ErrorStatus_ThrowsTypedError` — responder returns `500`; assert the thrown exception type/`TranscriberErrorType` mapping matches the code's model (read the error-mapping region ~200-260 first and assert what it actually does).
4. `TranscribeAudioAsync_MissingFile_Throws` — nonexistent path → the documented error (read code for the exact behavior; assert that).

**Verify**: `dotnet test -c Release --filter FullyQualifiedName~RemoteTranscriber` → 4+ pass.

### Step 2: RemoteTranscriberTests — SSE streaming parse

Responder returns `200` with `Content-Type: text/event-stream` and a body such as:

```
data: hello

data:  world

data: [DONE]
```

Tests:

5. `TranscribeStreamingAsync_ParsesDataLines` — collect the `IAsyncEnumerable<string>` → chunks `["hello", " world"]` (adjust to actual per-chunk semantics after reading ~470-530: chunks may be cumulative or delta — assert what the code does and name the test accordingly).
6. `TranscribeStreamingAsync_DataWithoutSpace_Parsed` — `data:hello` variant handled (code path at ~523-524).
7. `TranscribeStreamingAsync_NonStreamingResponse_FallsBack` — responder returns `application/json` body; assert the fallback behavior the code implements at ~449-460 (single full-text yield or error — read first, then assert).
8. `TranscribeStreamingAsync_CancelledMidStream_Throws OperationCanceledException` — cancel the token after the first chunk; use a responder whose stream blocks (e.g. a custom `HttpContent` over a `Channel`-fed stream) — if this proves complex, a simpler variant that cancels before enumeration is acceptable; note which you chose.

**Verify**: `dotnet test -c Release --filter FullyQualifiedName~TranscribeStreaming` → all pass.

### Step 3: TranscriptionController — public toggle state machine

Extend `TranscriptionControllerTests.cs` using the existing `CreateController` helper, upgraded so the config/recorder/transcriber mocks are controllable (mirror `TypelessControllerTests`' factory-mock style):

The initial execution found that `IAudioRecorderFactory` returns the sealed
concrete `AudioRecorder`, so it cannot provide deterministic recording control.
Add an internal constructor overload and
`Func<AppConfig, string, CancellationToken, Task<RecordingStats>>` field,
matching `TypelessController`'s existing seam. The public constructor delegates
to the internal overload with a default function that performs the current
`AudioRecorder` setup and `RecordAsync` call unchanged. Tests pass a TCS-backed
delegate. This is an authorized production seam, not a behavior change.

- `IConfigService` mock: `CreateValidatedCopy()` returns `CreateConfig(...)` (helper already exists in the file).
- `IAudioRecorderFactory`/recorder mock: read `TypelessControllerTests.cs` first and copy its recorder-faking approach verbatim (same interfaces).

Tests:

9. `TriggerTranscribeAsync_TranscriberDisabled_ReturnsFalse_NoEvents` — config with `Transcriber.Enabled = false`; assert `false`, `OnStarted` never fired.
10. `TriggerTranscribeAsync_SecondPressWhileRecording_StopsRecording` — first call starts recording (recorder mock blocks on a `TaskCompletionSource` honoring its CancellationToken); second call returns `false` and cancels the recorder's token; then the first call completes through transcription. Assert transcriber invoked once.
11. `TriggerTranscribeAsync_WhileTranscribing_ReturnsFalse` — hold the transcriber mock open with a TCS; a second `TriggerTranscribeAsync` returns false without a second `OnStarted`.
12. `TriggerTranscribeAsync_FiresEventSequence` — happy path: `OnStarted` → `OnProcessingStarted` → `OnCompleted` in order (record into a `List<string>`).
13. `TriggerTranscribeAsync_PersistsHistory` — happy path; verify `IHistoryService.AppendTranscription` called with the final text.

Keep the 3 existing reflection tests until each scenario is re-covered above; `ApplyFinalTextAsync` streamed/non-streamed behavior (existing tests 2-3) is NOT re-covered by 9-13 — keep those two reflection tests, delete only `InvokeStreamingTranscriptionAsync` usage if test 12/13 exercise streaming publicly (they do when `CreateConfig(streamResults: true)`); judge per test and state the outcome in the commit message.

Use `TaskCompletionSource` for all synchronization — no `Task.Delay` sleeps as sync primitives.

**Verify**: `dotnet test -c Release --filter FullyQualifiedName~TranscriptionController` → all pass.

### Step 4: TextRefiner — retry, parsing, error mapping

Extend `TailSlap.Tests/TextRefinerTests.cs` with the same StubHandler:

14. `RefineAsync_ParsesChatCompletionResponse` — 200 with an OpenAI-style `choices[0].message.content` body (read TextRefiner's parse code ~160-249 to get the exact schema incl. the source-gen context) → returns the content.
15. `RefineAsync_TransientFailure_RetriesOnce` — responder fails the first request (500), succeeds the second → result returned, `handler.Requests.Count == 2`. If the 1s backoff makes this test slow, that's acceptable (~1s); do not mock time.
16. `RefineAsync_BothAttemptsFail_Throws` — two 500s → throws; `Requests.Count == 2`.
17. `RefineAsync_ShortOutput_ThrowsShortOutputError` — response content dramatically shorter than input (trigger the `ShortOutputErrorMessage` guard — read its threshold first); assert the exception message matches the const.

Note: method name may be `RefineAsync` or similar — check `ITextRefiner` for the exact signature before writing.

**Verify**: `dotnet test -c Release --filter FullyQualifiedName~TextRefiner` → all pass (6 existing + 4 new).

## Test plan

This plan IS the test plan: 17 new tests across three files, all deterministic (StubHandler + TCS, no real network, no reflection for new tests). Full-suite gate: `dotnet test -c Release` → green, total test count increases by ≥15.

## Done criteria

- [ ] `dotnet build -c Release` exits 0; `dotnet test -c Release` exits 0
- [ ] `TailSlap.Tests/RemoteTranscriberTests.cs` exists with ≥8 tests
- [ ] `TriggerTranscribeAsync` is exercised directly by ≥5 tests (no reflection)
- [ ] TextRefiner retry behavior (2 attempts) asserted via captured request count
- [ ] Zero behavior changes in production code (`git diff TailSlap/` shows at most visibility keywords)
- [ ] No files outside the in-scope list are modified (`git status`)
- [ ] `plans/README.md` status row for 020 updated

## STOP conditions

- `TranscriberConfig`'s API key is not settable in tests without touching DPAPI on a restricted runner and tests fail on it — drop test 2's key variant and report.
- `RemoteTranscriber` constructs its `HttpClient` in a way that bypasses `IHttpClientFactory.CreateClient` (e.g. static client) — STOP and report; do not refactor production HTTP plumbing under a test plan.
- The recorder-faking approach from TypelessControllerTests does not transfer (different interface usage) and Step 3 would require production seams beyond `internal` — STOP and report which seam is missing.
- More than ~30 lines of production-code change appear necessary — this is a test plan; STOP.

## Maintenance notes

- Plan 027 (language hint) MUST extend test 1 to assert the `language` form field — the request-shape test added here is its regression harness.
- Plan 025 (shared result sink) will refactor `TranscriptionController` internals; the public-API tests added here are exactly the characterization net that makes 025 safe — land 020 first.
- Reviewers: watch for hidden real-network calls (a test passing only when localhost:18000 is up is a bug in the test).
