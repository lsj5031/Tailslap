# Plan 019: Stop tests from wiping the real user's history — injectable HistoryService directory + real tests + file lock

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**:
> `git diff --stat f3016ac..HEAD -- TailSlap/HistoryService.cs TailSlap.Tests/HistoryServiceTests.cs`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P1
- **Effort**: M
- **Risk**: LOW
- **Depends on**: none
- **Category**: tests
- **Planned at**: commit `f3016ac`, 2026-07-30

## Why this matters

Running `dotnet test` on a developer machine currently **destroys and pollutes the developer's real encrypted history**: `HistoryServiceTests.ClearAll_DoesNotThrow` deletes `%APPDATA%\TailSlap\history.jsonl.encrypted` and `transcription-history.jsonl.encrypted`, and the `Append_*` tests write junk entries into them, because `HistoryService` hardcodes its paths with no injection point. The tests are also order-dependent (ClearAll racing Appends across xUnit parallel classes) and assert almost nothing (mostly "does not throw"). Additionally, `HistoryService` file appends/trims are unsynchronized even though three singleton controllers call it concurrently — a trim's read+`File.Move` can race an append and drop an entry. This plan adds a base-directory seam, rewrites the tests against temp directories with real round-trip assertions, and serializes file access.

## Current state

### `TailSlap/HistoryService.cs` (~490 lines) — hardcoded static paths

```csharp
public sealed class HistoryService : IHistoryService
{
    private static string Dir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TailSlap"
        );
    private static string FilePath => Path.Combine(Dir, "history.jsonl.encrypted");
    private static string TranscriptionFilePath =>
        Path.Combine(Dir, "transcription-history.jsonl.encrypted");
    private const int MaxEntries = 50;
    ...
    private int _refinementAppendCount = 0;
    private int _transcriptionAppendCount = 0;
    private const int TrimInterval = 10;
```

- `Append(original, refined, model)` — DPAPI-encrypts both texts, `File.AppendAllText(FilePath, ...)`, increments `_refinementAppendCount`, trims every 10 appends.
- `AppendTranscription(text, durationMs)` — same shape against `TranscriptionFilePath`.
- `ReadAll()` / `ReadAllTranscriptions()` — stream-read via the brace-counting `ReadRawJsonEntries`, decrypt, return tuples.
- `TrimJsonlFile(filePath, historyType)` — reads all entries, keeps last 50, writes `filePath + ".tmp"`, `File.Move(tempPath, filePath, overwrite: true)`. No lock anywhere; counters are non-atomic.
- `ClearRefinementHistory`/`ClearTranscriptionHistory`/`ClearAll` — `File.Delete`.
- Encryption helpers `EncryptString`/`DecryptString` wrap `Dpapi.Protect`/`Unprotect` (Windows DPAPI CurrentUser — works in tests on Windows CI/dev machines; ciphertext round-trips within the same user session).

### `TailSlap/IHistoryService.cs`

```csharp
public interface IHistoryService
{
    void Append(string original, string refined, string model);
    List<(DateTime Timestamp, string Model, string Original, string Refined)> ReadAll();
    void AppendTranscription(string text, int recordingDurationMs);
    List<(DateTime Timestamp, string Text, int RecordingDurationMs)> ReadAllTranscriptions();
    void ClearRefinementHistory();
    void ClearTranscriptionHistory();
    void ClearAll();
}
```

### `TailSlap.Tests/HistoryServiceTests.cs` (10 tests) — the offenders

```csharp
[Fact]
public void ClearAll_DoesNotThrow()
{
    var service = new HistoryService();
    service.ClearAll();                       // deletes the REAL user files
}

[Fact]
public void Append_ValidInputs_DoesNotThrow()
{
    var service = new HistoryService();
    service.Append("original text", "refined text", "gpt-4o");   // pollutes REAL history
}
```

The two `ReadRawJsonEntries_*` tests (reflection on the private static parser) are good — keep them unchanged.

### Concurrent callers (why the lock matters)

