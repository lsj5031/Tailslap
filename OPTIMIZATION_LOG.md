# Performance Optimization: Async Clipboard Operations

## Baseline Assessment
Establishing a quantitative baseline is impractical in the current environment due to:
- Operating system: Linux (non-Windows)
- Dependency on `System.Windows.Forms.Clipboard` (Win32 specific)
- Dependency on UI SynchronizationContext

## Optimization Rationale

### 1. Responsiveness (UI Thread)
**Problem:** The current `SetTextCore` method uses `Thread.Sleep(50)` within a retry loop. When executing on the UI thread (mandatory for clipboard operations), this blocks the message pump, causing the application to hang for at least 50ms per retry.
**Solution:** Replacing `Thread.Sleep` with `await Task.Delay` allows the message pump to continue processing, maintaining UI responsiveness during clipboard contention.

### 2. Thread Pool Efficiency (Background Threads)
**Problem:** `SetText` currently performs blocking marshaling. If called from a background thread, it uses `ManualResetEventSlim.Wait()` to block that thread until the UI thread completes the work. This "sync-over-async" pattern wastes thread pool threads.
**Solution:** By transitioning to `SetTextAsync`, background callers can `await` the result, releasing their thread back to the pool until the operation is complete.

### 3. Latency and Throughput
While the individual delay (50ms) is small, in real-time dictation scenarios (like those handled by `TextTyper`), these synchronous delays accumulate and can significantly increase the latency between speech and text appearing on screen. Transitioning to async operations allows for better interleaving of transcription and text delivery tasks.
