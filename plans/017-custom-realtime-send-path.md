# Plan 017: Serialize all WebSocket sends and fix buffer ownership in the legacy (custom) RealtimeTranscriber

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md` — unless a reviewer dispatched you and told you they
> maintain the index.
>
> **Drift check (run first)**:
> `git diff --stat f3016ac..HEAD -- TailSlap/RealtimeTranscriber.cs TailSlap.Tests/RealtimeTranscriberTests.cs`
> If any in-scope file changed since this plan was written, compare the
> "Current state" excerpts against the live code before proceeding; on a
> mismatch, treat it as a STOP condition.

## Status

- **Priority**: P2 (legacy `custom` provider only — the default provider is `openai`)
- **Effort**: S
- **Risk**: LOW
- **Depends on**: none (plan 016 recommended first for the shared ownership pattern)
- **Category**: bug
- **Planned at**: commit `f3016ac`, 2026-07-30

## Why this matters

The legacy `custom`-provider realtime client (`RealtimeTranscriber`, still selected whenever `transcriber.realtimeProvider != "openai"`) has three send-path bugs. (1) `HeartbeatLoopAsync` calls `_ws.SendAsync` directly every 10s while `SendLoopAsync` concurrently sends audio on the same `ClientWebSocket` — `ClientWebSocket` permits only ONE outstanding send; a collision throws `InvalidOperationException`, which the heartbeat treats as a lost connection and tears the whole session down spuriously. (2) `StopAsync` enqueues a `new byte[32000]` (NOT rented from the pool); if sending that stop item fails, the catch blocks call `ArrayPool<byte>.Shared.Return` on it — `Return` throws `ArgumentException` for foreign arrays, the exception escapes the catch and kills the send loop, so the stop signal is never delivered. Even on success, the stop buffer is never returned (correct for a foreign array, but only by accident). (3) The `DropOldest` bounded channel silently leaks rented buffers on backlog, same as the OpenAI sibling (fixed there by plan 016).

## Current state

### `TailSlap/RealtimeTranscriber.cs` (607 lines)

Channel creation (~110-117): `Channel.CreateBounded<QueueItem>(new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false })` — no drop callback. `QueueItem` is a `readonly record struct QueueItem(byte[]? Buffer, int Count, bool IsStop)` (check the exact declaration near the top of the file).

Send loop (~163-260): on success the audio branch returns the buffer after `SendAsync`; the `IsStop` branch sends `item.Buffer` as silence padding then a `{"action":"stop"}` text frame — and does NOT return the buffer. Both catch blocks do:

```csharp
if (item.Buffer != null)
    ArrayPool<byte>.Shared.Return(item.Buffer);   // throws ArgumentException for the
                                                  // non-pooled 32000-byte stop buffer
```

Heartbeat direct send (~295-310), racing the send loop:

```csharp
// Send an empty binary frame as a keepalive/ping
await _ws.SendAsync(
        ArraySegment<byte>.Empty,
        WebSocketMessageType.Binary,
        endOfMessage: true,
        pingCts.Token
    )
    .ConfigureAwait(false);
```

with catch → `HandleConnectionLostAsync($"Ping failed: {ex.Message}")` → full session teardown.

`StopAsync` (~352-378):

```csharp
if (_sendChannel != null)
{
    var silence = new byte[32000]; // 1s silence
    await _sendChannel
        .Writer.WriteAsync(new QueueItem(silence, silence.Length, true), ct)
        .ConfigureAwait(false);
}
```

### Provider selection

`TailSlap/RealtimeTranscriberFactory.cs` (~line 17) returns this class whenever `RealtimeProvider != "openai"`. AGENTS.md documents `custom` as still supported for legacy stream endpoints — do not delete this class.

### Tests

There is currently NO `RealtimeTranscriberTests.cs` (plan 020/025 do not create one either). Add only the narrowly-scoped tests described below; a full characterization suite is a separate decision (recorded in `plans/README.md` under considered-and-rejected).

## Commands you will need

| Purpose | Command | Expected on success |
|---------|---------|---------------------|
| Build | `dotnet build -c Release` | exit 0 |
| Full suite | `dotnet test -c Release` | all pass |

## Scope

**In scope**:

- `TailSlap/RealtimeTranscriber.cs`
- `TailSlap.Tests/RealtimeTranscriberTests.cs` (create, minimal)
- `plans/README.md` (status row)

**Out of scope**:

- `OpenAIRealtimeTranscriber.cs` — plan 016.
- `RealtimeTranscriberFactory`, provider selection, protocol/message formats — behavior must remain wire-compatible with existing custom servers.
- Removing the heartbeat feature (the staleness detection via `_lastReceiveTime` stays).

## Git workflow

- Branch: `advisor/017-custom-realtime-send-path`
- Commit message example: `Fix: serialize custom realtime sends and stop returning foreign arrays to ArrayPool`
- Do NOT push or open a PR unless the operator instructed it.

## Steps

### Step 1: Tag buffer ownership on QueueItem

Change the record struct to carry ownership:

```csharp
private readonly record struct QueueItem(byte[]? Buffer, int Count, bool IsStop, bool Pooled = true);
```

Update the three construction sites:

- `SendAudioChunkAsync` (~148): `new QueueItem(rented, pcm16Data.Count, false)` → unchanged semantics (`Pooled` defaults true).
- `StopAsync` (~370): `new QueueItem(silence, silence.Length, true, Pooled: false)`.
- The new ping item added in Step 2.

Then make every `ArrayPool<byte>.Shared.Return(item.Buffer)` site conditional: `if (item.Buffer != null && item.Pooled) ArrayPool<byte>.Shared.Return(item.Buffer);` — in both catch blocks, the WebSocket-not-open branch, and add the missing conditional return after the success `IsStop` branch is NOT needed (foreign array, GC handles it) — instead ensure the success audio branch keeps its existing return.

**Verify**: `dotnet build -c Release` → exit 0; `grep -n "Shared.Return" TailSlap/RealtimeTranscriber.cs` → every site guarded by `item.Pooled` (except the `SendAudioChunkAsync` TryWrite-false site, which always deals with a rented buffer — leave it).

### Step 2: Route heartbeat pings through the send channel

Add a `bool IsPing` flag (or reuse a sentinel: `new QueueItem(null, 0, false)` is currently meaningless — prefer an explicit flag for readability):

```csharp
private readonly record struct QueueItem(
    byte[]? Buffer, int Count, bool IsStop, bool Pooled = true, bool IsPing = false);
