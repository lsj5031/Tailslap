# Plan 004: Advance TextTyper baseline only after successful delivery

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**:
> `git diff --stat 6d0b6ca..HEAD -- TailSlap/TextTyper.cs TailSlap.Tests/TextTyperTests.cs`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P1
- **Effort**: S
- **Risk**: LOW
- **Depends on**: none (001 recommended so CI gates the new test)
- **Category**: bug
- **Planned at**: commit `6d0b6ca`, 2026-07-09

## Why this matters

`TextTyper` streams progressive full strings (typeless SSE, streaming transcription) by computing a common-prefix delta against `_baselineText`, backspacing, then typing the suffix. After a failed delivery, it still sets `_baselineText = text`. The next chunk then treats the failed text as already on screen and only types the **new suffix** — so the failed prefix never appears (silent data loss). Controllers already surface clipboard fallback notifications; the baseline must stay honest so retries re-attempt undelivered content.

## Current state

### `TailSlap/TextTyper.cs`

- Field: `private string _baselineText = ""` (~18), guarded by `_stateLock`.
- `TypeAsync` ends with (~259–263):

```csharp
// Update baseline
lock (_stateLock)
{
    _baselineText = text;
}

return new TypeResult
{
    DeliverySuccess = deliverySuccess,
    TextOnClipboard = textOnClipboard,
    Text = text,
    NewText = newText,
    BackspaceCount = backspaceCount,
};
```

`deliverySuccess` is computed above (~145–257): false when clipboard paste and SendKeys fallbacks all fail (user is notified that text is on clipboard).

Callers that stream successive full strings:

- `TypelessController` / `TranscriptionController` use `TypeAsync(fullText)` repeatedly.
- Common-prefix logic: `CalculateBackspaceCount` / prefix helpers use `_baselineText`.

### Tests — `TailSlap.Tests/TextTyperTests.cs`

- Uses `TestableTextTyper` subclass that no-ops `SendBackspace` / `TypeTextDirectly` for headless runs.
- Helpers `GetBaselineText` / `SetBaselineText` via reflection (~802+).
- Existing: `TypeAsync_TextDelivered_UpdatesBaseline` asserts baseline updates on success.
- **Missing**: baseline must **not** advance when delivery fails.

**Convention**: xUnit + Moq; async `TypeAsync` tests; reflection helpers already established in this file — match that style.

## Commands you will need

| Purpose | Command | Expected on success |
|---------|---------|---------------------|
| Build | `dotnet build -c Release` | exit 0 |
| TextTyper tests | `dotnet test -c Release --filter FullyQualifiedName~TextTyper` | all pass |
| Full suite | `dotnet test -c Release` | all pass |

## Scope

**In scope**:

- `TailSlap/TextTyper.cs`
- `TailSlap.Tests/TextTyperTests.cs`
- `plans/README.md` status

**Out of scope**:

- Changing paste algorithms in `ClipboardService`
- `RealtimeTranscriptionController` direct typing path (`TypeTextDirectly` / its own `_lastTypedLength`) — separate code path from `TextTyper` baseline
- UI notification copy changes (unless a test forces it)

## Git workflow

- Branch: `advisor/004-texttyper-baseline-success`
- Commit message example: `Fix TextTyper baseline update on failed delivery`
- Do NOT push/PR unless asked.

## Steps

### Step 1: Add a failing regression test first (TDD preferred)

In `TextTyperTests.cs`, add a test modeled after `TypeAsync_TextDelivered_UpdatesBaseline`:

**Case A — failed long paste does not advance baseline**

```csharp
[Fact]
public async Task TypeAsync_DeliveryFailure_DoesNotUpdateBaseline()
{
    var mockClip = CreateMockClipboardService();
    // Force clipboard path: threshold low, text long enough / use multi-char above threshold
    mockClip.Setup(c => c.SetTextAndPasteAsync(It.IsAny<string>())).ReturnsAsync(false);
    mockClip.Setup(c => c.SetTextAsync(It.IsAny<string>())).ReturnsAsync(true);
    // Use TestableTextTyper so TypeTextDirectly is no-op — but for Unicode/newline
    // clipboard path is used. Easiest: text with newline so useClipboard is true,
    // paste fails, SendKeys fallback skipped for multiline → deliverySuccess false.
    var typer = CreateTextTyper(mockClip, clipboardThreshold: 5);
    SetBaselineText(typer, "");

    var result = await typer.TypeAsync("line1\nline2");

    Assert.False(result.DeliverySuccess);
    Assert.Equal("", GetBaselineText(typer)); // still empty / previous baseline
}
```

