# Plan 016: Fix ArrayPool double-return and DropOldest buffer/stop-marker loss in the OpenAI realtime transcriber

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**:
> `git diff --stat f3016ac..HEAD -- TailSlap/OpenAIRealtimeTranscriber.cs TailSlap.Tests/OpenAIRealtimeTranscriberTests.cs`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P1
- **Effort**: S
- **Risk**: LOW
- **Depends on**: none
- **Category**: bug
- **Planned at**: commit `f3016ac`, 2026-07-30

## Why this matters

Two bugs in `OpenAIRealtimeTranscriber`'s send pipeline corrupt or lose audio exactly under the conditions (slow/flaky network) where reliability matters most. (1) The send loop returns a rented buffer to `ArrayPool<byte>.Shared` immediately after resampling, then the catch blocks return the SAME buffer again on send failure — `ArrayPool` doesn't detect double returns, so the same array can be handed to two concurrent renters and audio chunks get silently cross-corrupted. (2) The bounded send channel uses `DropOldest` with no drop callback: dropped items leak their rented buffers, `TryWrite` never returns false (making the `_chunksSkipped` accounting dead code), and under backlog the drop can discard the `IsStop` commit marker itself — the final utterance's `input_audio_buffer.commit` is never sent and its transcript never arrives.

## Current state

### `TailSlap/OpenAIRealtimeTranscriber.cs` (803 lines)

Queue item type (line 14):

```csharp
private readonly record struct QueueItem(byte[]? Buffer, int Count, bool IsStop);
```

Channel creation (~lines 98-105), inside `ConnectAsync`:

```csharp
_sendChannel = Channel.CreateBounded<QueueItem>(
    new BoundedChannelOptions(100)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false,
    }
);
```

Producer (~211-220) — the `TryWrite` false-branch is unreachable with `DropOldest`:

```csharp
var rented = ArrayPool<byte>.Shared.Rent(pcm16Data.Count);
Buffer.BlockCopy(pcm16Data.Array!, pcm16Data.Offset, rented, 0, pcm16Data.Count);

if (!_sendChannel.Writer.TryWrite(new QueueItem(rented, pcm16Data.Count, false)))
{
    ArrayPool<byte>.Shared.Return(rented);
    Interlocked.Increment(ref _chunksSkipped);
}
```

Send loop double-return (~256-312). First return happens BEFORE the send; both catch blocks return again:

```csharp
else if (item.Buffer != null)
{
    // Resample 16kHz -> 24kHz and send as base64-encoded input_audio_buffer.append
    var resampled = AudioResampler.Resample16To24(item.Buffer, 0, item.Count);
    ArrayPool<byte>.Shared.Return(item.Buffer);          // <-- first return
    ...
    await _ws.SendAsync(...).ConfigureAwait(false);       // <-- can throw / time out
    ...
}
...
catch (OperationCanceledException)
{
    Logger.Log("OpenAIRealtimeTranscriber SendLoop: Send timeout");
    if (item.Buffer != null)
        ArrayPool<byte>.Shared.Return(item.Buffer);       // <-- second return (same array)
    ...
}
catch (Exception ex)
{
    Logger.Log($"OpenAIRealtimeTranscriber SendLoop: Send failed - {ex.Message}");
    if (item.Buffer != null)
        ArrayPool<byte>.Shared.Return(item.Buffer);       // <-- second return (same array)
    ...
}
```

Stop marker enqueue (~686-692):

```csharp
if (_sendChannel != null)
{
    // Empty buffer as a stop marker
    await _sendChannel
        .Writer.WriteAsync(new QueueItem(null, 0, true), ct)
        .ConfigureAwait(false);
}
```

### Test conventions

`TailSlap.Tests/OpenAIRealtimeTranscriberTests.cs` (13 tests) exercises internal/parsing logic without a live WebSocket. Match its structure for any new tests; the double-return fix is mostly structural, so tests focus on what is testable without a socket.

## Commands you will need

