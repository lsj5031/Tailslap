# Plan 018: Cache config in memory so the WH_KEYBOARD_LL hook thread never touches disk

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**:
> `git diff --stat f3016ac..HEAD -- TailSlap/ConfigService.cs TailSlap/MainForm.cs TailSlap.Tests/ConfigServiceTests.cs`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P1
- **Effort**: S–M
- **Risk**: MED (caching changes config freshness semantics — mitigated by the existing FileSystemWatcher)
- **Depends on**: none
- **Category**: bug
- **Planned at**: commit `f3016ac`, 2026-07-30

## Why this matters

Every push-to-talk key-down runs `TypelessController.HandleKeyDownAsync` synchronously on the low-level keyboard hook callback (`WH_KEYBOARD_LL`), and that path calls `ConfigService.CreateValidatedCopy()` → `LoadOrDefault()` → `File.ReadAllText` + JSON deserialization (plus a full `Save()` if the file is missing). Windows requires hook callbacks to return within `LowLevelHooksTimeout` (~300ms default); if disk is slow at that moment (AV scan, drive wake-up), Windows **silently removes the hook** and push-to-talk stops working until the app reinstalls it — a hard-to-diagnose "hotkey died" failure. Caching the config in memory (invalidated by the FileSystemWatcher that already exists) makes hotkey paths allocation-cheap and disk-free, and also removes redundant disk reads from the other three hotkey modes.

## Current state

### `TailSlap/ConfigService.cs` (663 lines)

- `LoadOrDefault()` (~420-457): reads and deserializes `%APPDATA%\TailSlap\config.json` on EVERY call; creates + `Save()`s a default config if missing; falls back to `new AppConfig()` on error with a notification.
- `Save(AppConfig cfg)` (~459-483): sets `_lastRead = DateTime.Now` (self-save suppression for the watcher) and writes the file.
- `CreateValidatedCopy()` (~535-...): calls `LoadOrDefault()` then clamps invalid values; called on every hotkey press by all four modes.
- Watcher plumbing (~333-418): `FileSystemWatcher` on `config.json` with a 500ms debounce `System.Threading.Timer` that fires `public event Action? ConfigChanged`. Self-saves are suppressed via the `_lastRead` timestamp check in `OnFileChanged`.
- `AppConfig.Clone()` exists (ConfigService.cs ~20-33) and deep-clones all sections (`HotkeyConfig`, `LlmConfig`, `TranscriberConfig` each have `Clone()`).

### The dangerous call path

- `TailSlap/MainForm.cs` (~182-189):

```csharp
_keyboardHook.OnKeyDown += () => SafeFireAndForget(_typelessController.HandleKeyDownAsync());
_keyboardHook.OnKeyUp += () => SafeFireAndForget(_typelessController.HandleKeyUpAsync());
```

`KeyboardHook.HookCallback` invokes `OnKeyDown` synchronously on the hook thread; `HandleKeyDownAsync` (TypelessController.cs ~180-243) executes synchronously through `_config.CreateValidatedCopy()` (line ~203) before its first await/`Task.Run`. The Processing-rejected branch also shows a balloon (`NotificationService.ShowWarning`) synchronously from the same callback.

### `IConfigService` interface

Find it with `grep -n "interface IConfigService" TailSlap/*.cs` — `LoadOrDefault`, `Save`, `CreateValidatedCopy`, `ConfigChanged`, `GetConfigPath` are the members controllers use. The cache is an implementation detail; the interface does not change.

### Conventions

- Logging wrapped in `try { Logger.Log(...) } catch { }`; sealed classes; `_camelCase` private fields.
- Tests: `TailSlap.Tests/ConfigServiceTests.cs` currently covers only the static `IsValid*` helpers (13 tests). Plan 019 introduces temp-dir seams for `HistoryService`; this plan adds a minimal cache test without requiring the config-path seam (see Step 4).

## Commands you will need

| Purpose | Command | Expected on success |
|---------|---------|---------------------|
| Build | `dotnet build -c Release` | exit 0 |
| Focused tests | `dotnet test -c Release --filter "FullyQualifiedName~ConfigService|FullyQualifiedName~TypelessController"` | all pass |
| Full suite | `dotnet test -c Release` | all pass |

## Scope

**In scope**:

- `TailSlap/ConfigService.cs`
- `TailSlap/MainForm.cs` (hook event wiring only)
- `TailSlap.Tests/ConfigServiceTests.cs`
- `plans/README.md` (status row)

**Out of scope**:

