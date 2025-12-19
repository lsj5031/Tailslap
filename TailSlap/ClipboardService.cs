using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

public sealed class ClipboardService : IClipboardService
{
    // Performance metrics
    private static readonly System.Collections.Generic.Dictionary<string, int> _captureStats =
        new();
    private static readonly System.Collections.Generic.Dictionary<
        string,
        IntPtr
    > _windowHandleCache = new();
    private static DateTime _lastCacheClear = DateTime.Now;

    // Events for UI feedback
    public event Action? CaptureStarted;
    public event Action? CaptureEnded;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int SendMessage(IntPtr hWnd, uint Msg, int wParam, StringBuilder lParam);

    [DllImport("user32.dll")]
    private static extern int SendMessage(IntPtr hWnd, uint Msg, out int wParam, out int lParam);

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

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
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    private const uint WM_COPY = 0x0301;
    private const uint WM_GETTEXT = 0x000D;
    private const uint WM_GETTEXTLENGTH = 0x000E;
    private const uint EM_GETSEL = 0x00B0;
    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;
    private const uint MAPVK_VK_TO_VSC = 0x0;
    private const int SW_RESTORE = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left,
            Top,
            Right,
            Bottom;
    }

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
        public KEYBDINPUT ki;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
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
                Logger.Log(
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
                Logger.Log(
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
            uint threadId = GetWindowThreadProcessId(hWnd, out uint pid);
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
                            Logger.Log(
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
                Logger.Log(
                    $"DescribeWindow: GetWindowThreadProcessId failed: {ex.GetType().Name}: {ex.Message}"
                );
            }
            catch { }
            proc = "(pid error)";
        }

        return $"hWnd=0x{hWnd.ToInt64():X}, class={cls}, title={title}, proc={proc}";
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

    // "Canary Probe" to check if UIA is responsive for a window
    private static bool IsUiaResponsive(IntPtr hWnd)
    {
        // Use a very short timeout for the probe
        using var cts = new CancellationTokenSource(50);
        try
        {
            var task = RunInMtaForUIA(
                () =>
                {
                    try
                    {
                        var el = AutomationElement.FromHandle(hWnd);
                        // Just accessing a lightweight property to see if the provider responds
                        var id = el.Current.AutomationId;
                        return true;
                    }
                    catch
                    {
                        return false;
                    }
                },
                cts
            );

            task.Wait(cts.Token);
            return task.Result;
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
                Logger.Log($"[{prefix}] Clipboard state error: {ex.GetType().Name}: {ex.Message}");
            }
            catch { }
        }
    }

    public string CaptureSelectionOrClipboard(bool useClipboardFallback = false)
    {
        var sw = Stopwatch.StartNew();
        ClearCacheIfNeeded();

        try
        {
            Logger.Log(
                $"=== CAPTURE START === ThreadId={Thread.CurrentThread.ManagedThreadId}, Apt={Thread.CurrentThread.GetApartmentState()}, Fallback={useClipboardFallback}"
            );
        }
        catch { }

        // Notify UI to start animation
        CaptureStarted?.Invoke();

        try
        {
            LogClipboardState("Before");
            string? originalClipboard = null;
            try
            {
                try
                {
                    Logger.Log("Step 1: Checking clipboard for text...");
                }
                catch { }
                if (Clipboard.ContainsText())
                {
                    try
                    {
                        Logger.Log("Step 1a: Clipboard contains text, reading...");
                    }
                    catch { }
                    originalClipboard = Clipboard.GetText(TextDataFormat.UnicodeText);
                    try
                    {
                        Logger.Log(
                            $"Step 1b: Original clipboard captured: len={(originalClipboard?.Length ?? 0)}"
                        );
                    }
                    catch { }
                }
                else
                {
                    try
                    {
                        Logger.Log("Step 1a: Clipboard does not contain text");
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                try
                {
                    Logger.Log(
                        $"Step 1 ERROR: Read original clipboard failed: {ex.GetType().Name}: {ex.Message}"
                    );
                }
                catch { }
                try
                {
                    NotificationService.ShowWarning(
                        "Unable to access clipboard. Check if another application is using it."
                    );
                }
                catch { }
            }

            IntPtr foregroundWindow = IntPtr.Zero;
            try
            {
                try
                {
                    Logger.Log("Step 2: Getting foreground window...");
                }
                catch { }
                foregroundWindow = GetForegroundWindow();
                try
                {
                    Logger.Log(
                        $"Step 2a: Foreground window obtained: {DescribeWindow(foregroundWindow)}"
                    );
                }
                catch { }
            }
            catch (Exception ex)
            {
                try
                {
                    Logger.Log(
                        $"Step 2 ERROR: GetForegroundWindow failed: {ex.GetType().Name}: {ex.Message}"
                    );
                }
                catch { }
            }

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
                            Logger.Log(
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
                    Logger.Log(
                        $"Step 3 ERROR: Window analysis failed: {ex.GetType().Name}: {ex.Message}"
                    );
                }
                catch { }
                windowClass = "(analysis error)";
            }

            // 1) Try UI Automation first (but skip for some window classes due to stability issues)
            bool isFirefox = windowClass.Equals("MozillaWindowClass", StringComparison.Ordinal);
            bool isSublime = IsWindowClass(foregroundWindow, "PX_WINDOW_CLASS");
            bool isNotepad = windowClass.Equals("Notepad", StringComparison.Ordinal);
            bool isChrome = windowClass.StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal); // Chrome, Edge, etc.

            // Dynamic Canary Probe: Check if UIA is responsive for others
            // Note: We explicitly skip Chrome because it passes the probe but hangs on deep calls
            bool isUiaResponsive =
                !isFirefox && !isNotepad && !isChrome && IsUiaResponsive(foregroundWindow);

            bool skipUIA = isFirefox || isNotepad || isChrome || !isUiaResponsive;
            try
            {
                Logger.Log(
                    $"Step 3c: Window analysis: isFirefox={isFirefox}, isSublime={isSublime}, isNotepad={isNotepad}, "
                        + $"isChrome={isChrome}, isUiaResponsive={isUiaResponsive}, skipUIA={skipUIA}, useClipboardFallback={useClipboardFallback}"
                );
            }
            catch { }
            if (!skipUIA)
            {
                try
                {
                    try
                    {
                        Logger.Log($"Step 4: Attempting UI Automation for {windowClass}...");
                    }
                    catch { }

                    // Create cancellation token for this UIA attempt
                    using var cts = new CancellationTokenSource();

                    var uia = TryGetSelectionViaUIA(cts);
                    if (!string.IsNullOrWhiteSpace(uia))
                    {
                        RecordCaptureSuccess("UIA", true);
                        try
                        {
                            Logger.Log($"Step 4a: UIA selection captured: len={uia.Length}");
                        }
                        catch { }
                        try
                        {
                            Logger.Log("=== CAPTURE SUCCESS (UIA) ===");
                        }
                        catch { }
                        return uia;
                    }
                    else
                    {
                        RecordCaptureSuccess("UIA", false);
                        try
                        {
                            Logger.Log("Step 4a: UIA selection unavailable or empty");
                        }
                        catch { }
                    }
                    // UIA hit-test near caret as a secondary attempt
                    try
                    {
                        Logger.Log("Step 4b: Attempting UIA FromPoint...");
                    }
                    catch { }

                    using var ctsPt = new CancellationTokenSource();
                    var uiaPt = TryGetSelectionViaUIAFromCaret(ctsPt);
                    if (!string.IsNullOrWhiteSpace(uiaPt))
                    {
                        RecordCaptureSuccess("UIA_FromPoint", true);
                        try
                        {
                            Logger.Log(
                                $"Step 4c: UIA(FromPoint) selection captured: len={uiaPt.Length}"
                            );
                        }
                        catch { }
                        try
                        {
                            Logger.Log("=== CAPTURE SUCCESS (UIA FromPoint) ===");
                        }
                        catch { }
                        return uiaPt;
                    }
                    else
                    {
                        RecordCaptureSuccess("UIA_FromPoint", false);
                        try
                        {
                            Logger.Log("Step 4c: UIA(FromPoint) selection unavailable or empty");
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    try
                    {
                        Logger.Log(
                            $"Step 4 ERROR: UIA selection error: {ex.GetType().Name}: {ex.Message}"
                        );
                    }
                    catch { }
                    // Continue to other methods instead of crashing
                }
            }
            else if (isFirefox)
            {
                try
                {
                    Logger.Log(
                        "Step 4: Skipping UI Automation for Firefox (MozillaWindowClass) to prevent COM crashes - will attempt copy methods"
                    );
                }
                catch { }
            }
            else if (isNotepad)
            {
                try
                {
                    Logger.Log(
                        "Step 4: Skipping UI Automation for Notepad window to avoid potential UIA instability - will attempt copy methods"
                    );
                }
                catch { }
            }
            else if (isChrome)
            {
                try
                {
                    Logger.Log(
                        "Step 4: Skipping UI Automation for Chrome/Edge (Chrome_WidgetWin_*) to prevent UIA crashes - will attempt copy methods"
                    );
                }
                catch { }
            }
            else if (!isUiaResponsive)
            {
                try
                {
                    Logger.Log(
                        "Step 4: Skipping UI Automation because window failed Canary Probe (unresponsive) - will attempt copy methods"
                    );
                }
                catch { }
            }

            // 1b) UIA deep search (caret point and subtree scan) - skip for blacklisted windows
            if (!skipUIA)
            {
                try
                {
                    using var ctsDeep = new CancellationTokenSource();
                    var uiaDeep = TryGetSelectionViaUIADeep(foregroundWindow, ctsDeep);
                    if (!string.IsNullOrWhiteSpace(uiaDeep))
                    {
                        try
                        {
                            Logger.Log($"UIA(deep) selection captured: len={uiaDeep.Length}");
                        }
                        catch { }
                        return uiaDeep;
                    }
                    else
                    {
                        try
                        {
                            Logger.Log("UIA(deep) found no selection");
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    try
                    {
                        Logger.Log($"UIA(deep) error: {ex.GetType().Name}: {ex.Message}");
                    }
                    catch { }
                }
            }
            else if (isFirefox)
            {
                try
                {
                    Logger.Log(
                        "Step 4b: Skipping UIA deep search for Firefox to prevent COM crashes"
                    );
                }
                catch { }
            }
            else if (isNotepad)
            {
                try
                {
                    Logger.Log(
                        "Step 4b: Skipping UIA deep search for Notepad to avoid potential UIA instability"
                    );
                }
                catch { }
            }
            else if (isChrome)
            {
                try
                {
                    Logger.Log(
                        "Step 4b: Skipping UIA deep search for Chrome/Edge to prevent UIA crashes"
                    );
                }
                catch { }
            }
            else if (!isUiaResponsive)
            {
                try
                {
                    Logger.Log(
                        "Step 4b: Skipping UIA deep search because window failed Canary Probe"
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
                    Logger.Log(
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
            IntPtr targetHwnd = ResolveFocusHwnd(foregroundWindow);
            try
            {
                Logger.Log($"Target hwnd for copy: {DescribeWindow(targetHwnd)}");
            }
            catch { }

            // Optimized capture strategy: faster timeouts, better order
            int timeoutPrimary = 600; // Reduced from 1200ms
            int timeoutAlt = 300; // Reduced from 1200ms

            // Firefox-specific strategy: prioritize SendKeys and use longer timeouts
            if (isFirefox)
            {
                try
                {
                    Logger.Log("Firefox: Starting Firefox-specific copy strategy...");
                }
                catch { }
                timeoutPrimary = 800; // Longer timeout for Firefox
                timeoutAlt = 500;
            }

            try
            {
                Logger.Log("Step 6c: Starting SendKeys copy attempts...");
            }
            catch { }
            // Try SendKeys first (fastest for most apps, especially reliable for Firefox)
            if (
                TryCopyAndRead(
                    targetHwnd,
                    seqBefore,
                    CopyMethod.SendKeysCtrlC,
                    out var copied,
                    timeoutPrimary
                )
            )
            {
                try
                {
                    Logger.Log(
                        $"Step 7: Captured via SendKeys Ctrl+C: len={copied.Length} (elapsed {sw.ElapsedMilliseconds} ms){(isFirefox ? " [Firefox]" : "")}"
                    );
                }
                catch
                {
                    try
                    {
                        Logger.Log(
                            $"=== CAPTURE SUCCESS (SendKeys){(isFirefox ? " [Firefox]" : "")} ==="
                        );
                    }
                    catch { }
                }
                return copied;
            }

            // Quick fallback to original clipboard if available
            if (useClipboardFallback && (originalClipboard?.Length > 0))
            {
                try
                {
                    Logger.Log(
                        $"Falling back to original clipboard: len={originalClipboard.Length}"
                    );
                }
                catch { }
                return originalClipboard!;
            }

            // Application-specific attempts with shorter timeouts
            if (isSublime) // Sublime Text
            {
                try
                {
                    Logger.Log("Sublime: SendKeys failed, trying Double Ctrl+C");
                }
                catch { }
                if (
                    TryCopyAndRead(
                        targetHwnd,
                        seqBefore,
                        CopyMethod.DoubleCtrlC,
                        out copied,
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

            // Firefox-specific: try additional methods if SendKeys failed
            if (isFirefox)
            {
                try
                {
                    Logger.Log("Firefox: SendKeys failed, trying alternative copy methods...");
                }
                catch { }
                // Try standard Ctrl+C with SendInput for Firefox
                if (TryCopyAndRead(targetHwnd, seqBefore, CopyMethod.CtrlC, out copied, timeoutAlt))
                {
                    try
                    {
                        Logger.Log(
                            $"Firefox: Captured via Ctrl+C: len={copied.Length} (elapsed {sw.ElapsedMilliseconds} ms)"
                        );
                    }
                    catch
                    {
                        try
                        {
                            Logger.Log("=== CAPTURE SUCCESS (Firefox Ctrl+C) ===");
                        }
                        catch { }
                    }
                    return copied;
                }

                // Try Ctrl+Insert as Firefox fallback
                if (TryCopyAndRead(targetHwnd, seqBefore, CopyMethod.CtrlInsert, out copied, 400))
                {
                    try
                    {
                        Logger.Log(
                            $"Firefox: Captured via Ctrl+Insert: len={copied.Length} (elapsed {sw.ElapsedMilliseconds} ms)"
                        );
                    }
                    catch
                    {
                        try
                        {
                            Logger.Log("=== CAPTURE SUCCESS (Firefox Ctrl+Insert) ===");
                        }
                        catch { }
                    }
                    return copied;
                }
            }
            else
            {
                // Try standard Ctrl+C with shorter timeout for non-Firefox
                if (TryCopyAndRead(targetHwnd, seqBefore, CopyMethod.CtrlC, out copied, timeoutAlt))
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

                // Last resort methods with minimal timeout
                if (TryCopyAndRead(targetHwnd, seqBefore, CopyMethod.CtrlInsert, out copied, 200))
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
            }

            try
            {
                Logger.Log(
                    $"All copy attempts failed to update clipboard{(isFirefox ? " [Firefox]" : "")}"
                );
            }
            catch { }

            // Fallback: try to use clipboard contents if allowed
            if (useClipboardFallback)
            {
                string? fallback = originalClipboard;
                // If original clipboard was empty, try reading current clipboard state
                if (string.IsNullOrEmpty(fallback))
                {
                    try
                    {
                        if (Clipboard.ContainsText())
                        {
                            fallback = Clipboard.GetText(TextDataFormat.UnicodeText);
                            try
                            {
                                Logger.Log(
                                    $"Fallback clipboard read: hasText=True, len={fallback?.Length ?? 0}{(isFirefox ? " [Firefox]" : "")}"
                                );
                            }
                            catch { }
                        }
                        else
                        {
                            try
                            {
                                Logger.Log("Fallback clipboard read: hasText=False");
                            }
                            catch { }
                        }
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            Logger.Log(
                                $"Fallback clipboard read failed: {ex.GetType().Name}: {ex.Message}"
                            );
                        }
                        catch { }
                    }
                }

                if (!string.IsNullOrWhiteSpace(fallback))
                {
                    try
                    {
                        Logger.Log(
                            $"No selection captured; using clipboard fallback (elapsed {sw.ElapsedMilliseconds} ms){(isFirefox ? " [Firefox]" : "")}, len={fallback.Length}"
                        );
                    }
                    catch { }
                    return fallback!;
                }
            }

            // Enhanced Firefox failure diagnostics when even fallback has nothing
            if (isFirefox)
            {
                try
                {
                    Logger.Log(
                        $"Firefox capture failed: No copy methods succeeded and no clipboard fallback available (elapsed {sw.ElapsedMilliseconds} ms)"
                    );
                }
                catch { }
                try
                {
                    Logger.Log(
                        "Firefox troubleshooting: Make sure text is highlighted in Firefox before triggering hotkey"
                    );
                }
                catch { }
                try
                {
                    Logger.Log("=== CAPTURE FAILED (Firefox) ===");
                }
                catch { }
            }
            else
            {
                try
                {
                    Logger.Log(
                        $"Step FINAL: No selection captured; not falling back to existing clipboard (elapsed {sw.ElapsedMilliseconds} ms)"
                    );
                }
                catch { }
                try
                {
                    Logger.Log("=== CAPTURE FAILED ===");
                }
                catch { }
            }
            return string.Empty;
        }
        finally
        {
            // Ensure UI animation stops
            CaptureEnded?.Invoke();
        }
    }

    public bool SetText(string text)
    {
        int retries = 3;
        while (retries-- > 0)
        {
            try
            {
                Clipboard.SetText(text, TextDataFormat.UnicodeText);
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
                    Logger.Log(
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
                Thread.Sleep(50);
            }
        }
        return false;
    }

    public async System.Threading.Tasks.Task<bool> PasteAsync()
    {
        try
        {
            await Task.Delay(150).ConfigureAwait(true); // Increased delay for better focus restoration
            bool success = await PasteWithMultipleMethodsAsync();
            if (!success)
            {
                try
                {
                    Logger.Log("All paste methods failed");
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
                Logger.Log($"Paste failed: {ex.GetType().Name}: {ex.Message}");
            }
            catch { }
            NotificationService.ShowError($"Paste operation failed: {ex.Message}");
            return false;
        }
    }

    private async System.Threading.Tasks.Task<bool> PasteWithMultipleMethodsAsync()
    {
        // Try multiple paste methods in order of reliability
        string[] methods = { "Shift+Insert", "Ctrl+V", "SendInput Ctrl+V" };

        foreach (string method in methods)
        {
            try
            {
                Logger.Log($"Attempting paste with {method}");

                bool success = method switch
                {
                    "Ctrl+V" => await TryPasteCtrlVAsync(),
                    "Shift+Insert" => await TryPasteShiftInsertAsync(),
                    "SendInput Ctrl+V" => await TryPasteSendInputAsync(),
                    _ => false,
                };

                if (success)
                {
                    Logger.Log($"Paste successful with {method}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                try
                {
                    Logger.Log($"{method} failed: {ex.GetType().Name}: {ex.Message}");
                }
                catch { }
            }

            // Brief delay between methods
            await Task.Delay(50).ConfigureAwait(true);
        }

        return false;
    }

    private async System.Threading.Tasks.Task<bool> TryPasteCtrlVAsync()
    {
        try
        {
            SendKeys.SendWait("^v");
            await Task.Delay(30).ConfigureAwait(true);
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
            SendKeys.SendWait("+{INSERT}");
            await Task.Delay(30).ConfigureAwait(true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async System.Threading.Tasks.Task<bool> TryPasteSendInputAsync()
    {
        try
        {
            // Use SendInput for more reliable paste
            ushort[] modifiers =
            {
                0x11, /*CTRL*/
            };
            SendChordScancode(
                modifiers,
                0x56 /*'V'*/
            );
            await Task.Delay(30).ConfigureAwait(true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryGetSelectionViaUIA(CancellationTokenSource? cts = null)
    {
        // Use MTA thread for UI Automation as per Microsoft recommendations.
        // AutomationElement instances do not implement IDisposable and require no explicit Dispose/using.
        // This prevents COM marshaling crashes with applications like Firefox.
        var uiaTask = RunInMtaForUIA(() =>
        {
            try
            {
                var focused = AutomationElement.FocusedElement;
                if (focused == null)
                    return null;

                try
                {
                    if (
                        focused.TryGetCurrentPattern(TextPattern.Pattern, out var tpObj)
                        && tpObj is TextPattern tp
                    )
                    {
                        var sel = tp.GetSelection();
                        if (sel != null && sel.Length > 0)
                        {
                            var text = sel[0].GetText(int.MaxValue);
                            return string.IsNullOrWhiteSpace(text) ? null : text;
                        }
                    }
                    if (
                        focused.TryGetCurrentPattern(ValuePattern.Pattern, out var vpObj)
                        && vpObj is ValuePattern vp
                    )
                    {
                        var v = vp.Current.Value;
                        return string.IsNullOrWhiteSpace(v) ? null : v;
                    }
                }
                catch (Exception ex)
                {
                    try
                    {
                        Logger.Log($"UIA pattern access failed: {ex.GetType().Name}: {ex.Message}");
                    }
                    catch { }
                    return null;
                }

                // Search within the active window for a focused text provider
                var hwnd = GetForegroundWindow();
                if (hwnd != IntPtr.Zero)
                {
                    try
                    {
                        var root = AutomationElement.FromHandle(hwnd);
                        if (root != null)
                        {
                            var cond = new PropertyCondition(
                                AutomationElement.IsTextPatternAvailableProperty,
                                true
                            );
                            var el = root.FindFirst(TreeScope.Subtree, cond);
                            if (
                                el != null
                                && el.TryGetCurrentPattern(TextPattern.Pattern, out var tpo)
                                && tpo is TextPattern tp2
                            )
                            {
                                var sel2 = tp2.GetSelection();
                                if (sel2 != null && sel2.Length > 0)
                                {
                                    var t2 = sel2[0].GetText(int.MaxValue);
                                    if (!string.IsNullOrWhiteSpace(t2))
                                        return t2;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            Logger.Log(
                                $"UIA subtree search failed: {ex.GetType().Name}: {ex.Message}"
                            );
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                try
                {
                    Logger.Log(
                        $"UIA focused element access failed: {ex.GetType().Name}: {ex.Message}"
                    );
                }
                catch { }
            }
            return null;
        });

        // Wait for completion with timeout
        if (uiaTask.Wait(System.TimeSpan.FromMilliseconds(800)))
        {
            return uiaTask.Result;
        }
        else
        {
            try
            {
                Logger.Log("UIA operation timed out after 800ms");
            }
            catch { }
            return null;
        }
    }

    private static string? TryGetSelectionViaUIAFromCaret(CancellationTokenSource? cts = null)
    {
        // Use MTA thread for UI Automation as per Microsoft recommendations
        var uiaTask = RunInMtaForUIA(() =>
        {
            try
            {
                var fg = GetForegroundWindow();
                var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
                uint threadId = GetWindowThreadProcessId(fg, out uint tid);
                if (threadId == 0 || !GetGUIThreadInfo(threadId, ref info))
                    return null;
                if (info.hwndCaret == IntPtr.Zero)
                    return null;
                var rc = info.rcCaret;
                var pt = new POINT { X = rc.Left + 1, Y = rc.Top + (rc.Bottom - rc.Top) / 2 };
                try
                {
                    var logStr =
                        $"Caret hwnd={DescribeWindow(info.hwndCaret)}, rc=({rc.Left},{rc.Top},{rc.Right},{rc.Bottom})";
                    Logger.Log(logStr);
                }
                catch { }
                try
                {
                    ClientToScreen(info.hwndCaret, ref pt);
                }
                catch { }
                System.Windows.Point wpt = new(pt.X, pt.Y);
                AutomationElement? el = null;
                try
                {
                    el = AutomationElement.FromPoint(wpt);
                }
                catch (Exception ex)
                {
                    try
                    {
                        Logger.Log($"UIA FromPoint failed: {ex.GetType().Name}: {ex.Message}");
                    }
                    catch { }
                    el = null;
                }
                if (el == null)
                    return null;
                // Walk up to find a TextPattern provider
                for (
                    AutomationElement? cur = el;
                    cur != null;
                    cur = TreeWalker.RawViewWalker.GetParent(cur)
                )
                {
                    try
                    {
                        if (
                            cur.TryGetCurrentPattern(TextPattern.Pattern, out var tpo)
                            && tpo is TextPattern tp
                        )
                        {
                            var sel = tp.GetSelection();
                            if (sel != null && sel.Length > 0)
                            {
                                var t = sel[0].GetText(int.MaxValue);
                                if (!string.IsNullOrWhiteSpace(t))
                                    return t;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            Logger.Log(
                                $"UIA caret pattern access failed: {ex.GetType().Name}: {ex.Message}"
                            );
                        }
                        catch { }
                        // Continue walking up the tree
                    }
                }
            }
            catch (Exception ex)
            {
                try
                {
                    Logger.Log($"UIA caret method failed: {ex.GetType().Name}: {ex.Message}");
                }
                catch { }
            }
            return null;
        });

        // Wait for completion with timeout
        if (uiaTask.Wait(System.TimeSpan.FromMilliseconds(500)))
        {
            return uiaTask.Result;
        }
        else
        {
            try
            {
                Logger.Log("UIA caret operation timed out after 500ms");
            }
            catch { }
            return null;
        }
    }

    private static string? TryGetSelectionViaWin32(IntPtr hwndForeground)
    {
        try
        {
            var ctrl = ResolveFocusHwnd(hwndForeground);
            if (ctrl == IntPtr.Zero)
                return null;
            int len = 0;
            try
            {
                len = (int)SendMessage(ctrl, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero);
            }
            catch
            {
                len = 0;
            }
            if (len <= 0)
                return null;
            int selStart = 0,
                selEnd = 0;
            try
            {
                SendMessage(ctrl, EM_GETSEL, out selStart, out selEnd);
            }
            catch
            {
                selStart = selEnd = 0;
            }
            if (selEnd <= selStart)
                return null;
            var sb = new StringBuilder(len + 1);
            try
            {
                SendMessage(ctrl, WM_GETTEXT, sb.Capacity, sb);
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

    private static string? TryGetSelectionViaUIADeep(
        IntPtr hwndForeground,
        CancellationTokenSource? cts = null
    )
    {
        // Use MTA thread for UI Automation as per Microsoft recommendations
        var uiaTask = RunInMtaForUIA(() =>
        {
            try
            {
                // Attempt selection from caret point
                try
                {
                    var caretSel = TryGetSelectionAtCaretPoint();
                    if (!string.IsNullOrWhiteSpace(caretSel))
                        return caretSel;
                }
                catch (Exception ex)
                {
                    try
                    {
                        Logger.Log($"UIA caret method failed: {ex.GetType().Name}: {ex.Message}");
                    }
                    catch { }
                }
                if (hwndForeground == IntPtr.Zero)
                    return null;
                AutomationElement? root = null;
                try
                {
                    root = AutomationElement.FromHandle(hwndForeground);
                }
                catch (Exception ex)
                {
                    try
                    {
                        Logger.Log(
                            $"UIA deep FromHandle failed: {ex.GetType().Name}: {ex.Message}"
                        );
                    }
                    catch { }
                    return null;
                }
                if (root == null)
                    return null;

                var sw = Stopwatch.StartNew();
                int visited = 0;
                var stack = new System.Collections.Generic.Stack<AutomationElement>();
                stack.Push(root);
                while (stack.Count > 0 && visited < 3000 && sw.ElapsedMilliseconds < 400)
                {
                    AutomationElement? el = null;
                    try
                    {
                        el = stack.Pop();
                    }
                    catch
                    {
                        el = null;
                    }
                    if (el == null)
                        continue;
                    visited++;
                    try
                    {
                        if (
                            el.TryGetCurrentPattern(TextPattern.Pattern, out var tpo)
                            && tpo is TextPattern tp
                        )
                        {
                            try
                            {
                                var sel = tp.GetSelection();
                                if (sel != null && sel.Length > 0)
                                {
                                    var t = sel[0].GetText(int.MaxValue);
                                    if (!string.IsNullOrWhiteSpace(t))
                                        return t;
                                }
                            }
                            catch (Exception ex)
                            {
                                try
                                {
                                    Logger.Log(
                                        $"UIA deep selection access failed: {ex.GetType().Name}: {ex.Message}"
                                    );
                                }
                                catch { }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            Logger.Log(
                                $"UIA deep pattern access failed: {ex.GetType().Name}: {ex.Message}"
                            );
                        }
                        catch { }
                    }

                    try
                    {
                        var children = el.FindAll(TreeScope.Children, Condition.TrueCondition);
                        for (int i = 0; i < children.Count; i++)
                        {
                            var child = children[i];
                            if (child != null)
                                stack.Push(child);
                        }
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            Logger.Log(
                                $"UIA deep children enumeration failed: {ex.GetType().Name}: {ex.Message}"
                            );
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                try
                {
                    Logger.Log($"UIA deep search failed: {ex.GetType().Name}: {ex.Message}");
                }
                catch { }
            }
            return null;
        });

        // Wait for completion with timeout
        if (uiaTask.Wait(System.TimeSpan.FromMilliseconds(800)))
        {
            return uiaTask.Result;
        }
        else
        {
            try
            {
                Logger.Log("UIA deep search timed out after 800ms");
            }
            catch { }
            return null;
        }
    }

    private static string? TryGetSelectionAtCaretPoint()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
                return null;
            var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
            uint threadId = GetWindowThreadProcessId(hwnd, out uint tid);
            if (threadId == 0 || !GetGUIThreadInfo(threadId, ref info))
                return null;
            var owner = info.hwndCaret != IntPtr.Zero ? info.hwndCaret : hwnd;
            int cx = info.rcCaret.Left + ((info.rcCaret.Right - info.rcCaret.Left) / 2);
            int cy = info.rcCaret.Top + ((info.rcCaret.Bottom - info.rcCaret.Top) / 2);
            var pt = new POINT { X = cx, Y = cy };
            try
            {
                ClientToScreen(owner, ref pt);
            }
            catch { }
            var el = AutomationElement.FromPoint(new System.Windows.Point(pt.X, pt.Y));
            if (
                el != null
                && el.TryGetCurrentPattern(TextPattern.Pattern, out var tpo)
                && tpo is TextPattern tp
            )
            {
                var sel = tp.GetSelection();
                if (sel != null && sel.Length > 0)
                {
                    var t = sel[0].GetText(int.MaxValue);
                    if (!string.IsNullOrWhiteSpace(t))
                        return t;
                }
            }
        }
        catch { }
        return null;
    }

    private static IntPtr ResolveFocusHwnd(IntPtr hwndForeground)
    {
        try
        {
            var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
            uint threadId = GetWindowThreadProcessId(hwndForeground, out uint _);
            if (threadId != 0 && GetGUIThreadInfo(threadId, ref info))
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
        int timeoutMs = 1200
    )
    {
        result = string.Empty;

        // Enhanced Firefox diagnostics - declare in outer scope for exception handling
        bool isFirefoxWindow = IsWindowClass(hwnd, "MozillaWindowClass");
        if (isFirefoxWindow)
        {
            try
            {
                Logger.Log($"Firefox: Copy method {method} being attempted on Firefox window");
            }
            catch { }
            try
            {
                Logger.Log($"Firefox: Window details: {DescribeWindow(hwnd)}");
            }
            catch { }
        }

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
                        Logger.Log(
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
                    SendChordScancode(
                        new ushort[]
                        {
                            0x11, /*VK_CONTROL*/
                        },
                        0x43 /*'C'*/
                    );
                    break;
                case CopyMethod.WmCopy:
                    try
                    {
                        SendMessage(targetForMessage, WM_COPY, IntPtr.Zero, IntPtr.Zero);
                    }
                    catch { }
                    break;
                case CopyMethod.CtrlInsert:
                    NormalizeInputState();
                    SendChordScancode(
                        new ushort[]
                        {
                            0x11, /*VK_CONTROL*/
                        },
                        0x2D /*VK_INSERT*/
                    );
                    break;
                case CopyMethod.CtrlShiftC:
                    NormalizeInputState();
                    SendChordScancode(
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
                            var t = WaitForClipboardTextChange(seqBefore, perAttempt);
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
                    break;
                case CopyMethod.DoubleCtrlC:
                    NormalizeInputState();
                    SendChordScancode(new ushort[] { 0x11 }, 0x43);
                    Thread.Sleep(120);
                    SendChordScancode(new ushort[] { 0x11 }, 0x43);
                    break;
                case CopyMethod.MenuAltEC:
                    NormalizeInputState();
                    // Alt+E open Edit menu, then 'C' for Copy
                    SendChordScancode(
                        new ushort[]
                        {
                            0x12, /*ALT*/
                        },
                        0x45 /*'E'*/
                    );
                    Thread.Sleep(150);
                    SendChordScancode(
                        Array.Empty<ushort>(),
                        0x43 /*'C'*/
                    );
                    break;
            }
            var text = WaitForClipboardTextChange(seqBefore, timeoutMs);
            if (!string.IsNullOrWhiteSpace(text))
            {
                result = text!;
                if (isFirefoxWindow)
                {
                    try
                    {
                        Logger.Log(
                            $"Firefox: Copy method {method} SUCCEEDED: len={text.Length}, seq={seqBefore}->{GetClipboardSequenceNumber()}"
                        );
                    }
                    catch { }
                }
                else
                {
                    try
                    {
                        Logger.Log($"TryCopyAndRead success: method={method}, len={text.Length}");
                    }
                    catch { }
                }
                return true;
            }
            else
            {
                if (isFirefoxWindow)
                {
                    try
                    {
                        Logger.Log(
                            $"Firefox: Copy method {method} FAILED: no clipboard change, seq={seqBefore}->{GetClipboardSequenceNumber()}"
                        );
                    }
                    catch { }
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
        }
        catch (Exception ex)
        {
            if (isFirefoxWindow)
            {
                try
                {
                    Logger.Log(
                        $"Firefox: Copy method {method} EXCEPTION: {ex.GetType().Name}: {ex.Message}"
                    );
                }
                catch { }
            }
            else
            {
                try
                {
                    Logger.Log(
                        $"TryCopyAndRead({method}) error: {ex.GetType().Name}: {ex.Message}"
                    );
                }
                catch { }
            }
        }
        return false;
    }

    private static string? WaitForClipboardTextChange(uint seqBefore, int timeoutMs)
    {
        var start = Environment.TickCount;
        while (Environment.TickCount - start < timeoutMs)
        {
            uint seqNow = 0;
            try
            {
                seqNow = GetClipboardSequenceNumber();
            }
            catch { }
            if (seqNow != 0 && seqNow != seqBefore)
            {
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
            }
            Thread.Sleep(25);
        }
        return null;
    }

    private static void SendChordScancode(ushort[] modifiersVk, ushort keyVk)
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
        try
        {
            SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
        }
        catch { }
        Thread.Sleep(30); // Reduced from 80ms for faster response
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

    public System.Threading.Tasks.Task<string> CaptureSelectionOrClipboardAsync(
        bool useClipboardFallback = false
    )
    {
        return RunInSta(() => CaptureSelectionOrClipboard(useClipboardFallback));
    }

    private static void ClearCacheIfNeeded()
    {
        // Clear cache every 5 minutes to prevent stale handles
        if (DateTime.Now - _lastCacheClear > TimeSpan.FromMinutes(5))
        {
            _windowHandleCache.Clear();
            _lastCacheClear = DateTime.Now;
        }
    }

    private static void RecordCaptureSuccess(string method, bool success)
    {
        string key = success ? $"{method}_success" : $"{method}_fail";
        _captureStats.TryGetValue(key, out int count);
        _captureStats[key] = count + 1;

        // Log stats every 10 captures
        if ((_captureStats.Values.Sum() % 10) == 0)
        {
            try
            {
                Logger.Log(
                    $"Capture stats: {string.Join(", ", _captureStats.Select(kvp => $"{kvp.Key}={kvp.Value}"))}"
                );
            }
            catch { }
        }
    }

    private static IntPtr GetCachedWindowHandle(string windowClass)
    {
        if (_windowHandleCache.TryGetValue(windowClass, out IntPtr cachedHandle))
        {
            // Verify handle is still valid
            if (IsWindow(cachedHandle))
                return cachedHandle;
            else
                _windowHandleCache.Remove(windowClass);
        }
        return IntPtr.Zero;
    }

    private static void CacheWindowHandle(string windowClass, IntPtr handle)
    {
        if (handle != IntPtr.Zero && IsWindow(handle))
        {
            _windowHandleCache[windowClass] = handle;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

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

    // New method for UI Automation operations using MTA thread (recommended by Microsoft)
    // Enhanced with thread tracking and abort for timed-out operations
    private static System.Threading.Tasks.Task<T> RunInMtaForUIA<T>(
        Func<T> func,
        CancellationTokenSource? cts = null
    )
    {
        var tcs = new System.Threading.Tasks.TaskCompletionSource<T>();
        Thread? th = null;
        th = new Thread(() =>
        {
            try
            {
                var r = func();
                tcs.SetResult(r);
            }
            catch (ThreadAbortException)
            {
                // Thread was aborted due to timeout - this is expected
                try
                {
                    Logger.Log("UIA MTA thread aborted due to timeout");
                }
                catch { }
                tcs.TrySetCanceled();
            }
            catch (Exception ex)
            {
                try
                {
                    Logger.Log($"UIA MTA thread exception: {ex.GetType().Name}: {ex.Message}");
                }
                catch { }
                tcs.TrySetException(ex);
            }
        });
        th.IsBackground = true;
        th.SetApartmentState(ApartmentState.MTA); // Use MTA for UI Automation as per Microsoft docs
        th.Start();

        // Monitor thread but don't try to abort (PlatformNotSupported in .NET Core/5+)
        // We just rely on the Task.Wait timeout in the caller to unblock the main thread
        // The background thread may remain as a zombie if it hangs, but won't crash the app

        return tcs.Task;
    }
}