Adjust setup until you reliably get `DeliverySuccess == false` with `TestableTextTyper` (multiline or Unicode avoids SendKeys success path in production; Testable no-ops SendKeys only when that path runs — multiline skips SendKeys fallback in current code).

**Case B — second call after failure still tries full undelivered text**

After a failed `TypeAsync("hello world")` (baseline stays `""` or prior value), a successful later delivery of `"hello world!"` should attempt to deliver content that includes the previously failed prefix (not only `"!"`).

Assert via mock: `SetTextAndPasteAsync` or observation of `NewText` / calls — e.g. after failure of `"hello world"`, success path for `"hello world extra"` should not assume `"hello world"` was typed.

Keep tests deterministic (no real UI).

**Verify**:

```powershell
dotnet test -c Release --filter FullyQualifiedName~TypeAsync_DeliveryFailure_DoesNotUpdateBaseline
```

**Expected before fix**: FAIL (baseline incorrectly updated).  
**Expected after step 2**: PASS.

### Step 2: Fix baseline update in `TextTyper.TypeAsync`

Replace unconditional baseline assignment with success-only update:

```csharp
// Update baseline only when delivery succeeded so a later chunk
// retries undelivered text via common-prefix logic.
if (deliverySuccess)
{
    lock (_stateLock)
    {
        _baselineText = text;
    }
}
```

Keep `TypeResult.DeliverySuccess` accurate.

**Edge cases**:

- `newText.Length == 0` path already sets `deliverySuccess = true` (backspaces only) — baseline **should** update to `text` in that case (current behavior for empty delta after successful prior state). Leave that as success.
- `autoPaste: false` with successful `SetTextAsync` → `deliverySuccess` true → baseline updates (OK: clipboard is the intended sink).
- When delivery fails but text is on clipboard, baseline stays old so next stream retry can paste again — correct.

**Verify**: regression tests pass; existing `TypeAsync_TextDelivered_UpdatesBaseline` still passes.

### Step 3: Run full TextTyper suite + full solution tests

```powershell
dotnet test -c Release --filter FullyQualifiedName~TextTyper
dotnet test -c Release
```

**Verify**: exit 0.

## Test plan

| Test | Intent |
|------|--------|
| `TypeAsync_DeliveryFailure_DoesNotUpdateBaseline` (new) | Failed delivery leaves baseline unchanged |
| Optional: `TypeAsync_AfterFailure_RetriesFullUndeliveredPrefix` | Next successful full string is not suffix-only |
| Existing success baseline tests | Still update on success |

Pattern: `TailSlap.Tests/TextTyperTests.cs` (`TestableTextTyper`, Moq clipboard).

## Done criteria

- [ ] `_baselineText` updates only when `deliverySuccess` is true
- [ ] New regression test(s) exist and pass
- [ ] `dotnet test -c Release --filter FullyQualifiedName~TextTyper` exits 0
- [ ] Full `dotnet test -c Release` exits 0
- [ ] No out-of-scope files modified
- [ ] `plans/README.md` status for 004 set to `DONE`

## STOP conditions

- You cannot construct a deterministic `DeliverySuccess == false` path under `TestableTextTyper` without changing production control flow in a large way — then add an `internal` test seam (e.g. optional inject for delivery) only if minimal; otherwise STOP and report.
- Callers depend on baseline advancing even on failure (document if you find comments asserting that) — STOP; that would be a product decision.
- Drift rewrote `TypeAsync` substantially — re-validate excerpts.

## Maintenance notes

- Reviewers: confirm streaming callers (`TypelessController`, `TranscriptionController`) benefit without changes.
- Realtime controller’s separate `_lastTypedLength` is **not** fixed here; if similar “advance on failure” exists there, file a follow-up.
- Do not “helpfully” reset baseline on failure beyond leaving it unchanged.