```

In `HeartbeatLoopAsync`, replace the direct `_ws.SendAsync(ArraySegment<byte>.Empty, ...)` block (and its two catch-teardown blocks) with a channel write:

```csharp
if (_sendChannel != null && !_sendChannel.Writer.TryWrite(new QueueItem(null, 0, false, IsPing: true)))
{
    Logger.Log("Heartbeat: send queue full, skipping ping");
}
```

Keep the staleness check (`timeSinceLastReceive > _heartbeatTimeout` → `HandleConnectionLostAsync`) exactly as-is — that logic is sound and does not send.

In `SendLoopAsync`, handle the ping item before the stop/audio branches:

```csharp
if (item.IsPing)
{
    await _ws.SendAsync(ArraySegment<byte>.Empty, WebSocketMessageType.Binary,
        endOfMessage: true, sendCts.Token).ConfigureAwait(false);
    Logger.Log("Heartbeat: Ping sent");
    _consecutiveErrors = 0;
    continue;
}
```

A failed ping send now flows through the send loop's existing `_consecutiveErrors`/`MaxConsecutiveErrors` machinery instead of instantly killing the session — that is the intended behavior change.

**Verify**: `dotnet build -c Release` → exit 0; `grep -n "_ws.SendAsync" TailSlap/RealtimeTranscriber.cs` → all occurrences are inside `SendLoopAsync` (none in `HeartbeatLoopAsync`). `DisconnectAsync`'s `CloseAsync`-related calls are fine (close handshake is allowed concurrently with sends being stopped — but check: if `DisconnectAsync` calls `_ws.CloseAsync` while the send loop may still be sending, that pre-existing behavior is out of scope; do not fix here, just note it).

### Step 3: Add the drop callback (same as plan 016)

Replace the channel creation with the `itemDropped` overload:

```csharp
_sendChannel = Channel.CreateBounded<QueueItem>(
    new BoundedChannelOptions(100)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false,
    },
    item =>
    {
        if (item.Buffer != null && item.Pooled)
            ArrayPool<byte>.Shared.Return(item.Buffer);
        if (item.IsStop)
            Logger.Log("RealtimeTranscriber: stop marker dropped from send queue");
        Interlocked.Increment(ref _chunksSkipped);
    }
);
```

**Verify**: `dotnet build -c Release` → exit 0.

### Step 4: Minimal tests

Create `TailSlap.Tests/RealtimeTranscriberTests.cs` (model header/usings on `OpenAIRealtimeTranscriberTests.cs`):

- `SendAudioChunkAsync_BeforeConnect_DoesNotThrow` — construct with a minimal `TranscriberConfig`, call `SendAudioChunkAsync(new byte[100])`, assert no exception.
- `StopAsync_NotConnected_DoesNotThrow` — same, `await transcriber.StopAsync()`.
- If `QueueItem` is private and untestable directly, that's fine — these two smoke tests plus compilation are the gate.

**Verify**: `dotnet test -c Release` → all pass.

## Test plan

- Two new smoke tests above.
- Full suite regression: `dotnet test -c Release` → green.
- Manual smoke (only if a legacy custom-protocol server is available): set `transcriber.realtimeProvider` to `custom`, run a session > 30s (two heartbeat intervals during active streaming), confirm no spurious "Connection lost - Ping failed" in `%APPDATA%\TailSlap\logs\app.jsonl`, and confirm the final transcript arrives after stop.

## Done criteria

- [ ] `dotnet build -c Release` exits 0
- [ ] `dotnet test -c Release` exits 0
- [ ] `HeartbeatLoopAsync` contains no `_ws.SendAsync` call
- [ ] Every `ArrayPool` return of a `QueueItem` buffer is guarded by `Pooled`
- [ ] Channel has an `itemDropped` callback
- [ ] No files outside the in-scope list are modified (`git status`)
- [ ] `plans/README.md` status row for 017 updated

## STOP conditions

- The excerpted code doesn't match (drift).
- You find the custom protocol requires the ping to be an actual WebSocket PING control frame rather than an empty binary frame (it is an empty binary frame today — if a comment or server doc in the repo says otherwise, STOP and report; do not change frame semantics).
- Session teardown behavior changes for the STALENESS path (no-data timeout) — that path must remain an immediate `HandleConnectionLostAsync`.

## Maintenance notes

- Reviewers: confirm ping failures now increment `_consecutiveErrors` instead of instant teardown, and that `MaxConsecutiveErrors` still bounds the failure window.
- This class is legacy; if the `custom` provider is ever removed, delete this file and `RealtimeTranscriberFactory`'s branch instead of maintaining it further (decision recorded in plans/README.md).
- Keep the QueueItem ownership shape consistent with `OpenAIRealtimeTranscriber` (plan 016) so a future consolidation is mechanical.