`HistoryService` is registered as a DI singleton; `TypelessController`, `TranscriptionController`, and `RealtimeTranscriptionController.CleanupAsync` all append from thread-pool threads and can finish sessions near-simultaneously.

### Conventions

- Sealed classes, `_camelCase` fields, logging wrapped in `try { Logger.Log(...) } catch { }`.
- DI registration lives in `Program.cs` (or `MainForm` wiring) — find with `grep -rn "HistoryService" TailSlap/Program.cs TailSlap/MainForm.cs`. The default constructor must keep working unchanged there.

## Commands you will need

| Purpose | Command | Expected on success |
|---------|---------|---------------------|
| Build | `dotnet build -c Release` | exit 0 |
| Focused tests | `dotnet test -c Release --filter FullyQualifiedName~HistoryService` | all pass |
| Full suite | `dotnet test -c Release` | all pass |

## Scope

**In scope**:

- `TailSlap/HistoryService.cs`
- `TailSlap.Tests/HistoryServiceTests.cs`
- `plans/README.md` (status row)

**Out of scope**:

- `IHistoryService` interface — unchanged.
- Callers/controllers, DI registration — the default ctor keeps today's paths.
- `HistoryForm`/`TranscriptionHistoryForm`, `HistoryQuery` — unrelated.
- ConfigService path seam — separate concern (noted in plan 018 maintenance).

## Git workflow

- Branch: `advisor/019-historyservice-test-seam`
- Commit message example: `Fix: injectable HistoryService directory, temp-dir tests, serialized file access`
- Do NOT push or open a PR unless the operator instructed it.

## Steps

### Step 1: Make the base directory an instance concern

Replace the three static path properties with instance members initialized from an optional constructor parameter:

```csharp
public sealed class HistoryService : IHistoryService
{
    private readonly string _dir;
    private readonly string _filePath;
    private readonly string _transcriptionFilePath;

    public HistoryService()
        : this(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TailSlap"
            )
        ) { }

    public HistoryService(string baseDirectory)
    {
        _dir = baseDirectory ?? throw new ArgumentNullException(nameof(baseDirectory));
        _filePath = Path.Combine(_dir, "history.jsonl.encrypted");
        _transcriptionFilePath = Path.Combine(_dir, "transcription-history.jsonl.encrypted");
    }
```

Then mechanically replace every use of `Dir` → `_dir`, `FilePath` → `_filePath`, `TranscriptionFilePath` → `_transcriptionFilePath` throughout the class. `ReadRawJsonEntries` stays `private static` (the existing reflection tests depend on that exact signature).

**Verify**: `dotnet build -c Release` → exit 0; `dotnet test -c Release --filter FullyQualifiedName~ReadRawJsonEntries` → both reflection tests still pass.

### Step 2: Serialize file access and make counters safe

Add `private readonly object _fileLock = new();`. Wrap the file-touching bodies (after the argument-validation early returns) of `Append`, `AppendTranscription`, `TrimJsonlFile`, `ClearRefinementHistory`, `ClearTranscriptionHistory` in `lock (_fileLock)`. `ReadAll`/`ReadAllTranscriptions` open with `FileShare.ReadWrite` and tolerate torn reads by design — leave them lock-free (do NOT lock reads; the history forms call them from the UI thread and a trim under the lock could stall the UI).

With appends and trims serialized by the lock, the `_refinementAppendCount++`/`_transcriptionAppendCount++` increments are now inside the lock — no `Interlocked` needed; just confirm they end up inside the locked region.

While adding the trim regression test, the existing implementation was found to
keep its read `FileStream` open through `File.Move(..., overwrite: true)`. On
Windows this prevents replacement and silently leaves more than 50 entries.
In `TrimJsonlFile`, materialize `allEntries` inside the reader `using` scope,
then dispose the reader and stream before writing the temporary file and moving
it over the original. Keep the read, temporary write, and replacement inside
`_fileLock`; preserve the existing diagnostics and warning behavior.

**Verify**: `dotnet build -c Release` → exit 0.

### Step 3: Rewrite the destructive tests against temp directories