- `TypelessController` / other controllers — they keep calling `CreateValidatedCopy()`; it just becomes cheap.
- `IConfigService` interface shape.
- Making the config directory injectable (that is a test-seam concern; if plan 019's pattern is wanted for ConfigService too, it is a follow-up).
- Atomic temp+rename config saves — pre-existing behavior, not this plan.

## Git workflow

- Branch: `advisor/018-config-cache-hook-thread`
- Commit message example: `Fix: cache config in memory so hook callbacks never hit disk`
- Do NOT push or open a PR unless the operator instructed it.

## Steps

### Step 1: Add the in-memory cache to ConfigService

Add fields:

```csharp
private AppConfig? _cache;
private readonly object _cacheLock = new();
```

Rework `LoadOrDefault()`:

```csharp
public AppConfig LoadOrDefault()
{
    lock (_cacheLock)
    {
        if (_cache != null)
            return _cache.Clone();
    }

    var loaded = LoadFromDiskOrDefault();   // the current LoadOrDefault body, renamed
    lock (_cacheLock)
    {
        _cache = loaded;
        return _cache.Clone();
    }
}
```

Rename the existing body to `private AppConfig LoadFromDiskOrDefault()` unchanged. Returning `Clone()` is mandatory — callers (e.g. `MainForm._currentConfig`) mutate the returned object and would otherwise corrupt the cache.

**Verify**: `dotnet build -c Release` → exit 0.

### Step 2: Keep the cache coherent on Save and external change

- In `Save(AppConfig cfg)`, after the successful `File.WriteAllText`, update the cache: `lock (_cacheLock) { _cache = cfg.Clone(); }`. On save failure, invalidate instead: `lock (_cacheLock) { _cache = null; }` (disk and memory may now disagree; next read reloads).
- In the debounce timer callback (~365-374, the one that fires `ConfigChanged?.Invoke()`), invalidate BEFORE firing the event:

```csharp
_ =>
{
    lock (_cacheLock)
    {
        _cache = null;
    }
    try
    {
        ConfigChanged?.Invoke();
    }
    catch { }
},
```

- Also invalidate in `OnFileChanged` for the no-debounce-timer fallback branch (the `timer == null` path fires `ConfigChanged` directly — invalidate there too) and in the `catch` fallback that fires `ConfigChanged`.

**Verify**: `dotnet build -c Release` → exit 0.

### Step 3: Move hook handlers off the hook thread

In `MainForm` (~182-189), wrap both handler bodies in `Task.Run` so the hook callback returns immediately regardless of what the controller does:

```csharp
_keyboardHook.OnKeyDown += () =>
{
    _ = Task.Run(() => SafeFireAndForget(_typelessController.HandleKeyDownAsync()));
};
_keyboardHook.OnKeyUp += () =>
{
    _ = Task.Run(() => SafeFireAndForget(_typelessController.HandleKeyUpAsync()));
};
```

This preserves ordering sufficiently for the state machine: key-down and key-up handlers each guard on controller state (`_state != Recording` etc.), and plan 015 makes those transitions atomic. Note the trade-off in a code review comment if asked: an extreme thread-pool starvation could reorder down/up, but the state guard makes the reordered up a no-op, which fails safe (recording simply doesn't start).

**Verify**: `dotnet build -c Release` → exit 0; `dotnet test -c Release --filter FullyQualifiedName~TypelessController` → all pass.

### Step 4: Cache behavior test

`ConfigService`'s path is hardcoded to `%APPDATA%\TailSlap`, so avoid disk-dependent assertions. Add one safe test to `ConfigServiceTests.cs`:

```csharp
[Fact]
public void LoadOrDefault_ReturnsDistinctClones()
{
    using var svc = new ConfigService();   // check: does ConfigService implement IDisposable? adjust accordingly
    var a = svc.LoadOrDefault();
    var b = svc.LoadOrDefault();
    Assert.NotSame(a, b);                  // mutations of one caller's copy must not leak
    a.AutoPaste = !a.AutoPaste;
    Assert.NotEqual(a.AutoPaste, svc.LoadOrDefault().AutoPaste);
}
```

Caveat: this reads (or creates) the REAL user config file — it must not write to it. `LoadOrDefault` only writes when the file is missing; on a dev machine it exists. If you judge this too invasive for CI, mark the test with a comment and make it tolerant (it performs no `Save`). If `ConfigService` is not IDisposable, drop the `using`.

**Verify**: `dotnet test -c Release --filter FullyQualifiedName~ConfigService` → all pass (13 existing + 1 new).

## Test plan

- `LoadOrDefault_ReturnsDistinctClones` (new) — pins the clone-on-read contract that protects the cache.
- Existing ConfigService validator tests + TypelessController suite — regression gate.
- Manual smoke: run the app, edit `%APPDATA%\TailSlap\config.json` externally (change a hotkey), confirm the change still takes effect within ~1s (watcher → invalidate → reload); toggle a setting in the Settings form and confirm it persists.

## Done criteria

- [ ] `dotnet build -c Release` exits 0; `dotnet test -c Release` exits 0
- [ ] `LoadOrDefault` serves from `_cache` (clone) and only `LoadFromDiskOrDefault` touches the file
- [ ] `Save` updates the cache on success and invalidates on failure
- [ ] Watcher/debounce paths invalidate the cache before firing `ConfigChanged`
- [ ] MainForm hook handlers dispatch via `Task.Run`
- [ ] No files outside the in-scope list are modified (`git status`)
- [ ] `plans/README.md` status row for 018 updated

## STOP conditions

- The debounce/watcher code doesn't match the excerpts (drift).
- You find a caller that relies on `LoadOrDefault` observing an external file edit IMMEDIATELY (bypassing the watcher's 500ms debounce) — grep callers of `LoadOrDefault`/`CreateValidatedCopy` and report if any comment/test demands read-through semantics.
- Any TypelessController test becomes flaky after Step 3 (would indicate a real ordering dependency the state guards don't cover) — STOP and report which test.

## Maintenance notes

- Reviewers: the `_lastRead` self-save suppression interacts with Step 2 — a self-save updates the cache directly and the watcher event for it is suppressed; confirm no path can leave the cache stale after an external edit (watcher invalidation is the only refresh trigger besides Save).
- If plan 019's injectable-directory pattern is later applied to ConfigService, the cache logic carries over unchanged.
- Future settings UI work should keep calling `Save()` (which refreshes the cache) rather than writing the file directly.