| Purpose | Command | Expected on success |
|---------|---------|---------------------|
| Build | `dotnet build -c Release` | exit 0 |
| Focused tests | `dotnet test -c Release --filter FullyQualifiedName~OpenAIRealtimeTranscriber` | all pass |
| Full suite | `dotnet test -c Release` | all pass |

## Scope

**In scope**:

- `TailSlap/OpenAIRealtimeTranscriber.cs`
- `TailSlap.Tests/OpenAIRealtimeTranscriberTests.cs`
- `plans/README.md` (status row)

**Out of scope**:

- `TailSlap/RealtimeTranscriber.cs` (legacy custom provider) — plan 017 fixes its send path; do not touch here.
- `AudioResampler`, `RealtimeTranscriptionController` — unrelated.
- Changing the channel capacity (100) or the DropOldest policy itself — the policy is fine once drops are handled.

## Git workflow

- Branch: `advisor/016-openai-realtime-buffer-fixes`
- Commit message example: `Fix: ArrayPool double-return and DropOldest buffer loss in OpenAI realtime send path`
- Do NOT push or open a PR unless the operator instructed it.

## Steps

### Step 1: Eliminate the double return with single-ownership tracking

In `SendLoopAsync`, restructure the inner `while (TryRead)` body so each rented buffer is returned exactly once. Pattern:

```csharp
while (_sendChannel.Reader.TryRead(out var item))
{
    byte[]? toReturn = item.Buffer;   // ownership: return exactly once
    try
    {
        if (_ws?.State != WebSocketState.Open)
        {
            continue;   // finally returns the buffer
        }

        using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        sendCts.CancelAfter(_sendTimeout);

        if (item.IsStop)
        {
            await SendCommitAndClearAsync(sendCts.Token);
        }
        else if (item.Buffer != null)
        {
            var resampled = AudioResampler.Resample16To24(item.Buffer, 0, item.Count);
            ArrayPool<byte>.Shared.Return(item.Buffer);
            toReturn = null;   // ownership transferred; catch/finally must not return again
            // ... existing base64 / serialize / SendAsync / bookkeeping unchanged ...
        }

        _consecutiveErrors = 0;
    }
    catch (OperationCanceledException)
    {
        // keep existing logging + _consecutiveErrors / HandleConnectionLostAsync logic,
        // but REMOVE the ArrayPool.Return calls from the catch bodies
        ...
    }
    catch (Exception ex)
    {
        ...
    }
    finally
    {
        if (toReturn != null)
            ArrayPool<byte>.Shared.Return(toReturn);
    }
}
```

Key invariant: `toReturn = null` is assigned on the SAME line-group as the first `Return`, before any awaitable operation. Preserve all existing logging, `_consecutiveErrors` handling, `HandleConnectionLostAsync` thresholds, and the early `return` on max errors exactly as they are — only the buffer ownership changes. Also check the existing "WebSocket not open" skip branch (~241-245, which currently returns the buffer and `continue`s) — fold it into the same pattern (let `finally` do the return).

**Verify**: `dotnet build -c Release` → exit 0; `grep -n "Shared.Return" TailSlap/OpenAIRealtimeTranscriber.cs` → within `SendLoopAsync` there are exactly two return sites: the ownership-transfer one after `Resample16To24` and the one in `finally`.

### Step 2: Handle dropped items via the itemDropped callback

Replace the channel creation with the overload that takes a drop callback:

```csharp
_sendChannel = Channel.CreateBounded<QueueItem>(
    new BoundedChannelOptions(100)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false,
    },
    static item =>
    {
        if (item.Buffer != null)
            ArrayPool<byte>.Shared.Return(item.Buffer);
    }
);
```

Note the callback cannot increment `_chunksSkipped` if declared `static` — drop the `static` modifier and increment `Interlocked.Increment(ref _chunksSkipped)` inside it so the existing skip accounting becomes live again. Remove the now-dead `if (!TryWrite)` branch in `SendAudioChunkAsync` (~216-220)? NO — keep it: `TryWrite` can still return false after the writer is completed. Keep the branch as-is; it is a safe belt-and-braces path.

**Verify**: `dotnet build -c Release` → exit 0.