In `HistoryServiceTests.cs`, replace the 8 non-reflection tests with temp-dir tests. Shared helper:

```csharp
private static HistoryService CreateTempService(out string dir)
{
    dir = Path.Combine(Path.GetTempPath(), "TailSlapTests_" + Guid.NewGuid().ToString("N"));
    return new HistoryService(dir);
}
```

Tests to write (delete the temp dir in a `finally`):

1. `Append_ThenReadAll_RoundTripsPlaintext` — append ("hello", "world", "test-model"), `ReadAll()` returns 1 entry with `Original == "hello"`, `Refined == "world"`, `Model == "test-model"` (proves DPAPI encrypt/decrypt round-trip and file format).
2. `AppendTranscription_ThenRead_RoundTrips` — same for transcription with `RecordingDurationMs`.
3. `Append_EmptyInputs_WritesNothing` — append("", "", "m"), `ReadAll()` empty, file absent.
4. `Trim_KeepsOnlyLast50Entries` — append 61 valid entries (trim fires at the 10-append intervals), assert `ReadAll().Count <= 50` and the LAST appended text is present, the FIRST is gone.
5. `ClearAll_RemovesBothFiles` — append one of each, `ClearAll()`, both `ReadAll` calls empty, files deleted.
6. `ConcurrentAppends_LoseNothing` — `Parallel.For(0, 20, i => svc.AppendTranscription($"entry {i}", i))`, assert `ReadAllTranscriptions().Count == 20` (this is the regression test for Step 2; before the lock it can intermittently fail).
7. `ReadAll_MissingDirectory_ReturnsEmpty` — service on a nonexistent dir, `ReadAll()` returns empty without throwing.

Keep the two `ReadRawJsonEntries_*` reflection tests untouched.

**Verify**: `dotnet test -c Release --filter FullyQualifiedName~HistoryService` → all pass (7 new + 2 kept). Then confirm the user files are untouched: note the `LastWriteTime` of `%APPDATA%\TailSlap\*.encrypted` before and after a full `dotnet test -c Release` run — unchanged.

## Test plan

Covered by Step 3 (that IS the test plan — this is a test-infrastructure plan). Structural pattern: plain xUnit `[Fact]`s as in the existing file; temp-dir lifecycle per test with `try/finally` cleanup.

## Done criteria

- [ ] `dotnet build -c Release` exits 0; `dotnet test -c Release` exits 0
- [ ] `grep -n "SpecialFolder.ApplicationData" TailSlap/HistoryService.cs` matches ONLY inside the parameterless constructor
- [ ] No test in `HistoryServiceTests.cs` constructs `new HistoryService()` without a directory argument
- [ ] `Append`/`AppendTranscription`/`TrimJsonlFile`/`Clear*` bodies run under `_fileLock`
- [ ] A full test run leaves `%APPDATA%\TailSlap\*.encrypted` files' timestamps unchanged
- [ ] No files outside the in-scope list are modified (`git status`)
- [ ] `plans/README.md` status row for 019 updated

## STOP conditions

- `HistoryService` is constructed anywhere with reflection/DI conventions that would break on a second constructor (grep `ActivatorUtilities`/`AddSingleton<HistoryService>` — if DI resolves by longest constructor, the string parameter would break startup; in that case register with an explicit factory lambda `new HistoryService()` and note it — if that requires editing `Program.cs`, that one-line DI registration change is authorized as an exception to scope).
- DPAPI round-trip fails in the test environment (would indicate a non-Windows or restricted-profile CI runner) — STOP and report; do not stub out encryption.
- The trim test cannot deterministically trigger trims (interval logic differs from the excerpt) — re-read the live code, adjust counts; if trim behavior is materially different, STOP.

## Maintenance notes

- Reviewers: confirm reads stayed lock-free and the lock never wraps a `NotificationService` call that could marshal to the UI thread.
- Plan 025 (shared result sink) will route all controller history writes through one place — the lock added here remains the single point of file-access serialization.
- If ConfigService later gets the same seam, mirror this constructor pattern for consistency.
