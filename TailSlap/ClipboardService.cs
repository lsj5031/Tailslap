using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

public sealed class ClipboardService : IClipboardService
{
    private const string ExcludeFromMonitorProcessingFormat =
        "ExcludeClipboardContentFromMonitorProcessing";
    private const string CanIncludeInClipboardHistoryFormat = "CanIncludeInClipboardHistory";
    private const string CanUploadToCloudClipboardFormat = "CanUploadToCloudClipboard";

    private readonly IConfigService _configService;

    // Performance metrics. Captures run on per-call STA threads, so access
    // must be synchronized.
    private static readonly object _captureStatsLock = new();
    private static readonly System.Collections.Generic.Dictionary<string, int> _captureStats =
        new();

    // UI thread context for clipboard operations (clipboard requires STA thread)
    private static SynchronizationContext? _uiContext;

    // Events for UI feedback
    public event Action? CaptureStarted;
    public event Action? CaptureEnded;

    public ClipboardService(IConfigService configService)
    {
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
    }

    /// <summary>
    /// Initialize the clipboard service with the UI synchronization context.
    /// Must be called from the UI thread during application startup.
    /// </summary>
    public static void Initialize()
    {
        _uiContext = SynchronizationContext.Current;
        try
        {
            Logger.Log(
                $"ClipboardService.Initialize: uiContext={_uiContext?.GetType().Name ?? "null"}, ThreadId={Thread.CurrentThread.ManagedThreadId}"
            );
        }
        catch { }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    // Foreign windows may be hung; a plain SendMessage would block the calling
    // thread (often the UI thread) indefinitely, so every cross-process window
    // message goes through SendMessageTimeout with SMTO_ABORTIFHUNG.
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint Msg,
        IntPtr wParam,
        IntPtr lParam,
        uint fuFlags,
        uint uTimeout,
        out IntPtr lpdwResult
    );

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint Msg,
        IntPtr wParam,
        StringBuilder lParam,
        uint fuFlags,
        uint uTimeout,
        out IntPtr lpdwResult
    );

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint Msg,
        out int wParam,
        out int lParam,
        uint fuFlags,
        uint uTimeout,
        out IntPtr lpdwResult
    );

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    internal static int NativeInputSize => Marshal.SizeOf<INPUT>();

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    private const uint WM_COPY = 0x0301;
    private const uint WM_PASTE = 0x0302;
    private const uint WM_GETTEXT = 0x000D;
    private const uint WM_GETTEXTLENGTH = 0x000E;
    private const uint EM_GETSEL = 0x00B0;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const uint ForeignQueryTimeoutMs = 1000;
    private const uint ForeignActionTimeoutMs = 2000;
    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;
    private const uint MAPVK_VK_TO_VSC = 0x0;
    private const int SW_RESTORE = 9;
    private const int GWL_STYLE = -16;
    private const long ES_READONLY = 0x0800;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public INPUTUNION U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;

        [FieldOffset(0)]
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private static bool IsTargetElevatedAboveSelf(IntPtr foregroundWindow)
    {
        if (foregroundWindow == IntPtr.Zero)
        {
            return false;
        }

        IntPtr targetProcess = IntPtr.Zero;
        IntPtr targetToken = IntPtr.Zero;
        IntPtr selfToken = IntPtr.Zero;

        try
        {
            NativeMethods.GetWindowThreadProcessId(foregroundWindow, out uint targetProcessId);
            if (targetProcessId == 0)
            {
                return false;
            }

            targetProcess = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION,
                false,
                targetProcessId
            );
            if (targetProcess == IntPtr.Zero)
            {
                return Marshal.GetLastWin32Error() == 5;
            }

            if (
                !TryGetTokenElevation(targetProcess, out bool targetElevated)
                || !TryGetTokenElevation(Process.GetCurrentProcess().Handle, out bool selfElevated)
            )
            {
                return false;
            }

            return targetElevated && !selfElevated;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (targetToken != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(targetToken);
            }

            if (selfToken != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(selfToken);
            }

            if (targetProcess != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(targetProcess);
            }
        }

        bool TryGetTokenElevation(IntPtr processHandle, out bool elevated)
        {
            elevated = false;
            IntPtr token = IntPtr.Zero;
            if (
                !NativeMethods.OpenProcessToken(processHandle, NativeMethods.TOKEN_QUERY, out token)
            )
            {
                return false;
            }

            if (processHandle == targetProcess)
            {
                targetToken = token;
            }
            else
            {
                selfToken = token;
            }

            if (
                !NativeMethods.GetTokenInformation(
                    token,
                    NativeMethods.TokenElevation,
                    out int tokenElevation,
                    sizeof(int),
                    out _
                )
            )
            {
                return false;
            }

            elevated = tokenElevation != 0;
            return true;
        }
    }

    private static string DescribeWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
            return "hWnd=0";

        var title = new StringBuilder(256);
        var cls = new StringBuilder(128);
        string proc = "?";

        // Safely get window title
        try
        {
            int titleLength = GetWindowText(hWnd, title, title.Capacity);
            if (titleLength == 0)
            {
                title.Clear();
                title.Append("(no title)");
            }
        }
        catch (Exception ex)
        {
            try
            {
                Logger.LogWarning(
                    $"DescribeWindow: GetWindowText failed: {ex.GetType().Name}: {ex.Message}"
                );
            }
            catch { }
            title.Clear();
            title.Append("(title error)");
        }

        // Safely get window class
        try
        {
            int classLength = GetClassName(hWnd, cls, cls.Capacity);
            if (classLength == 0)
            {
                cls.Clear();
                cls.Append("(no class)");
            }
        }
        catch (Exception ex)
        {
            try
            {
                Logger.LogWarning(
                    $"DescribeWindow: GetClassName failed: {ex.GetType().Name}: {ex.Message}"
                );
            }
            catch { }
            cls.Clear();
            cls.Append("(class error)");
        }

        // Safely get process information
        try
        {
            uint threadId = NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
            if (threadId != 0)
            {
                if (pid != 0)
                {
                    try
                    {
                        using var p = Process.GetProcessById((int)pid);
                        proc = p.ProcessName + ":" + pid;
                    }
                    catch (ArgumentException)
                    {
                        proc = $"(invalid pid: {pid})";
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            Logger.LogWarning(
                                $"DescribeWindow: Process.GetProcessById failed: {ex.GetType().Name}: {ex.Message}"
                            );
                        }
                        catch { }
                        proc = $"(process error: {pid})";
                    }
                }
                else
                {
                    proc = "(no pid)";
                }
            }
            else
            {
                proc = "(pid error)";
            }
        }
        catch (Exception ex)
        {
            try
            {
                Logger.LogWarning(
                    $"DescribeWindow: GetWindowThreadProcessId failed: {ex.GetType().Name}: {ex.Message}"
                );
            }
            catch { }
            proc = "(pid error)";
        }

        return $"hWnd=0x{hWnd.ToInt64():X}, class={cls}, title={title}, proc={proc}";
    }

    private static string GetWindowClassName(IntPtr hWnd)
    {
        try
        {
            var className = new StringBuilder(128);
            return GetClassName(hWnd, className, className.Capacity) > 0
                ? className.ToString()
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsWindowClass(IntPtr hWnd, string expected)
    {
        try
        {
            var cls = new StringBuilder(128);
            GetClassName(hWnd, cls, cls.Capacity);
            return string.Equals(cls.ToString(), expected, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static void LogClipboardState(string prefix)
    {
        try
        {
            bool hasText = Clipboard.ContainsText();
            int len = 0;
            if (hasText)
            {
                try
                {
                    len = Clipboard.GetText(TextDataFormat.UnicodeText)?.Length ?? 0;
                }
                catch { }
            }
            string formats = "";
            try
            {
                var data = Clipboard.GetDataObject();
                if (data != null)
                {
                    formats = string.Join(",", data.GetFormats());
                }
            }
            catch { }
            try
            {
                Logger.Log(
                    $"[{prefix}] Clipboard hasText={hasText}, textLen={len}, formats=[{formats}]"
                );
            }
            catch { }
        }
        catch (Exception ex)
        {
            try
            {
                Logger.LogWarning(
                    $"[{prefix}] Clipboard state error: {ex.GetType().Name}: {ex.Message}"
                );
            }
            catch { }
        }
    }

    public string CaptureSelectionOrClipboard(
        bool useClipboardFallback = false,
        IntPtr? targetWindow = null
    )
    {
        var sw = Stopwatch.StartNew();

        try
        {
            Logger.Log(
                $"=== CAPTURE START === ThreadId={Thread.CurrentThread.ManagedThreadId}, Apt={Thread.CurrentThread.GetApartmentState()}, Fallback={useClipboardFallback}"
            );
        }
        catch { }

        // Resolve the target before any UI callback can show an overlay or otherwise
        // change activation. A caller that captured the target at the hotkey boundary
        // wins; the fallback is retained for tray/menu invocation.
        IntPtr foregroundWindow = targetWindow.GetValueOrDefault();
        if (foregroundWindow == IntPtr.Zero)
        {
            try
            {
                foregroundWindow = NativeMethods.GetForegroundWindow();
            }
            catch (Exception ex)
            {
                try
                {
                    Logger.LogWarning(
                        $"Step 2 ERROR: GetForegroundWindow failed: {ex.GetType().Name}: {ex.Message}"
                    );
                }
                catch { }
            }
        }

        try
        {
            Logger.Log(
                $"Step 2a: Capture target {(targetWindow.HasValue ? "provided" : "current foreground")}: {DescribeWindow(foregroundWindow)}"
            );
        }
        catch { }

        // Notify UI only after the target has been captured.
        CaptureStarted?.Invoke();

        if (targetWindow.HasValue && foregroundWindow == IntPtr.Zero)
        {
            try
            {
                Logger.LogWarning("Capture aborted: no external foreground target was available");
            }
            catch { }
            return string.Empty;
        }

        try
        {
            // Snapshot only diagnostics here. Selection capture must not treat
            // this pre-existing clipboard content as the user's selection.
            LogClipboardState("Before selection capture");

            // Check window class for logging purposes (but don't skip any operations)
            string windowClass = "unknown";
            try
            {
                try
                {
                    Logger.Log("Step 3: Analyzing foreground window...");
                }
                catch { }
                if (foregroundWindow != IntPtr.Zero)
                {
                    try
                    {
                        Logger.Log(
                            $"Step 3a: Checking window class for hWnd=0x{foregroundWindow.ToInt64():X}"
                        );
                    }
                    catch { }
                    var cls = new StringBuilder(128);
                    try
                    {
                        int classLength = GetClassName(foregroundWindow, cls, cls.Capacity);
                        if (classLength > 0)
                        {
                            windowClass = cls.ToString();
                            try
                            {
                                Logger.Log($"Step 3b: Window class='{windowClass}'");
                            }
                            catch { }
                        }
                        else
                        {
                            windowClass = "(no class)";
                            try
                            {
                                Logger.Log("Step 3b: No window class available");
                            }
                            catch { }
                        }
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            Logger.LogWarning(
                                $"Step 3b ERROR: GetClassName failed: {ex.GetType().Name}: {ex.Message}"
                            );
                        }
                        catch { }
                        windowClass = "(class error)";
                    }
                }
                else
                {
                    try
                    {
                        Logger.Log("Step 3a: No foreground window to check");
                    }
                    catch { }
                    windowClass = "(no window)";
                }
            }
            catch (Exception ex)
            {
                try
                {
                    Logger.LogWarning(
                        $"Step 3 ERROR: Window analysis failed: {ex.GetType().Name}: {ex.Message}"
                    );
                }
                catch { }
                windowClass = "(analysis error)";
            }

            // 1) Try UI Automation first, but isolate it in a helper process so provider crashes
            // cannot terminate the main tray app.
            bool isSublime = IsWindowClass(foregroundWindow, "PX_WINDOW_CLASS");
            try
            {
                Logger.Log(
                    $"Step 3c: Window analysis: isSublime={isSublime}, useClipboardFallback={useClipboardFallback}"
                );
            }
            catch { }

            bool continueUiaAttempts = true;
            try
            {
                Logger.Log(
                    $"Step 4: Attempting UI Automation via isolated helper for {windowClass}..."
                );
            }
            catch { }

            try
            {
                var uia = TryGetSelectionViaUiaProbe(
                    UiaProbeMode.Focused,
                    foregroundWindow,
                    800,
                    "UIA"
                );
                continueUiaAttempts = uia.ContinueAttempts;
                if (!string.IsNullOrWhiteSpace(uia.Text))
                {
                    RecordCaptureSuccess("UIA", true);
                    try
                    {
                        Logger.Log($"Step 4a: UIA selection captured: len={uia.Text.Length}");
                    }
                    catch { }
                    try
                    {
                        Logger.Log("=== CAPTURE SUCCESS (UIA) ===");
                    }
                    catch { }
                    return uia.Text!;
                }

                RecordCaptureSuccess("UIA", false);
                try
                {
                    Logger.Log("Step 4a: UIA selection unavailable or empty");
                }
                catch { }
            }
            catch (Exception ex)
            {
                continueUiaAttempts = false;
                try
                {
                    Logger.LogWarning(
                        $"Step 4 ERROR: UIA selection error: {ex.GetType().Name}: {ex.Message}"
                    );
                }
                catch { }
            }

            if (continueUiaAttempts)
            {
                try
                {
                    Logger.Log("Step 4b: Attempting UIA FromPoint via isolated helper...");
                }
                catch { }

                try
                {
                    var uiaPt = TryGetSelectionViaUiaProbe(
                        UiaProbeMode.Caret,
                        foregroundWindow,
                        500,
                        "UIA(FromPoint)"
                    );
                    continueUiaAttempts = uiaPt.ContinueAttempts;
                    if (!string.IsNullOrWhiteSpace(uiaPt.Text))
                    {
                        RecordCaptureSuccess("UIA_FromPoint", true);
                        try
                        {
                            Logger.Log(
                                $"Step 4c: UIA(FromPoint) selection captured: len={uiaPt.Text.Length}"
                            );
                        }
                        catch { }
                        try
                        {
                            Logger.Log("=== CAPTURE SUCCESS (UIA FromPoint) ===");
                        }
                        catch { }
                        return uiaPt.Text!;
                    }

                    RecordCaptureSuccess("UIA_FromPoint", false);
                    try
                    {
                        Logger.Log("Step 4c: UIA(FromPoint) selection unavailable or empty");
                    }
                    catch { }
                }
                catch (Exception ex)
                {
                    continueUiaAttempts = false;
                    try
                    {
                        Logger.LogWarning(
                            $"Step 4 ERROR: UIA(FromPoint) selection error: {ex.GetType().Name}: {ex.Message}"
                        );
                    }
                    catch { }
                }
            }

            if (continueUiaAttempts)
            {
                try
                {
                    Logger.Log("Step 4d: Attempting UIA deep search via isolated helper...");
                }
                catch { }

                try
                {
                    var uiaDeep = TryGetSelectionViaUiaProbe(
                        UiaProbeMode.Deep,
                        foregroundWindow,
                        800,
                        "UIA(deep)"
                    );
                    continueUiaAttempts = uiaDeep.ContinueAttempts;
                    if (!string.IsNullOrWhiteSpace(uiaDeep.Text))
                    {
                        try
                        {
                            Logger.Log($"UIA(deep) selection captured: len={uiaDeep.Text.Length}");
                        }
                        catch { }
                        return uiaDeep.Text!;
                    }

                    try
                    {
                        Logger.Log("UIA(deep) found no selection");
                    }
                    catch { }
                }
                catch (Exception ex)
                {
                    continueUiaAttempts = false;
                    try
                    {
                        Logger.LogWarning($"UIA(deep) error: {ex.GetType().Name}: {ex.Message}");
                    }
                    catch { }
                }
            }

            if (!continueUiaAttempts)
            {
                try
                {
                    Logger.LogWarning(
                        "Step 4e: UIA helper failed or timed out; skipping additional UI Automation attempts"
                    );
                }
                catch { }
            }

            // 2) Win32 direct read (standard edit controls)
            try
            {
                try
                {
                    Logger.Log("Step 5: Attempting Win32 selection read...");
                }
                catch { }
                var win32Sel = TryGetSelectionViaWin32(foregroundWindow);
                if (!string.IsNullOrWhiteSpace(win32Sel))
                {
                    try
                    {
                        Logger.Log($"Step 5a: Win32 selection captured: len={win32Sel.Length}");
                    }
                    catch { }
                    try
                    {
                        Logger.Log("=== CAPTURE SUCCESS (Win32) ===");
                    }
                    catch { }
                    return win32Sel;
                }
                else
                {
                    try
                    {
                        Logger.Log("Step 5a: Win32 selection unavailable or empty");
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                try
                {
                    Logger.LogWarning(
                        $"Step 5 ERROR: Win32 selection error: {ex.GetType().Name}: {ex.Message}"
                    );
                }
                catch { }
            }

            // 3) Clipboard-based copy without clearing, using sequence number + multiple methods
            uint seqBefore = 0;
            try
            {
                seqBefore = GetClipboardSequenceNumber();
                Logger.Log($"Clipboard seq before: {seqBefore}");
            }
            catch { }

            // A clipboard fallback is only safe when the capture attempt changed
            // the clipboard. An unchanged clipboard may contain stale, unrelated
            // text, so never accept it as the selected text.
            bool clipboardChanged = false;
            IntPtr targetHwnd = ResolveFocusHwnd(foregroundWindow);
            try
            {
                Logger.Log($"Target hwnd for copy: {DescribeWindow(targetHwnd)}");
            }
            catch { }

            // Unified copy strategy: the same method sequence and timeouts for
            // every app. Whether a copy landed is decided by the clipboard
            // sequence check inside TryCopyAndRead, not by the target's window
            // class, so no app needs special-casing. The longer timeouts only
            // bind on failure paths; successful copies return as soon as the
            // clipboard changes.
            const int timeoutPrimary = 800;
            const int timeoutAlt = 500;

            try
            {
                Logger.Log("Step 6c: Starting SendKeys copy attempts...");
            }
            catch { }
            // Try SendKeys first (fastest for most apps)
            if (
                TryCopyAndRead(
                    targetHwnd,
                    seqBefore,
                    CopyMethod.SendKeysCtrlC,
                    out var copied,
                    ref clipboardChanged,
                    timeoutPrimary
                )
            )
            {
                try
                {
                    Logger.Log(
                        $"Step 7: Captured via SendKeys Ctrl+C: len={copied.Length} (elapsed {sw.ElapsedMilliseconds} ms)"
                    );
                }
                catch
                {
                    try
                    {
                        Logger.Log("=== CAPTURE SUCCESS (SendKeys) ===");
                    }
                    catch { }
                }
                return copied;
            }

            // Application-specific attempts with shorter timeouts
            if (isSublime) // Sublime Text
            {
                try
                {
                    Logger.LogWarning("Sublime: SendKeys failed, trying Double Ctrl+C");
                }
                catch { }
                if (
                    TryCopyAndRead(
                        targetHwnd,
                        seqBefore,
                        CopyMethod.DoubleCtrlC,
                        out copied,
                        ref clipboardChanged,
                        timeoutAlt
                    )
                )
                {
                    try
                    {
                        Logger.Log(
                            $"Captured via Double Ctrl+C (Sublime): len={copied.Length} (elapsed {sw.ElapsedMilliseconds} ms)"
                        );
                    }
                    catch { }
                    return copied;
                }
            }

            // Standard Ctrl+C, then Ctrl+Insert as the fallback: one shared
            // sequence for every app.
            if (
                TryCopyAndRead(
                    targetHwnd,
                    seqBefore,
                    CopyMethod.CtrlC,
                    out copied,
                    ref clipboardChanged,
                    timeoutAlt
                )
            )
            {
                try
                {
                    Logger.Log(
                        $"Captured via Ctrl+C: len={copied.Length} (elapsed {sw.ElapsedMilliseconds} ms)"
                    );
                }
                catch { }
                return copied;
            }

            if (
                TryCopyAndRead(
                    targetHwnd,
                    seqBefore,
                    CopyMethod.CtrlInsert,
                    out copied,
                    ref clipboardChanged,
                    timeoutAlt
                )
            )
            {
                try
                {
                    Logger.Log(
                        $"Captured via Ctrl+Insert: len={copied.Length} (elapsed {sw.ElapsedMilliseconds} ms)"
                    );
                }
                catch { }
                return copied;
            }

            try
            {
                Logger.LogWarning("All copy attempts failed to update clipboard");
            }
            catch { }

            // Optional compatibility fallback: only use text if the clipboard
            // changed during this capture attempt. This preserves the setting's
            // usefulness without silently processing stale clipboard contents.
            if (useClipboardFallback && clipboardChanged)
            {
                string? fallback = null;
                try
                {
                    if (Clipboard.ContainsText())
                    {
                        fallback = Clipboard.GetText(TextDataFormat.UnicodeText);
                        if (!string.IsNullOrWhiteSpace(fallback))
                        {
                            Logger.LogWarning(
                                $"No selection captured; using changed clipboard fallback (elapsed {sw.ElapsedMilliseconds} ms), len={fallback.Length}"
                            );
                            return fallback;
                        }
                    }
                }
                catch (Exception ex)
                {
                    try
                    {
                        Logger.LogWarning(
                            $"Changed clipboard fallback read failed: {ex.GetType().Name}: {ex.Message}"
                        );
                    }
                    catch { }
                }
            }

            try
            {
                Logger.LogWarning(
                    $"Step FINAL: No selection captured; not falling back to existing clipboard (elapsed {sw.ElapsedMilliseconds} ms)"
                );
            }
            catch { }
            try
            {
                Logger.LogWarning(
                    "Troubleshooting: make sure text is highlighted in the target app before triggering the hotkey"
                );
            }
            catch { }
            try
            {
                Logger.Error("=== CAPTURE FAILED ===");
            }
            catch { }
            return string.Empty;
        }
        finally
        {
            // Ensure UI animation stops
            CaptureEnded?.Invoke();
        }
    }

    public async System.Threading.Tasks.Task<bool> SetTextAsync(string text)
    {
        // If we're not on the UI thread and have a UI context, marshal the call
        var currentContext = SynchronizationContext.Current;
        bool hasUiContext = _uiContext != null;
        bool needsMarshal = hasUiContext && currentContext != _uiContext;

        try
        {
            Logger.Log(
                $"SetTextAsync: hasUiContext={hasUiContext}, currentContext={currentContext?.GetType().Name ?? "null"}, needsMarshal={needsMarshal}, ThreadId={Thread.CurrentThread.ManagedThreadId}"
            );
        }
        catch { }

        if (needsMarshal)
        {
            try
            {
                Logger.Log("SetTextAsync: Marshaling to UI thread");
            }
            catch { }

            var setTextTask = RunOnUiContextAsync(() => SetTextCoreAsync(text));
            var completedTask = await Task.WhenAny(setTextTask, Task.Delay(5000))
                .ConfigureAwait(false);
            if (completedTask != setTextTask)
            {
                try
                {
                    Logger.LogWarning("SetTextAsync: Timed out waiting for UI thread");
                }
                catch { }
                NotificationService.ShowError("Failed to set clipboard text. Please try again.");
                return false;
            }

            return await setTextTask.ConfigureAwait(false);
        }

        return await SetTextCoreAsync(text).ConfigureAwait(false);
    }

    private async System.Threading.Tasks.Task<bool> SetTextCoreAsync(string text)
    {
        bool excludeFromClipboardHistory = _configService
            .CreateValidatedCopy()
            .ExcludeFromClipboardHistory;
        int retries = 3;
        while (retries-- > 0)
        {
            try
            {
                if (excludeFromClipboardHistory)
                {
                    Clipboard.SetDataObject(BuildExcludedDataObject(text), copy: true);
                }
                else
                {
                    Clipboard.SetText(text, TextDataFormat.UnicodeText);
                }
                try
                {
                    Logger.Log($"SetText ok, len={text?.Length ?? 0}");
                }
                catch { }
                return true;
            }
            catch (Exception ex)
            {
                try
                {
                    Logger.LogWarning(
                        $"SetText failed (retries left {retries}): {ex.GetType().Name}: {ex.Message}"
                    );
                }
                catch { }
                if (retries == 0)
                {
                    NotificationService.ShowError(
                        "Failed to set clipboard text. Please try again."
                    );
                }
                await Task.Delay(50).ConfigureAwait(true);
            }
        }
        return false;
    }

    internal static DataObject BuildExcludedDataObject(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var dataObject = new DataObject();
        dataObject.SetData(DataFormats.UnicodeText, autoConvert: false, text);
        dataObject.SetData(
            ExcludeFromMonitorProcessingFormat,
            autoConvert: false,
            CreateDwordStream(1)
        );
        dataObject.SetData(
            CanIncludeInClipboardHistoryFormat,
            autoConvert: false,
            CreateDwordStream(0)
        );
        dataObject.SetData(
            CanUploadToCloudClipboardFormat,
            autoConvert: false,
            CreateDwordStream(0)
        );
        return dataObject;
    }

    private static MemoryStream CreateDwordStream(int value)
    {
        return new MemoryStream(BitConverter.GetBytes(value), writable: false);
    }

    public System.Threading.Tasks.Task<bool> PasteAsync(
        IntPtr? expectedForegroundWindow = null,
        uint expectedProcessId = 0,
        string? expectedWindowClass = null
    )
    {
        var currentContext = SynchronizationContext.Current;
        bool hasUiContext = _uiContext != null;
        bool needsMarshal = hasUiContext && currentContext != _uiContext;

        try
        {
            Logger.Log(
                $"PasteAsync: hasUiContext={hasUiContext}, currentContext={currentContext?.GetType().Name ?? "null"}, needsMarshal={needsMarshal}, ThreadId={Thread.CurrentThread.ManagedThreadId}"
            );
        }
        catch { }

        if (needsMarshal)
        {
            try
            {
                Logger.Log("PasteAsync: Marshaling to UI thread");
            }
            catch { }

            return RunOnUiContextAsync(() =>
                PasteAsyncCore(expectedForegroundWindow, expectedProcessId, expectedWindowClass)
            );
        }

        return PasteAsyncCore(expectedForegroundWindow, expectedProcessId, expectedWindowClass);
    }

    private async System.Threading.Tasks.Task<bool> PasteAsyncCore(
        IntPtr? expectedForegroundWindow = null,
        uint expectedProcessId = 0,
        string? expectedWindowClass = null
    )
    {
        try
        {
            LogPasteDiagnostic("PasteAsync");

            bool restoredForeground = false;
            var foregroundWindow = NativeMethods.GetForegroundWindow();
            if (
                expectedForegroundWindow.HasValue
                && expectedForegroundWindow.Value != IntPtr.Zero
                && foregroundWindow != expectedForegroundWindow.Value
            )
            {
                // The refinement hotkey is delivered to TailSlap's hidden form and
                // an LLM request can also outlive a focus transition. Restore the
                // exact captured target before refusing to inject input; never send
                // a paste chord to whichever window happens to be foreground now.
                uint targetProcessId = expectedProcessId;
                string targetWindowClass = expectedWindowClass ?? string.Empty;
                if (targetProcessId == 0 || string.IsNullOrWhiteSpace(targetWindowClass))
                {
                    NativeMethods.TryGetWindowIdentity(
                        expectedForegroundWindow.Value,
                        out targetProcessId,
                        out targetWindowClass
                    );
                }

                if (
                    NativeMethods.TryRestoreWindow(
                        expectedForegroundWindow.Value,
                        targetProcessId,
                        targetWindowClass
                    )
                    && NativeMethods.GetForegroundWindow() == expectedForegroundWindow.Value
                )
                {
                    foregroundWindow = expectedForegroundWindow.Value;
                    restoredForeground = true;
                    Logger.Log(
                        $"PasteAsync: Restored expected target window 0x{foregroundWindow.ToInt64():X} before paste"
                    );
                }
                else
                {
                    Logger.LogWarning(
                        "PasteAsync: Expected target window is no longer foreground and could not be restored; leaving text on the clipboard"
                    );
                    return false;
                }
            }

            if (foregroundWindow == IntPtr.Zero)
            {
                try
                {
                    Logger.Log("PasteAsync: No foreground window, will still attempt paste");
                }
                catch { }
            }

            if (IsTargetElevatedAboveSelf(foregroundWindow))
            {
                try
                {
                    NativeMethods.GetWindowThreadProcessId(
                        foregroundWindow,
                        out uint targetProcessId
                    );
                    Logger.LogWarning(
                        $"PasteAsync: Paste blocked because target process {targetProcessId} is elevated above TailSlap"
                    );
                }
                catch { }

                NotificationService.ShowError(
                    "Cannot paste into an elevated (admin) window. Text is on your clipboard — press Ctrl+V."
                );
                return false;
            }

            // WaitForModifierRelease polls with Thread.Sleep; run it on the
            // thread pool so a held hotkey does not freeze the UI thread the
            // paste is marshaled to.
            bool modifiersReleased = await Task.Run(() =>
                    TailSlap.NativeInputSimulator.WaitForModifierRelease(1000)
                )
                .ConfigureAwait(true);
            if (!modifiersReleased)
            {
                try
                {
                    Logger.LogWarning(
                        "PasteAsync: Modifier release wait timed out; proceeding with paste"
                    );
                }
                catch { }
            }

            if (restoredForeground)
            {
                // Only a freshly restored window needs time to accept focus
                // before it can receive the paste chord.
                await Task.Delay(250).ConfigureAwait(true);
            }
            bool success = await PasteWithMultipleMethodsAsync(
                foregroundWindow,
                expectedForegroundWindow
            );
            if (!success)
            {
                try
                {
                    Logger.Error("All paste methods failed");
                }
                catch { }
                NotificationService.ShowError("Auto-paste failed. Please paste manually (Ctrl+V).");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            try
            {
                Logger.Error($"Paste failed: {ex.GetType().Name}: {ex.Message}");
            }
            catch { }
            NotificationService.ShowError($"Paste operation failed: {ex.Message}");
            return false;
        }
    }

    public async System.Threading.Tasks.Task<bool> SetTextAndPasteAsync(string text)
    {
        // Optimized method to set text and paste in one go, saving/restoring clipboard if possible
        // Note: For real-time typing, we might skip saving original clipboard to be faster,
        // or we just overwrite it because the user intends to paste this text.
        // Given this is for dictation, overwriting clipboard is acceptable behavior (like Nuance Dragon).

        if (!await SetTextAsync(text).ConfigureAwait(false))
            return false;

        return await PasteAsync().ConfigureAwait(false);
    }

    internal static IReadOnlyList<string> PasteMethodOrder { get; } =
        Array.AsReadOnly(new[] { "WM_PASTE", "SendInput Ctrl+V", "Ctrl+V", "Shift+Insert" });

    // Custom editors (browsers, Electron, etc.) get one extra last-resort
    // method: paste as plain text. Its plain-text handler is more permissive
    // than the HTML paste path and is a known workaround when the normal
    // Ctrl+V chord is dropped. It is only reached when every earlier method
    // was verified not to have landed, so the side effects of Ctrl+Shift+V in
    // other editors are acceptable.
    internal static IReadOnlyList<string> CustomEditorPasteMethodOrder { get; } =
        Array.AsReadOnly(PasteMethodOrder.Concat(new[] { PlainTextPasteMethod }).ToArray());

    private const string SendInputPasteMethod = "SendInput Ctrl+V";

    private const string SendKeysPasteMethod = "Ctrl+V";

    private const string ShiftInsertPasteMethod = "Shift+Insert";

    private const string PlainTextPasteMethod = "Ctrl+Shift+V";

    private const string WindowMessagePasteMethod = "WM_PASTE";

    internal static bool ShouldStopAfterUnverifiedPasteAttempt(
        bool supportsNativePaste,
        string method
    )
    {
        // WM_PASTE is only a capability probe for custom editors and sends no
        // input when unsupported. Every other custom-editor method injects a
        // real paste chord, so never follow it with another blind chord: UIA
        // verification can be stale or can observe the wrong control.
        return !supportsNativePaste && method != WindowMessagePasteMethod;
    }

    internal static bool ShouldTrustUnverifiedPasteAttempt(
        bool supportsNativePaste,
        string method,
        bool injectionSucceeded
    )
    {
        // A custom editor can accept the paste while UIA still reports the
        // old value. If the input method injected successfully, trust that
        // result and suppress the misleading failure notification. Native
        // targets and the WM_PASTE probe still require their normal result.
        return (!supportsNativePaste && method != WindowMessagePasteMethod && injectionSucceeded);
    }

    private async System.Threading.Tasks.Task<bool> PasteWithMultipleMethodsAsync(
        IntPtr expectedForegroundWindow,
        IntPtr? expectedForegroundOverride = null
    )
    {
        LogPasteDiagnostic("PasteWithMultipleMethods");
        var pasteTarget = ResolvePasteTarget();
        bool supportsNativePaste = SupportsWindowMessagePaste(pasteTarget);
        IReadOnlyList<string> methodOrder =
            pasteTarget != IntPtr.Zero && !supportsNativePaste
                ? CustomEditorPasteMethodOrder
                : PasteMethodOrder;

        // Browser/custom editors have no generic Win32 paste verification.
        // Read the focused control's value through the UIA probe before
        // injecting anything so each attempt can be verified instead of
        // trusted: retries then happen only when the text provably did not
        // land, which keeps a fallback chord from duplicating a successful one.
        string? valueBefore = null;
        string? clipboardText = null;
        IntPtr verificationWindow = IntPtr.Zero;
        if (!supportsNativePaste)
        {
            clipboardText = TryReadClipboardText();
            verificationWindow = NativeMethods.GetForegroundWindow();
            valueBefore = await TryReadFocusedValueAsync(verificationWindow);
            try
            {
                Logger.Log(
                    valueBefore != null
                        ? $"Custom-editor paste verification armed (focused control value is readable, len={valueBefore.Length})"
                        : "Custom-editor paste verification unavailable; at most one unverified attempt will be made"
                );
            }
            catch { }
        }

        foreach (string method in methodOrder)
        {
            try
            {
                Logger.Log($"Attempting paste with {method}");

                if (
                    expectedForegroundOverride.HasValue
                    && expectedForegroundOverride.Value != IntPtr.Zero
                    && NativeMethods.GetForegroundWindow() != expectedForegroundOverride.Value
                )
                {
                    Logger.LogWarning(
                        "PasteWithMultipleMethodsAsync: Target changed before injection"
                    );
                    return false;
                }

                NormalizeInputState();
                bool success = method switch
                {
                    WindowMessagePasteMethod => await TryPasteWindowMessageAsync(),
                    SendKeysPasteMethod => await TryPasteCtrlVAsync(),
                    ShiftInsertPasteMethod => await TryPasteShiftInsertAsync(),
                    PlainTextPasteMethod => await TryPasteCtrlShiftVAsync(),
                    SendInputPasteMethod => await TryPasteSendInputAsync(expectedForegroundWindow),
                    _ => false,
                };

                if (supportsNativePaste)
                {
                    if (success)
                    {
                        Logger.Log($"Paste successful with {method}");
                        return true;
                    }
                }
                else if (valueBefore != null && method != WindowMessagePasteMethod)
                {
                    // WM_PASTE returns false before sending anything when the
                    // target is unsupported, so it cannot have delivered; skip
                    // its probe round trip. Every other method may have
                    // injected, so poll the control (browser commits are
                    // asynchronous) and decide.
                    string? valueAfter = await PollForPasteDeliveryAsync(
                        valueBefore,
                        clipboardText,
                        verificationWindow
                    );
                    if (valueAfter == null)
                    {
                        // Verification was lost mid-flight. The injection may
                        // well have landed, so never report failure (and never
                        // retry, which could duplicate): trust the method's own
                        // result and stop the loop.
                        try
                        {
                            Logger.LogWarning(
                                $"{method}: verification read failed after injection; treating the attempt as delivered to avoid a duplicate paste"
                            );
                        }
                        catch { }
                        if (success)
                        {
                            return true;
                        }

                        try
                        {
                            Logger.LogWarning(
                                $"{method}: verification lost and the method reported no success; stopping to avoid a duplicate paste"
                            );
                        }
                        catch { }
                    }
                    else if (IsPasteDelivered(valueBefore, valueAfter, clipboardText))
                    {
                        try
                        {
                            Logger.Log(
                                $"{method}: paste verified delivered (focused value length {valueBefore.Length} -> {valueAfter.Length})"
                            );
                        }
                        catch { }
                        return true;
                    }
                    else
                    {
                        try
                        {
                            Logger.Log(
                                $"{method}: paste verification reported NOT delivered (focused value length {valueBefore.Length} -> {valueAfter.Length} after polling)"
                            );
                        }
                        catch { }

                        if (ShouldTrustUnverifiedPasteAttempt(supportsNativePaste, method, success))
                        {
                            try
                            {
                                Logger.Log(
                                    $"{method}: input injection succeeded but UIA did not observe the paste; treating delivery as successful"
                                );
                            }
                            catch { }
                            return true;
                        }

                        // The injection itself failed and verification confirmed
                        // nothing landed, so no paste chord reached the target;
                        // trying the next method (including the plain-text
                        // Ctrl+Shift+V last resort) cannot duplicate the text.
                        try
                        {
                            Logger.Log(
                                $"{method}: injection failed and verification confirmed no delivery; trying the next method"
                            );
                        }
                        catch { }
                        await Task.Delay(50).ConfigureAwait(true);
                        continue;
                    }
                }
                else if (success)
                {
                    // Unverified path: trust the injection as past builds did.
                    // SendInput may safely fall back to SendKeys when it
                    // injects zero events. That helper logs the exact method,
                    // so do not attribute the result to SendInput here.
                    if (method != SendInputPasteMethod)
                    {
                        Logger.Log($"Paste successful with {method}");
                    }
                    return true;
                }

                if (method == SendInputPasteMethod && !supportsNativePaste && valueBefore == null)
                {
                    // TryPasteSendInputAsync owns the zero-event fallback so
                    // it can distinguish zero delivery from partial delivery.
                    return false;
                }
            }
            catch (Exception ex)
            {
                try
                {
                    Logger.LogWarning($"{method} failed: {ex.GetType().Name}: {ex.Message}");
                }
                catch { }
            }

            // For browser/custom editors, never send another paste chord after
            // the first real injection. UIA may report a false negative even
            // after the target accepted the paste, and a fallback chord would
            // duplicate the text. WM_PASTE is exempt because unsupported
            // custom editors return before sending any input.
            if (ShouldStopAfterUnverifiedPasteAttempt(supportsNativePaste, method))
            {
                try
                {
                    Logger.LogWarning(
                        "Unverified custom-editor paste attempt ended; no additional paste method will be tried"
                    );
                }
                catch { }
                return false;
            }

            // Brief delay between methods
            await Task.Delay(50).ConfigureAwait(true);
        }

        return false;
    }

    private async System.Threading.Tasks.Task<bool> TryPasteWindowMessageAsync()
    {
        try
        {
            var foregroundWindow = NativeMethods.GetForegroundWindow();
            if (foregroundWindow == IntPtr.Zero)
            {
                return false;
            }

            var targetWindow = ResolveFocusHwnd(foregroundWindow);
            if (targetWindow == IntPtr.Zero)
            {
                targetWindow = foregroundWindow;
            }

            if (!SupportsWindowMessagePaste(targetWindow))
            {
                try
                {
                    Logger.Log(
                        $"TryPasteWindowMessageAsync: Skipping unsupported target {DescribeWindow(targetWindow)}"
                    );
                }
                catch { }
                return false;
            }

            if ((GetWindowLongPtr(targetWindow, GWL_STYLE).ToInt64() & ES_READONLY) != 0)
            {
                try
                {
                    Logger.Log(
                        $"TryPasteWindowMessageAsync: Skipping read-only target {DescribeWindow(targetWindow)}"
                    );
                }
                catch { }
                return false;
            }

            try
            {
                Logger.Log(
                    $"TryPasteWindowMessageAsync: Sending WM_PASTE to {DescribeWindow(targetWindow)}"
                );
            }
            catch { }

            return await VerifyPasteDeliveryAsync(
                targetWindow,
                () =>
                {
                    NormalizeInputState();
                    TrySendForeignMessage(
                        targetWindow,
                        WM_PASTE,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        ForeignActionTimeoutMs,
                        out _
                    );
                }
            );
        }
        catch
        {
            return false;
        }
    }

    private static bool TrySendForeignMessage(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam,
        uint timeoutMs,
        out IntPtr result
    )
    {
        result = IntPtr.Zero;
        try
        {
            return SendMessageTimeout(
                    hWnd,
                    msg,
                    wParam,
                    lParam,
                    SMTO_ABORTIFHUNG,
                    timeoutMs,
                    out result
                ) != IntPtr.Zero;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetWindowTextLength(IntPtr hWnd, out int length)
    {
        length = 0;
        if (
            !TrySendForeignMessage(
                hWnd,
                WM_GETTEXTLENGTH,
                IntPtr.Zero,
                IntPtr.Zero,
                ForeignQueryTimeoutMs,
                out var result
            )
        )
        {
            return false;
        }

        length = (int)result;
        return true;
    }

    private static bool TryGetNativeSelection(IntPtr hWnd, out int selStart, out int selEnd)
    {
        selStart = 0;
        selEnd = 0;
        try
        {
            return SendMessageTimeout(
                    hWnd,
                    EM_GETSEL,
                    out selStart,
                    out selEnd,
                    SMTO_ABORTIFHUNG,
                    ForeignQueryTimeoutMs,
                    out _
                ) != IntPtr.Zero;
        }
        catch
        {
            return false;
        }
    }

    private static async System.Threading.Tasks.Task<bool> VerifyPasteDeliveryAsync(
        IntPtr targetWindow,
        Action pasteAction
    )
    {
        if (!TryGetWindowTextLength(targetWindow, out int lengthBefore))
        {
            // The target is not answering messages, so pasting into it or
            // reading the result back would only pile onto a hung queue.
            // Nothing has been injected yet, so a later method may retry.
            try
            {
                Logger.LogWarning(
                    "Paste verification skipped: target did not answer WM_GETTEXTLENGTH"
                );
            }
            catch { }
            return false;
        }

        bool hadSelection =
            TryGetNativeSelection(targetWindow, out int selStartBefore, out int selEndBefore)
            && selEndBefore > selStartBefore;

        pasteAction();
        await Task.Delay(75).ConfigureAwait(true);

        if (!TryGetWindowTextLength(targetWindow, out int lengthAfter))
        {
            // The paste chord was already sent. Treating a lost read-back as a
            // failure would trigger another paste method and risk duplicating
            // the text, so trust the injection.
            try
            {
                Logger.LogWarning(
                    "Paste verification inconclusive after injection; treating the attempt as delivered to avoid a duplicate paste"
                );
            }
            catch { }
            return true;
        }

        // Pasting over a highlighted selection can shrink or preserve the
        // total length (refined text is often shorter than the selection it
        // replaces), so any length change counts as delivered. An equal-length
        // replacement is detected by the selection collapsing or moving.
        bool delivered = lengthAfter != lengthBefore;
        if (!delivered && hadSelection)
        {
            if (TryGetNativeSelection(targetWindow, out int selStartAfter, out int selEndAfter))
            {
                delivered =
                    selStartAfter == selEndAfter
                    || selStartAfter != selStartBefore
                    || selEndAfter != selEndBefore;
            }
            else
            {
                delivered = true;
            }
        }

        if (!delivered)
        {
            try
            {
                Logger.LogWarning(
                    $"Paste verification failed: target text unchanged ({lengthBefore}->{lengthAfter})"
                );
            }
            catch { }
        }

        return delivered;
    }

    private async System.Threading.Tasks.Task<bool> TryPasteCtrlVAsync()
    {
        try
        {
            var targetWindow = ResolvePasteTarget();
            if (SupportsWindowMessagePaste(targetWindow))
            {
                return await VerifyPasteDeliveryAsync(
                    targetWindow,
                    () =>
                    {
                        NormalizeInputState();
                        SendKeys.SendWait("^v");
                    }
                );
            }

            NormalizeInputState();
            SendKeys.SendWait("^v");
            await Task.Delay(75).ConfigureAwait(true);
            Logger.LogWarning(
                "Custom-editor SendKeys Ctrl+V completed without generic verification"
            );
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async System.Threading.Tasks.Task<bool> TryPasteShiftInsertAsync()
    {
        try
        {
            var targetWindow = ResolvePasteTarget();
            if (SupportsWindowMessagePaste(targetWindow))
            {
                return await VerifyPasteDeliveryAsync(
                    targetWindow,
                    () =>
                    {
                        NormalizeInputState();
                        SendKeys.SendWait("+{INSERT}");
                    }
                );
            }

            NormalizeInputState();
            SendKeys.SendWait("+{INSERT}");
            await Task.Delay(75).ConfigureAwait(true);
            return false;
        }
        catch
        {
            return false;
        }
    }

    private async System.Threading.Tasks.Task<bool> TryPasteCtrlShiftVAsync()
    {
        try
        {
            var targetWindow = ResolvePasteTarget();
            if (SupportsWindowMessagePaste(targetWindow))
            {
                return await VerifyPasteDeliveryAsync(
                    targetWindow,
                    () =>
                    {
                        NormalizeInputState();
                        SendKeys.SendWait("^+v");
                    }
                );
            }

            // Paste-as-plain-text is the last-resort method for custom
            // editors; use the same split-batch virtual-key chord as Ctrl+V so
            // the injection mechanism matches what the UIA verification is
            // testing.
            return await TryPasteSendInputChordAsync(
                new ushort[]
                {
                    0x11 /*CTRL*/
                    ,
                    0x10, /*SHIFT*/
                },
                0x56 /*'V'*/
                ,
                "^+v",
                "Ctrl+Shift+V"
            );
        }
        catch
        {
            return false;
        }
    }

    private async System.Threading.Tasks.Task<bool> TryPasteSendInputAsync(
        IntPtr expectedForegroundWindow
    )
    {
        try
        {
            var targetWindow = ResolvePasteTarget();
            ushort[] modifiers =
            {
                0x11, /*CTRL*/
            };
            uint expectedInputEvents = (uint)(modifiers.Length + 3);

            void PasteWithSendInput()
            {
                NormalizeInputState();
                uint sent = SendChordScancode(
                    modifiers,
                    0x56 /*'V'*/
                );
                if (sent != expectedInputEvents)
                {
                    throw new InvalidOperationException(
                        $"SendInput rejected the Ctrl+V chord (sent={sent}/{expectedInputEvents})"
                    );
                }
            }

            bool supportsNativePaste = SupportsWindowMessagePaste(targetWindow);
            if (supportsNativePaste)
            {
                return await VerifyPasteDeliveryAsync(targetWindow, PasteWithSendInput);
            }

            if (
                expectedForegroundWindow != IntPtr.Zero
                && NativeMethods.GetForegroundWindow() != expectedForegroundWindow
            )
            {
                Logger.LogWarning(
                    "Paste target changed while waiting for input; leaving text on the clipboard"
                );
                return false;
            }

            IntPtr focusedTarget = ResolvePasteTarget();
            if (targetWindow != IntPtr.Zero && focusedTarget != targetWindow)
            {
                Logger.LogWarning(
                    "Paste focus changed while waiting for input; leaving text on the clipboard"
                );
                return false;
            }

            Logger.Log(
                $"SendInput paste target: foreground=0x{expectedForegroundWindow.ToInt64():X}, focused=0x{targetWindow.ToInt64():X}"
            );

            // Browsers and other custom editors have historically dropped a
            // Ctrl+V chord whose keydown and keyup arrive in one atomic
            // SendInput batch. Use a virtual-key chord split across two batches
            // with a delay for every custom editor, and let the caller's UIA
            // verification decide whether it landed.
            return await TryPasteSendInputChordAsync(
                new ushort[]
                {
                    0x11, /*CTRL*/
                },
                0x56 /*'V'*/
                ,
                "^v",
                "Ctrl+V"
            );
        }
        catch (Exception ex)
        {
            try
            {
                Logger.LogWarning(
                    $"TryPasteSendInputAsync failed: {ex.GetType().Name}: {ex.Message}"
                );
            }
            catch { }
            return false;
        }
    }

    private static IntPtr ResolvePasteTarget()
    {
        var foregroundWindow = NativeMethods.GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var targetWindow = ResolveFocusHwnd(foregroundWindow);
        return targetWindow != IntPtr.Zero ? targetWindow : foregroundWindow;
    }

    private static bool SupportsWindowMessagePaste(IntPtr hWnd)
    {
        try
        {
            var className = new StringBuilder(128);
            if (GetClassName(hWnd, className, className.Capacity) <= 0)
            {
                return false;
            }

            var cls = className.ToString();
            if (string.IsNullOrWhiteSpace(cls))
            {
                return false;
            }

            return cls.Equals("Edit", StringComparison.OrdinalIgnoreCase)
                || cls.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase)
                || cls.StartsWith("RICHEDIT", StringComparison.OrdinalIgnoreCase)
                || cls.StartsWith("WindowsForms10.EDIT", StringComparison.OrdinalIgnoreCase)
                || cls.StartsWith("Scintilla", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static UiaProbeInvocationResult TryGetSelectionViaUiaProbe(
        UiaProbeMode mode,
        IntPtr foregroundWindow,
        int timeoutMs,
        string label
    )
    {
        var result = UiaProbeClient.TryGetSelection(mode, foregroundWindow, timeoutMs);
        if (!result.ContinueAttempts && !string.IsNullOrWhiteSpace(result.FailureReason))
        {
            try
            {
                Logger.LogWarning($"{label} helper failed: {result.FailureReason}");
            }
            catch { }
        }

        return result;
    }

    private static string? TryGetSelectionViaWin32(IntPtr hwndForeground)
    {
        try
        {
            var ctrl = ResolveFocusHwnd(hwndForeground);
            if (ctrl == IntPtr.Zero)
                return null;
            if (!TryGetWindowTextLength(ctrl, out int len) || len <= 0)
                return null;
            if (!TryGetNativeSelection(ctrl, out int selStart, out int selEnd))
                return null;
            if (selEnd <= selStart)
                return null;
            var sb = new StringBuilder(len + 1);
            try
            {
                SendMessageTimeout(
                    ctrl,
                    WM_GETTEXT,
                    (IntPtr)sb.Capacity,
                    sb,
                    SMTO_ABORTIFHUNG,
                    ForeignQueryTimeoutMs,
                    out _
                );
            }
            catch { }
            var value = sb.ToString();
            if (string.IsNullOrEmpty(value))
                return null;
            if (selStart < 0 || selEnd > value.Length)
                return null;
            var slice = value.Substring(selStart, Math.Min(selEnd, value.Length) - selStart);
            return string.IsNullOrWhiteSpace(slice) ? null : slice;
        }
        catch
        {
            return null;
        }
    }

    private static IntPtr ResolveFocusHwnd(IntPtr hwndForeground)
    {
        try
        {
            var info = new NativeMethods.GUITHREADINFO
            {
                cbSize = Marshal.SizeOf<NativeMethods.GUITHREADINFO>(),
            };
            uint threadId = NativeMethods.GetWindowThreadProcessId(hwndForeground, out uint _);
            if (threadId != 0 && NativeMethods.GetGUIThreadInfo(threadId, ref info))
            {
                if (info.hwndFocus != IntPtr.Zero)
                    return info.hwndFocus;
                if (info.hwndActive != IntPtr.Zero)
                    return info.hwndActive;
            }
            // Fallback: attach to target thread and query GetFocus directly
            uint currentTid = GetCurrentThreadId();
            if (threadId != 0 && currentTid != threadId)
            {
                try
                {
                    if (AttachThreadInput(currentTid, threadId, true))
                    {
                        try
                        {
                            var f = GetFocus();
                            if (f != IntPtr.Zero)
                                return f;
                        }
                        finally
                        {
                            AttachThreadInput(currentTid, threadId, false);
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
        return hwndForeground;
    }

    private enum CopyMethod
    {
        CtrlC,
        WmCopy,
        CtrlInsert,
        CtrlShiftC,
        SendKeysCtrlC,
        DoubleCtrlC,
        MenuAltEC,
    }

    private bool TryCopyAndRead(
        IntPtr hwnd,
        uint seqBefore,
        CopyMethod method,
        out string result,
        ref bool clipboardChanged,
        int timeoutMs = 1200
    )
    {
        result = string.Empty;

        try
        {
            try
            {
                Logger.Log(
                    $"TryCopyAndRead start: method={method}, seqBefore={seqBefore}, timeoutMs={timeoutMs}"
                );
            }
            catch { }
            if (hwnd != IntPtr.Zero)
            {
                try
                {
                    try
                    {
                        Logger.Log(
                            $"TryCopyAndRead: Preparing window 0x{hwnd.ToInt64():X} for copy"
                        );
                    }
                    catch { }
                    if (IsIconic(hwnd))
                    {
                        try
                        {
                            Logger.Log($"TryCopyAndRead: Restoring minimized window");
                        }
                        catch { }
                        ShowWindow(hwnd, SW_RESTORE);
                    }
                    try
                    {
                        Logger.Log($"TryCopyAndRead: Bringing window to top");
                    }
                    catch { }
                    BringWindowToTop(hwnd);
                    try
                    {
                        Logger.Log($"TryCopyAndRead: Setting window as foreground");
                    }
                    catch { }
                    SetForegroundWindow(hwnd);
                }
                catch (Exception ex)
                {
                    try
                    {
                        Logger.LogWarning(
                            $"TryCopyAndRead: Window preparation failed: {ex.GetType().Name}: {ex.Message}"
                        );
                    }
                    catch { }
                }
                try
                {
                    Logger.Log($"TryCopyAndRead: Waiting 60ms for window to settle");
                }
                catch { }
                try
                {
                    Thread.Sleep(60);
                }
                catch { }
            }
            else
            {
                try
                {
                    Logger.Log($"TryCopyAndRead: No window handle (hwnd=0)");
                }
                catch { }
            }
            IntPtr targetForMessage = hwnd;
            if (method == CopyMethod.WmCopy)
            {
                var focused = ResolveFocusHwnd(hwnd);
                if (focused != IntPtr.Zero)
                    targetForMessage = focused;
                try
                {
                    Logger.Log($"WM_COPY target hwnd: {DescribeWindow(targetForMessage)}");
                }
                catch { }
            }
            switch (method)
            {
                case CopyMethod.CtrlC:
                    NormalizeInputState();
                    _ = SendChordScancode(
                        new ushort[]
                        {
                            0x11, /*VK_CONTROL*/
                        },
                        0x43 /*'C'*/
                    );
                    break;
                case CopyMethod.WmCopy:
                    TrySendForeignMessage(
                        targetForMessage,
                        WM_COPY,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        ForeignActionTimeoutMs,
                        out _
                    );
                    break;
                case CopyMethod.CtrlInsert:
                    NormalizeInputState();
                    _ = SendChordScancode(
                        new ushort[]
                        {
                            0x11, /*VK_CONTROL*/
                        },
                        0x2D /*VK_INSERT*/
                    );
                    break;
                case CopyMethod.CtrlShiftC:
                    NormalizeInputState();
                    _ = SendChordScancode(
                        new ushort[]
                        {
                            0x11 /*CTRL*/
                            ,
                            0x10, /*SHIFT*/
                        },
                        0x43 /*'C'*/
                    );
                    break;
                case CopyMethod.SendKeysCtrlC:
                    try
                    {
                        int perAttempt = Math.Max(200, timeoutMs / 3);
                        for (int i = 0; i < 3; i++)
                        {
                            try
                            {
                                SendKeys.SendWait("^c");
                                SendKeys.Flush();
                            }
                            catch { }
                            Thread.Sleep(60);
                            var t = WaitForClipboardTextChange(
                                seqBefore,
                                perAttempt,
                                out bool changedForAttempt
                            );
                            clipboardChanged |= changedForAttempt;
                            if (!string.IsNullOrWhiteSpace(t))
                            {
                                result = t!;
                                try
                                {
                                    Logger.Log(
                                        $"TryCopyAndRead success after {i + 1} SendKeys attempts: len={t.Length}"
                                    );
                                }
                                catch { }
                                return true;
                            }
                        }
                    }
                    catch { }
                    // The per-attempt waits above already spent the timeout
                    // budget; falling through to the shared wait below would
                    // double it for the same unchanged clipboard sequence.
                    try
                    {
                        Logger.Log($"TryCopyAndRead no change: method={method}");
                    }
                    catch { }
                    return false;
                case CopyMethod.DoubleCtrlC:
                    NormalizeInputState();
                    _ = SendChordScancode(new ushort[] { 0x11 }, 0x43);
                    Thread.Sleep(120);
                    _ = SendChordScancode(new ushort[] { 0x11 }, 0x43);
                    break;
                case CopyMethod.MenuAltEC:
                    NormalizeInputState();
                    // Alt+E open Edit menu, then 'C' for Copy
                    _ = SendChordScancode(
                        new ushort[]
                        {
                            0x12, /*ALT*/
                        },
                        0x45 /*'E'*/
                    );
                    Thread.Sleep(150);
                    _ = SendChordScancode(
                        Array.Empty<ushort>(),
                        0x43 /*'C'*/
                    );
                    break;
            }
            var text = WaitForClipboardTextChange(seqBefore, timeoutMs, out bool changed);
            clipboardChanged |= changed;
            if (!string.IsNullOrWhiteSpace(text))
            {
                result = text!;
                try
                {
                    Logger.Log($"TryCopyAndRead success: method={method}, len={text.Length}");
                }
                catch { }
                return true;
            }
            else
            {
                try
                {
                    Logger.Log($"TryCopyAndRead no change: method={method}");
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            try
            {
                Logger.LogWarning(
                    $"TryCopyAndRead({method}) error: {ex.GetType().Name}: {ex.Message}"
                );
            }
            catch { }
        }
        return false;
    }

    internal static bool IsClipboardSequenceChanged(uint sequenceBefore, uint sequenceAfter)
    {
        return sequenceAfter != 0 && sequenceAfter != sequenceBefore;
    }

    private static string? WaitForClipboardTextChange(
        uint seqBefore,
        int timeoutMs,
        out bool clipboardChanged
    )
    {
        clipboardChanged = false;
        var start = Environment.TickCount;
        int changedAt = 0;
        while (Environment.TickCount - start < timeoutMs)
        {
            uint seqNow = 0;
            try
            {
                seqNow = GetClipboardSequenceNumber();
            }
            catch { }
            if (IsClipboardSequenceChanged(seqBefore, seqNow))
            {
                if (!clipboardChanged)
                {
                    clipboardChanged = true;
                    changedAt = Environment.TickCount;
                }
                try
                {
                    if (Clipboard.ContainsText())
                    {
                        var txt = Clipboard.GetText(TextDataFormat.UnicodeText);
                        if (!string.IsNullOrWhiteSpace(txt))
                            return txt;
                    }
                }
                catch { }

                // The clipboard changed but holds no text (files, an image,
                // or an owner mid-update). Allow a short grace for the owner
                // to publish text, then stop burning the remaining timeout.
                if (Environment.TickCount - changedAt >= 200)
                    return null;
            }
            Thread.Sleep(25);
        }
        return null;
    }

    private static uint SendChordScancode(ushort[] modifiersVk, ushort keyVk)
    {
        ushort VK_INSERT = 0x2D;
        var inputs = new System.Collections.Generic.List<INPUT>();
        // Modifiers down (use scancodes)
        foreach (var vk in modifiersVk)
        {
            uint sc = MapVirtualKey(vk, MAPVK_VK_TO_VSC);
            inputs.Add(
                new INPUT
                {
                    type = INPUT_KEYBOARD,
                    U = new INPUTUNION
                    {
                        ki = new KEYBDINPUT
                        {
                            wVk = 0,
                            wScan = (ushort)sc,
                            dwFlags = KEYEVENTF_SCANCODE,
                        },
                    },
                }
            );
        }
        // Key down
        uint scKey = MapVirtualKey(keyVk, MAPVK_VK_TO_VSC);
        uint flagsDown = KEYEVENTF_SCANCODE;
        if (keyVk == VK_INSERT)
            flagsDown |= 0x0001; // EXTENDEDKEY
        inputs.Add(
            new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = (ushort)scKey,
                        dwFlags = flagsDown,
                    },
                },
            }
        );
        // Key up
        uint flagsUp = KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP;
        if (keyVk == VK_INSERT)
            flagsUp |= 0x0001;
        inputs.Add(
            new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = (ushort)scKey,
                        dwFlags = flagsUp,
                    },
                },
            }
        );
        // Modifiers up (reverse)
        for (int i = modifiersVk.Length - 1; i >= 0; i--)
        {
            uint sc = MapVirtualKey(modifiersVk[i], MAPVK_VK_TO_VSC);
            inputs.Add(
                new INPUT
                {
                    type = INPUT_KEYBOARD,
                    U = new INPUTUNION
                    {
                        ki = new KEYBDINPUT
                        {
                            wVk = 0,
                            wScan = (ushort)sc,
                            dwFlags = KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP,
                        },
                    },
                }
            );
        }
        uint sent = 0;
        int lastError = 0;
        try
        {
            sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
            lastError = Marshal.GetLastWin32Error();
        }
        catch (Exception ex)
        {
            try
            {
                Logger.LogWarning($"SendInput exception: {ex.GetType().Name}: {ex.Message}");
            }
            catch { }
        }

        if (sent != inputs.Count)
        {
            try
            {
                Logger.LogWarning(
                    $"SendInput Ctrl+V incomplete: sent={sent}/{inputs.Count}, win32Error={lastError}"
                );
            }
            catch { }
        }

        if (sent > 0 && sent < inputs.Count)
        {
            // SendInput can stop between events. Release any chord keys whose
            // key-down events may have been accepted so a partial paste cannot
            // leave Ctrl or V logically held for the next operation.
            var cleanup = new System.Collections.Generic.List<INPUT>();
            int modifierCount = modifiersVk.Length;
            if (sent == modifierCount + 1)
            {
                cleanup.Add(
                    new INPUT
                    {
                        type = INPUT_KEYBOARD,
                        U = new INPUTUNION
                        {
                            ki = new KEYBDINPUT
                            {
                                wVk = 0,
                                wScan = (ushort)scKey,
                                dwFlags = flagsUp,
                            },
                        },
                    }
                );
            }

            int acceptedModifiers = (int)Math.Min(sent, (uint)modifierCount);
            for (int i = acceptedModifiers - 1; i >= 0; i--)
            {
                uint sc = MapVirtualKey(modifiersVk[i], MAPVK_VK_TO_VSC);
                cleanup.Add(
                    new INPUT
                    {
                        type = INPUT_KEYBOARD,
                        U = new INPUTUNION
                        {
                            ki = new KEYBDINPUT
                            {
                                wVk = 0,
                                wScan = (ushort)sc,
                                dwFlags = KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP,
                            },
                        },
                    }
                );
            }

            try
            {
                uint cleanupSent = SendInput(
                    (uint)cleanup.Count,
                    cleanup.ToArray(),
                    Marshal.SizeOf<INPUT>()
                );
                if (cleanupSent != cleanup.Count)
                {
                    Logger.LogWarning(
                        $"SendInput cleanup incomplete: sent={cleanupSent}/{cleanup.Count}, win32Error={Marshal.GetLastWin32Error()}"
                    );
                }
            }
            catch (Exception ex)
            {
                try
                {
                    Logger.LogWarning(
                        $"SendInput cleanup exception: {ex.GetType().Name}: {ex.Message}"
                    );
                }
                catch { }
            }
        }

        Thread.Sleep(30); // Reduced from 80ms for faster response
        return sent;
    }

    private enum PasteChordInjection
    {
        AllEventsSent,
        ZeroEventsSent,
        PartialDelivery,
    }

    private static PasteChordInjection SendPasteChordVk(ushort[] modifiers, ushort key)
    {
        // Browsers and other custom editors are known to drop a chord whose
        // keydown and keyup arrive in one atomic SendInput batch: the target's
        // input pipeline can miss the keydown before the keyup is already
        // queued. Split the chord into two batches (downs, then ups after a
        // delay) so the keydown is observed first. Virtual key codes are used
        // because browsers match shortcuts on the VK of the key messages; the
        // older scancode-only batch chord is what was observed to fail.
        int size = Marshal.SizeOf<INPUT>();

        INPUT KeyDown(ushort vk) =>
            new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new INPUTUNION
                {
                    ki = new KEYBDINPUT { wVk = vk, wScan = 0 },
                },
            };

        INPUT KeyUp(ushort vk) =>
            new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = vk,
                        wScan = 0,
                        dwFlags = KEYEVENTF_KEYUP,
                    },
                },
            };

        var downBatch = new INPUT[modifiers.Length + 1];
        for (int i = 0; i < modifiers.Length; i++)
        {
            downBatch[i] = KeyDown(modifiers[i]);
        }
        downBatch[modifiers.Length] = KeyDown(key);

        uint sentDown = SendInput((uint)downBatch.Length, downBatch, size);
        if (sentDown == 0)
        {
            return PasteChordInjection.ZeroEventsSent;
        }

        if (sentDown < downBatch.Length)
        {
            // The first `sentDown` key-downs were accepted; release them in
            // reverse so no chord key can stick.
            var acceptedDowns = new ushort[(int)sentDown];
            for (int i = 0; i < acceptedDowns.Length; i++)
            {
                acceptedDowns[i] = downBatch[i].U.ki.wVk;
            }
            ReleaseKeys(acceptedDowns, size);
            return PasteChordInjection.PartialDelivery;
        }

        Thread.Sleep(60);

        var upBatch = new INPUT[modifiers.Length + 1];
        upBatch[0] = KeyUp(key);
        for (int i = 0; i < modifiers.Length; i++)
        {
            upBatch[i + 1] = KeyUp(modifiers[i]);
        }

        uint sentUp = SendInput((uint)upBatch.Length, upBatch, size);
        if (sentUp == upBatch.Length)
        {
            return PasteChordInjection.AllEventsSent;
        }

        // Key-ups that were not accepted leave their keys logically held;
        // release the remainder in reverse.
        var heldVks = new ushort[upBatch.Length - (int)sentUp];
        for (int i = 0; i < heldVks.Length; i++)
        {
            heldVks[i] = upBatch[(int)sentUp + i].U.ki.wVk;
        }
        ReleaseKeys(heldVks, size);
        return PasteChordInjection.PartialDelivery;
    }

    private static void ReleaseKeys(ushort[] heldVks, int size)
    {
        if (heldVks.Length == 0)
        {
            return;
        }

        // Key-ups in reverse order of the held keys.
        var inputs = new INPUT[heldVks.Length];
        for (int i = 0; i < heldVks.Length; i++)
        {
            inputs[i] = new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = heldVks[heldVks.Length - 1 - i],
                        wScan = 0,
                        dwFlags = KEYEVENTF_KEYUP,
                    },
                },
            };
        }

        try
        {
            SendInput((uint)inputs.Length, inputs, size);
        }
        catch { }
    }

    private async System.Threading.Tasks.Task<bool> TryPasteSendInputChordAsync(
        ushort[] modifiers,
        ushort key,
        string sendKeysSpec,
        string label
    )
    {
        var chordResult = SendPasteChordVk(modifiers, key);
        if (chordResult == PasteChordInjection.ZeroEventsSent)
        {
            Logger.LogWarning(
                $"SendInput injected zero events for the custom editor ({label}); trying one SendKeys {label} fallback"
            );
            NormalizeInputState();
            SendKeys.SendWait(sendKeysSpec);
            await Task.Delay(75).ConfigureAwait(true);
            Logger.LogWarning($"SendKeys {label} fallback completed for the custom editor");
            Logger.Log($"Paste delivered with SendKeys {label} fallback");
            return true;
        }

        if (chordResult == PasteChordInjection.PartialDelivery)
        {
            Logger.LogWarning(
                $"SendInput {label} partially delivered for the custom editor; leaving text on the clipboard"
            );
            return false;
        }

        await Task.Delay(75).ConfigureAwait(true);
        Logger.LogWarning(
            $"SendInput {label} was injected into the custom editor; delivery pending verification"
        );
        return true;
    }

    private static async System.Threading.Tasks.Task<string?> PollForPasteDeliveryAsync(
        string valueBefore,
        string? clipboardText,
        IntPtr expectedWindow
    )
    {
        // Browsers commit a paste asynchronously in the renderer process, so a
        // single read shortly after injection can miss a commit that lands a
        // few hundred milliseconds later and falsely report "not delivered",
        // which then moves on and duplicates the paste. Poll a few times,
        // stopping at the first read that shows the paste. The poll is also a
        // safety net for the previous method's late commit: any change stops
        // the loop, so a slow paste from method N is noticed while verifying
        // method N+1 and never duplicated.
        string? latest = await TryReadFocusedValueAsync(expectedWindow);
        if (latest == null)
        {
            return null;
        }

        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (IsPasteDelivered(valueBefore, latest, clipboardText))
            {
                return latest;
            }

            await Task.Delay(150).ConfigureAwait(true);
            latest = await TryReadFocusedValueAsync(expectedWindow);
            if (latest == null)
            {
                return null;
            }
        }

        return latest;
    }

    private static async System.Threading.Tasks.Task<string?> TryReadFocusedValueAsync(
        IntPtr expectedWindow
    )
    {
        var foregroundWindow = NativeMethods.GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
        {
            return null;
        }

        // The before/after values must come from the same window tree the
        // paste targeted. If focus moved between reads, comparing values from
        // different windows would report a spurious "delivered" result, so
        // treat the verification as lost instead.
        if (expectedWindow != IntPtr.Zero && foregroundWindow != expectedWindow)
        {
            try
            {
                Logger.LogWarning(
                    "TryReadFocusedValueAsync: foreground window changed; paste verification lost"
                );
            }
            catch { }
            return null;
        }

        return await System
            .Threading.Tasks.Task.Run(() =>
            {
                var (found, value) = UiaProbeClient.TryGetControlValue(foregroundWindow, 1200);
                return found ? value : null;
            })
            .ConfigureAwait(true);
    }

    private static string? TryReadClipboardText()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                string? text = Clipboard.GetText(TextDataFormat.UnicodeText);
                return string.IsNullOrEmpty(text) ? null : text;
            }
        }
        catch { }
        return null;
    }

    private const char ValueSeparator = '\u0000';

    internal static bool IsPasteDelivered(
        string? joinedBefore,
        string? joinedAfter,
        string? clipboardText
    )
    {
        if (joinedBefore == null || joinedAfter == null)
        {
            return false;
        }

        if (string.Equals(joinedAfter, joinedBefore, StringComparison.Ordinal))
        {
            return false;
        }

        // The probe returns the values of every Edit control in the target
        // window, joined by a separator, so a change in ANY of them counts as
        // delivery (the paste target may not be the first Edit in tree order).
        string[] before = joinedBefore.Split(ValueSeparator);
        string[] after = joinedAfter.Split(ValueSeparator);
        int count = Math.Min(before.Length, after.Length);
        for (int i = 0; i < count; i++)
        {
            if (IsSingleEditDelivered(before[i], after[i], clipboardText))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSingleEditDelivered(
        string valueBefore,
        string valueAfter,
        string? clipboardText
    )
    {
        if (string.Equals(valueAfter, valueBefore, StringComparison.Ordinal))
        {
            return false;
        }

        // The pasted text is the strongest signal: it also covers inserts that
        // replace an existing selection, where the value may not grow.
        if (
            !string.IsNullOrEmpty(clipboardText)
            && valueAfter.Contains(clipboardText, StringComparison.Ordinal)
        )
        {
            return true;
        }

        // Browsers may normalize pasted text (trim whitespace, strip trailing
        // newlines), so a strict containment match can false-negative even when
        // the paste landed. Any length change right after a paste injection is
        // treated as delivery: a false positive only stops retrying (the text
        // stays on the clipboard), while a false negative would retry and
        // duplicate the paste.
        return valueAfter.Length != valueBefore.Length;
    }

    private static void NormalizeInputState()
    {
        try
        {
            // Release potentially held modifiers from the hotkey (Ctrl/Alt/Shift/Win)
            ushort[] mods = new ushort[]
            {
                0x11 /*CTRL*/
                ,
                0x12 /*ALT*/
                ,
                0x10 /*SHIFT*/
                ,
                0x5B /*LWIN*/
                ,
                0x5C, /*RWIN*/
            };
            var inputs = new System.Collections.Generic.List<INPUT>();
            foreach (var m in mods)
            {
                bool down = (GetAsyncKeyState(m) & 0x8000) != 0;
                if (down)
                {
                    inputs.Add(
                        new INPUT
                        {
                            type = INPUT_KEYBOARD,
                            U = new INPUTUNION
                            {
                                ki = new KEYBDINPUT { wVk = m, dwFlags = KEYEVENTF_KEYUP },
                            },
                        }
                    );
                }
            }
            if (inputs.Count > 0)
            {
                try
                {
                    SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
                }
                catch { }
                Thread.Sleep(20); // Reduced from 40ms
            }
        }
        catch { }
    }

    private static void LogPasteDiagnostic(string prefix)
    {
        try
        {
            var fw = NativeMethods.GetForegroundWindow();
            var windowInfo = fw != IntPtr.Zero ? DescribeWindow(fw) : "no foreground window";

            bool ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0;
            bool alt = (GetAsyncKeyState(0x12) & 0x8000) != 0;
            bool shift = (GetAsyncKeyState(0x10) & 0x8000) != 0;
            bool win =
                (GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0;

            Logger.Log(
                $"[{prefix}] Foreground: {windowInfo}, Modifiers: Ctrl={ctrl}, Alt={alt}, Shift={shift}, Win={win}"
            );
        }
        catch { }
    }

    public System.Threading.Tasks.Task<string> CaptureSelectionOrClipboardAsync(
        bool useClipboardFallback = false,
        IntPtr? targetWindow = null
    )
    {
        return RunInSta(() => CaptureSelectionOrClipboard(useClipboardFallback, targetWindow));
    }

    private static void RecordCaptureSuccess(string method, bool success)
    {
        string key = success ? $"{method}_success" : $"{method}_fail";
        string? statsSnapshot = null;
        lock (_captureStatsLock)
        {
            _captureStats.TryGetValue(key, out int count);
            _captureStats[key] = count + 1;

            // Log stats every 10 captures
            if ((_captureStats.Values.Sum() % 10) == 0)
            {
                statsSnapshot = string.Join(
                    ", ",
                    _captureStats.Select(kvp => $"{kvp.Key}={kvp.Value}")
                );
            }
        }

        if (statsSnapshot != null)
        {
            try
            {
                Logger.Log($"Capture stats: {statsSnapshot}");
            }
            catch { }
        }
    }

    private static System.Threading.Tasks.Task<T> RunInSta<T>(Func<T> func)
    {
        var tcs = new System.Threading.Tasks.TaskCompletionSource<T>();
        var th = new Thread(() =>
        {
            try
            {
                var r = func();
                tcs.SetResult(r);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        th.IsBackground = true;
        th.SetApartmentState(ApartmentState.STA); // Use STA for clipboard operations (required for System.Windows.Forms.Clipboard)
        th.Start();
        return tcs.Task;
    }

    private static System.Threading.Tasks.Task<T> RunOnUiContextAsync<T>(
        Func<System.Threading.Tasks.Task<T>> func
    )
    {
        if (_uiContext == null || SynchronizationContext.Current == _uiContext)
        {
            return func();
        }

        var tcs = new System.Threading.Tasks.TaskCompletionSource<T>(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously
        );

        _uiContext.Post(
            async _ =>
            {
                try
                {
                    var result = await func().ConfigureAwait(true);
                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            },
            null
        );

        return tcs.Task;
    }
}