### Step 3: Protect the stop marker from being dropped

With DropOldest, a full channel drops the OLDEST item when a new one is written — so the stop marker is at risk only if audio chunks are written AFTER it. `SendAudioChunkAsync` can race the stop. Fix minimally: after enqueuing the stop marker (~686-692), call `_sendChannel.Writer.TryComplete()` so no further audio can be enqueued behind it:

```csharp
await _sendChannel.Writer.WriteAsync(new QueueItem(null, 0, true), ct).ConfigureAwait(false);
_sendChannel.Writer.TryComplete();
```

Then confirm `SendAudioChunkAsync` tolerates a completed writer: `TryWrite` on a completed channel returns false → the existing branch returns the rented buffer. Verify `SendLoopAsync`'s `WaitToReadAsync` exits gracefully when the channel completes after draining (it returns false — the loop ends normally). Also add to the drop callback from Step 2: if `item.IsStop`, log `"OpenAIRealtimeTranscriber: stop marker dropped from send queue"` — with TryComplete in place this should never fire; the log is a tripwire.

**Verify**: `dotnet build -c Release` → exit 0; `dotnet test -c Release --filter FullyQualifiedName~OpenAIRealtimeTranscriber` → all 13 existing tests pass.

### Step 4: Tests

Add to `OpenAIRealtimeTranscriberTests.cs` what is testable without a socket:

- If `SendAudioChunkAsync` is callable on a constructed-but-unconnected instance (`_sendChannel == null` → no-op): assert it does not throw (`SendAudioChunkAsync_BeforeConnect_DoesNotThrow`) — may already exist; skip if so.
- A channel-semantics test that documents the drop behavior: create a local `Channel.CreateBounded<int>` with `DropOldest` + drop callback in the test itself and assert the callback receives dropped items (guards against future .NET behavior changes silently breaking the assumption). Name it `BoundedChannel_DropOldest_InvokesItemDroppedCallback`.

If the class exposes no seam to unit-test the send loop directly, do NOT invent one — the structural fix plus existing tests suffice; note that in the commit message.

**Verify**: `dotnet test -c Release` → all pass.

## Test plan

- `BoundedChannel_DropOldest_InvokesItemDroppedCallback` (new) — pins the framework behavior the fix relies on.
- Existing 13 OpenAIRealtimeTranscriber tests — regression gate.
- Full suite: `dotnet test -c Release` → green.
- Manual smoke (optional): run a realtime session against the local backend (`http://localhost:18000`, see AGENTS.md "Local Debug Notes"), speak, stop — the final segment must appear; check `%APPDATA%\TailSlap\logs\app.jsonl` for the absence of "stop marker dropped".

## Done criteria

- [ ] `dotnet build -c Release` exits 0
- [ ] `dotnet test -c Release` exits 0
- [ ] No `ArrayPool<byte>.Shared.Return(item.Buffer)` remains inside the catch blocks of `SendLoopAsync`
- [ ] Channel creation passes an `itemDropped` callback that returns buffers and counts skips
- [ ] Stop-marker enqueue is followed by `Writer.TryComplete()`
- [ ] No files outside the in-scope list are modified (`git status`)
- [ ] `plans/README.md` status row for 016 updated

## STOP conditions

- The send-loop code doesn't match the excerpts (drift).
- `Writer.TryComplete()` after the stop marker breaks a reconnection path (search for `_sendChannel` reassignment — `ConnectAsync` creates a fresh channel per connection; if you find a path that reuses a completed channel, STOP and report).
- The `Channel.CreateBounded(options, itemDropped)` overload is unavailable in the targeted TFM (it exists since .NET 6 — if the compiler disagrees, something is off; STOP).

## Maintenance notes

- Reviewers: trace every path through the reworked send loop and confirm exactly one `Return` per rented buffer (continue-branch, stop-item, success, both catches).
- Plan 017 applies the same ownership discipline to the legacy `RealtimeTranscriber`; keep the two shapes consistent.
- If chunk size or channel capacity is tuned later, the drop callback keeps accounting correct — no further changes needed.
