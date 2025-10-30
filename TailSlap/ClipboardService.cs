using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Automation;

public sealed class ClipboardService
{
    // Performance metrics
    private static readonly System.Collections.Generic.Dictionary<string, int> _captureStats = new();
    private static readonly System.Collections.Generic.Dictionary<string, IntPtr> _windowHandleCache = new();
    private static DateTime _lastCacheClear = DateTime.Now;

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

    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int SendMessage(IntPtr hWnd, uint Msg, int wParam, StringBuilder lParam);
    [DllImport("user32.dll")] private static extern int SendMessage(IntPtr hWnd, uint Msg, out int wParam, out int lParam);
    [DllImport("user32.dll")] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")] private static extern IntPtr GetFocus();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    private const uint WM_COPY = 0x0301;
    private const uint WM_GETTEXT = 0x000D;
    private const uint WM_GETTEXTLENGTH = 0x000E;
    private const uint EM_GETSEL = 0x00B0;
    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;
    private const uint MAPVK_VK_TO_VSC = 0x0;
    private const int SW_RESTORE = 9;

    [StructLayout(LayoutKind.Sequential)] private struct GUITHREADINFO
    {
        public int cbSize; public uint flags; public IntPtr hwndActive; public IntPtr hwndFocus; public IntPtr hwndCapture; public IntPtr hwndMenuOwner; public IntPtr hwndMoveSize; public IntPtr hwndCaret; public RECT rcCaret;
    }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public int type; public INPUTUNION U; }
    [StructLayout(LayoutKind.Explicit)] private struct INPUTUNION { [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }

    private static string DescribeWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return "hWnd=0";
        var title = new StringBuilder(256);
        var cls = new StringBuilder(128);
        try { GetWindowText(hWnd, title, title.Capacity); } catch { }
        try { GetClassName(hWnd, cls, cls.Capacity); } catch { }
        string proc = "?";
        try
        {
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid != 0)
            {
                using var p = Process.GetProcessById((int)pid);
                proc = p.ProcessName + ":" + pid;
            }
        }
        catch { }
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
        catch { return false; }
    }

    private static void LogClipboardState(string prefix)
    {
        try
        {
            bool hasText = Clipboard.ContainsText();
            int len = 0;
            if (hasText)
            {
                try { len = Clipboard.GetText(TextDataFormat.UnicodeText)?.Length ?? 0; } catch { }
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
            try { Logger.Log($"[{prefix}] Clipboard hasText={hasText}, textLen={len}, formats=[{formats}]"); } catch { }
        }
        catch (Exception ex)
        {
            try { Logger.Log($"[{prefix}] Clipboard state error: {ex.GetType().Name}: {ex.Message}"); } catch { }
        }
    }

    public string CaptureSelectionOrClipboard(bool useClipboardFallback = false)
    {
        var sw = Stopwatch.StartNew();
        ClearCacheIfNeeded();
        
        try { Logger.Log($"Capture start. ThreadId={Thread.CurrentThread.ManagedThreadId}, Apt={Thread.CurrentThread.GetApartmentState()}, Fallback={useClipboardFallback}"); } catch { }
        LogClipboardState("Before");
        string? originalClipboard = null;
        try 
        { 
            if (Clipboard.ContainsText()) 
                originalClipboard = Clipboard.GetText(TextDataFormat.UnicodeText); 
            try { Logger.Log($"Original clipboard captured: len={(originalClipboard?.Length ?? 0)}"); } catch { }
        } 
        catch (Exception ex) { try { Logger.Log($"Read original clipboard failed: {ex.GetType().Name}: {ex.Message}"); } catch { } }
        
        IntPtr foregroundWindow = GetForegroundWindow();
        try { Logger.Log($"Foreground before: {DescribeWindow(foregroundWindow)}"); } catch { }
        
        // 1) Try UI Automation first
        try
        {
            var uia = TryGetSelectionViaUIA();
            if (!string.IsNullOrWhiteSpace(uia))
            {
                RecordCaptureSuccess("UIA", true);
                try { Logger.Log($"UIA selection captured: len={uia.Length}"); } catch { }
                return uia;
            }
            else 
            { 
                RecordCaptureSuccess("UIA", false);
                try { Logger.Log("UIA selection unavailable or empty"); } catch { } 
            }
            // UIA hit-test near caret as a secondary attempt
            var uiaPt = TryGetSelectionViaUIAFromCaret();
            if (!string.IsNullOrWhiteSpace(uiaPt))
            {
                RecordCaptureSuccess("UIA_FromPoint", true);
                try { Logger.Log($"UIA(FromPoint) selection captured: len={uiaPt.Length}"); } catch { }
                return uiaPt;
            }
            else
            {
                RecordCaptureSuccess("UIA_FromPoint", false);
            }
        }
        catch (Exception ex) { try { Logger.Log($"UIA selection error: {ex.GetType().Name}: {ex.Message}"); } catch { } }

        // 1b) UIA deep search (caret point and subtree scan)
        try
        {
            var uiaDeep = TryGetSelectionViaUIADeep(foregroundWindow);
            if (!string.IsNullOrWhiteSpace(uiaDeep))
            {
                try { Logger.Log($"UIA(deep) selection captured: len={uiaDeep.Length}"); } catch { }
                return uiaDeep;
            }
            else { try { Logger.Log("UIA(deep) found no selection"); } catch { } }
        }
        catch (Exception ex) { try { Logger.Log($"UIA(deep) error: {ex.GetType().Name}: {ex.Message}"); } catch { } }

        // 2) Win32 direct read (standard edit controls)
        try
        {
            var win32Sel = TryGetSelectionViaWin32(foregroundWindow);
            if (!string.IsNullOrWhiteSpace(win32Sel))
            {
                try { Logger.Log($"Win32 selection captured: len={win32Sel.Length}"); } catch { }
                return win32Sel;
            }
        }
        catch (Exception ex) { try { Logger.Log($"Win32 selection error: {ex.GetType().Name}: {ex.Message}"); } catch { } }

        // 3) Clipboard-based copy without clearing, using sequence number + multiple methods
        uint seqBefore = 0;
        try { seqBefore = GetClipboardSequenceNumber(); Logger.Log($"Clipboard seq before: {seqBefore}"); } catch { }
        IntPtr targetHwnd = ResolveFocusHwnd(foregroundWindow);
        try { Logger.Log($"Target hwnd for copy: {DescribeWindow(targetHwnd)}"); } catch { }

        // Optimized capture strategy: faster timeouts, better order
        int timeoutPrimary = 600;  // Reduced from 1200ms
        int timeoutAlt = 300;      // Reduced from 1200ms
        
        // Try SendKeys first (fastest for most apps)
        if (TryCopyAndRead(targetHwnd, seqBefore, CopyMethod.SendKeysCtrlC, out var copied, timeoutPrimary))
        { try { Logger.Log($"Captured via SendKeys Ctrl+C: len={copied.Length} (elapsed {sw.ElapsedMilliseconds} ms)"); } catch { } return copied; }
        
        // Quick fallback to original clipboard if available
        if (useClipboardFallback && (originalClipboard?.Length > 0))
        { try { Logger.Log($"Falling back to original clipboard: len={originalClipboard.Length}"); } catch { } return originalClipboard!; }
        
        // Application-specific attempts with shorter timeouts
        if (IsWindowClass(foregroundWindow, "PX_WINDOW_CLASS")) // Sublime Text
        {
            if (TryCopyAndRead(targetHwnd, seqBefore, CopyMethod.DoubleCtrlC, out copied, timeoutAlt))
            { try { Logger.Log($"Captured via Double Ctrl+C (Sublime): len={copied.Length} (elapsed {sw.ElapsedMilliseconds} ms)"); } catch { } return copied; }
        }
        
        // Try standard Ctrl+C with shorter timeout
        if (TryCopyAndRead(targetHwnd, seqBefore, CopyMethod.CtrlC, out copied, timeoutAlt))
        { try { Logger.Log($"Captured via Ctrl+C: len={copied.Length} (elapsed {sw.ElapsedMilliseconds} ms)"); } catch { } return copied; }
        
        // Last resort methods with minimal timeout
        if (TryCopyAndRead(targetHwnd, seqBefore, CopyMethod.CtrlInsert, out copied, 200))
        { try { Logger.Log($"Captured via Ctrl+Insert: len={copied.Length} (elapsed {sw.ElapsedMilliseconds} ms)"); } catch { } return copied; }
        
        try { Logger.Log("All copy attempts failed to update clipboard"); } catch { }
        if (useClipboardFallback && (originalClipboard?.Length > 0))
        { try { Logger.Log($"No selection captured; using original clipboard (elapsed {sw.ElapsedMilliseconds} ms)"); } catch { } return originalClipboard!; }
        try { Logger.Log($"No selection captured; not falling back to existing clipboard (elapsed {sw.ElapsedMilliseconds} ms)"); } catch { }
        return string.Empty;
    }

    public void SetText(string text)
    {
        int retries = 3;
        while (retries-- > 0)
        {
            try { Clipboard.SetText(text, TextDataFormat.UnicodeText); try { Logger.Log($"SetText ok, len={text?.Length ?? 0}"); } catch { } return; }
            catch (Exception ex) { try { Logger.Log($"SetText failed (retries left {retries}): {ex.GetType().Name}: {ex.Message}"); } catch { } Thread.Sleep(50); }
        }
    }

    public void Paste() 
    { 
        try 
        { 
            Thread.Sleep(150); // Increased delay for better focus restoration
            PasteWithMultipleMethods();
        } 
        catch (Exception ex) { try { Logger.Log($"Paste failed: {ex.GetType().Name}: {ex.Message}"); } catch { } } 
    }

    private void PasteWithMultipleMethods()
    {
        // Try multiple paste methods in order of reliability
        string[] methods = { "Ctrl+V", "Shift+Insert", "SendInput Ctrl+V" };
        
        foreach (string method in methods)
        {
            try 
            { 
                Logger.Log($"Attempting paste with {method}");
                
                bool success = method switch
                {
                    "Ctrl+V" => TryPasteCtrlV(),
                    "Shift+Insert" => TryPasteShiftInsert(),
                    "SendInput Ctrl+V" => TryPasteSendInput(),
                    _ => false
                };
                
                if (success) 
                {
                    Logger.Log($"Paste successful with {method}");
                    return;
                }
            }
            catch (Exception ex) 
            { 
                try { Logger.Log($"{method} failed: {ex.GetType().Name}: {ex.Message}"); } catch { } 
            }
            
            // Brief delay between methods
            Thread.Sleep(50);
        }
        
        Logger.Log("All paste methods failed");
    }

    private bool TryPasteCtrlV()
    {
        try
        {
            SendKeys.SendWait("^v");
            Thread.Sleep(30);
            return true;
        }
        catch { return false; }
    }

    private bool TryPasteShiftInsert()
    {
        try
        {
            SendKeys.SendWait("+{INSERT}");
            Thread.Sleep(30);
            return true;
        }
        catch { return false; }
    }

    private bool TryPasteSendInput()
    {
        try
        {
            // Use SendInput for more reliable paste
            ushort[] modifiers = { 0x11 /*CTRL*/ };
            SendChordScancode(modifiers, 0x56 /*'V'*/);
            Thread.Sleep(30);
            return true;
        }
        catch { return false; }
    }

    private static string? TryGetSelectionViaUIA()
    {
        var focused = AutomationElement.FocusedElement;
        if (focused == null) return null;
        try
        {
            if (focused.TryGetCurrentPattern(TextPattern.Pattern, out var tpObj) && tpObj is TextPattern tp)
            {
                var sel = tp.GetSelection();
                if (sel != null && sel.Length > 0)
                {
                    var text = sel[0].GetText(int.MaxValue);
                    return string.IsNullOrWhiteSpace(text) ? null : text;
                }
            }
            if (focused.TryGetCurrentPattern(ValuePattern.Pattern, out var vpObj) && vpObj is ValuePattern vp)
            {
                var v = vp.Current.Value;
                return string.IsNullOrWhiteSpace(v) ? null : v;
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
                        var cond = new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true);
                        var el = root.FindFirst(TreeScope.Subtree, cond);
                        if (el != null && el.TryGetCurrentPattern(TextPattern.Pattern, out var tpo) && tpo is TextPattern tp2)
                        {
                            var sel2 = tp2.GetSelection();
                            if (sel2 != null && sel2.Length > 0)
                            {
                                var t2 = sel2[0].GetText(int.MaxValue);
                                if (!string.IsNullOrWhiteSpace(t2)) return t2;
                            }
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    private static string? TryGetSelectionViaUIAFromCaret()
    {
        try
        {
            var fg = GetForegroundWindow();
            var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
            uint tid = GetWindowThreadProcessId(fg, out _);
            if (!GetGUIThreadInfo(tid, ref info)) return null;
            if (info.hwndCaret == IntPtr.Zero) return null;
            var rc = info.rcCaret;
            var pt = new POINT { X = rc.Left + 1, Y = rc.Top + (rc.Bottom - rc.Top) / 2 };
            try { var logStr = $"Caret hwnd={DescribeWindow(info.hwndCaret)}, rc=({rc.Left},{rc.Top},{rc.Right},{rc.Bottom})"; Logger.Log(logStr); } catch { }
            try { ClientToScreen(info.hwndCaret, ref pt); } catch { }
            System.Windows.Point wpt = new(pt.X, pt.Y);
            AutomationElement? el = null;
            try { el = AutomationElement.FromPoint(wpt); } catch { el = null; }
            if (el == null) return null;
            // Walk up to find a TextPattern provider
            for (AutomationElement? cur = el; cur != null; cur = TreeWalker.RawViewWalker.GetParent(cur))
            {
                try
                {
                    if (cur.TryGetCurrentPattern(TextPattern.Pattern, out var tpo) && tpo is TextPattern tp)
                    {
                        var sel = tp.GetSelection();
                        if (sel != null && sel.Length > 0)
                        {
                            var t = sel[0].GetText(int.MaxValue);
                            if (!string.IsNullOrWhiteSpace(t)) return t;
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    private static string? TryGetSelectionViaWin32(IntPtr hwndForeground)
    {
        try
        {
            var ctrl = ResolveFocusHwnd(hwndForeground);
            if (ctrl == IntPtr.Zero) return null;
            int len = 0;
            try { len = (int)SendMessage(ctrl, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero); } catch { len = 0; }
            if (len <= 0) return null;
            int selStart = 0, selEnd = 0;
            try { SendMessage(ctrl, EM_GETSEL, out selStart, out selEnd); } catch { selStart = selEnd = 0; }
            if (selEnd <= selStart) return null;
            var sb = new StringBuilder(len + 1);
            try { SendMessage(ctrl, WM_GETTEXT, sb.Capacity, sb); } catch { }
            var value = sb.ToString();
            if (string.IsNullOrEmpty(value)) return null;
            if (selStart < 0 || selEnd > value.Length) return null;
            var slice = value.Substring(selStart, Math.Min(selEnd, value.Length) - selStart);
            return string.IsNullOrWhiteSpace(slice) ? null : slice;
        }
        catch { return null; }
    }

    private static string? TryGetSelectionViaUIADeep(IntPtr hwndForeground)
    {
        try
        {
            // Attempt selection from caret point
            var caretSel = TryGetSelectionAtCaretPoint();
            if (!string.IsNullOrWhiteSpace(caretSel)) return caretSel;

            if (hwndForeground == IntPtr.Zero) return null;
            var root = AutomationElement.FromHandle(hwndForeground);
            if (root == null) return null;

            var sw = Stopwatch.StartNew();
            int visited = 0;
            var stack = new System.Collections.Generic.Stack<AutomationElement>();
            stack.Push(root);
            while (stack.Count > 0 && visited < 3000 && sw.ElapsedMilliseconds < 400)
            {
                AutomationElement? el = null;
                try { el = stack.Pop(); } catch { el = null; }
                if (el == null) continue;
                visited++;
                try
                {
                    if (el.TryGetCurrentPattern(TextPattern.Pattern, out var tpo) && tpo is TextPattern tp)
                    {
                        try
                        {
                            var sel = tp.GetSelection();
                            if (sel != null && sel.Length > 0)
                            {
                                var t = sel[0].GetText(int.MaxValue);
                                if (!string.IsNullOrWhiteSpace(t)) return t;
                            }
                        }
                        catch { }
                    }
                }
                catch { }

                try
                {
                    var children = el.FindAll(TreeScope.Children, Condition.TrueCondition);
                    for (int i = 0; i < children.Count; i++)
                    {
                        var child = children[i];
                        if (child != null) stack.Push(child);
                    }
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    private static string? TryGetSelectionAtCaretPoint()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;
            var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
            uint tid = GetWindowThreadProcessId(hwnd, out _);
            if (!GetGUIThreadInfo(tid, ref info)) return null;
            var owner = info.hwndCaret != IntPtr.Zero ? info.hwndCaret : hwnd;
            int cx = info.rcCaret.Left + ((info.rcCaret.Right - info.rcCaret.Left) / 2);
            int cy = info.rcCaret.Top + ((info.rcCaret.Bottom - info.rcCaret.Top) / 2);
            var pt = new POINT { X = cx, Y = cy };
            try { ClientToScreen(owner, ref pt); } catch { }
            var el = AutomationElement.FromPoint(new System.Windows.Point(pt.X, pt.Y));
            if (el != null && el.TryGetCurrentPattern(TextPattern.Pattern, out var tpo) && tpo is TextPattern tp)
            {
                var sel = tp.GetSelection();
                if (sel != null && sel.Length > 0)
                {
                    var t = sel[0].GetText(int.MaxValue);
                    if (!string.IsNullOrWhiteSpace(t)) return t;
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
            uint tid = GetWindowThreadProcessId(hwndForeground, out uint _);
            if (GetGUIThreadInfo(tid, ref info))
            {
                if (info.hwndFocus != IntPtr.Zero) return info.hwndFocus;
                if (info.hwndActive != IntPtr.Zero) return info.hwndActive;
            }
            // Fallback: attach to target thread and query GetFocus directly
            uint currentTid = GetCurrentThreadId();
            if (tid != 0 && currentTid != tid)
            {
                try
                {
                    if (AttachThreadInput(currentTid, tid, true))
                    {
                        try { var f = GetFocus(); if (f != IntPtr.Zero) return f; }
                        finally { AttachThreadInput(currentTid, tid, false); }
                    }
                }
                catch { }
            }
        }
        catch { }
        return hwndForeground;
    }

    private enum CopyMethod { CtrlC, WmCopy, CtrlInsert, CtrlShiftC, SendKeysCtrlC, DoubleCtrlC, MenuAltEC }

    private bool TryCopyAndRead(IntPtr hwnd, uint seqBefore, CopyMethod method, out string result, int timeoutMs = 1200)
    {
        result = string.Empty;
        try
        {
            try { Logger.Log($"TryCopyAndRead start: method={method}, seqBefore={seqBefore}"); } catch { }
            if (hwnd != IntPtr.Zero)
            {
                try
                {
                    if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
                    BringWindowToTop(hwnd);
                    SetForegroundWindow(hwnd);
                }
                catch { }
                Thread.Sleep(60);
            }
            IntPtr targetForMessage = hwnd;
            if (method == CopyMethod.WmCopy)
            {
                var focused = ResolveFocusHwnd(hwnd);
                if (focused != IntPtr.Zero) targetForMessage = focused;
                try { Logger.Log($"WM_COPY target hwnd: {DescribeWindow(targetForMessage)}"); } catch { }
            }
            switch (method)
            {
                case CopyMethod.CtrlC: NormalizeInputState(); SendChordScancode(new ushort[] { 0x11 /*VK_CONTROL*/ }, 0x43 /*'C'*/); break;
                case CopyMethod.WmCopy: try { SendMessage(targetForMessage, WM_COPY, IntPtr.Zero, IntPtr.Zero); } catch { } break;
                case CopyMethod.CtrlInsert: NormalizeInputState(); SendChordScancode(new ushort[] { 0x11 /*VK_CONTROL*/ }, 0x2D /*VK_INSERT*/); break;
                case CopyMethod.CtrlShiftC: NormalizeInputState(); SendChordScancode(new ushort[] { 0x11 /*CTRL*/, 0x10 /*SHIFT*/ }, 0x43 /*'C'*/); break;
                case CopyMethod.SendKeysCtrlC:
                    try
                    {
                        int perAttempt = Math.Max(200, timeoutMs / 3);
                        for (int i = 0; i < 3; i++)
                        {
                            try { SendKeys.SendWait("^c"); SendKeys.Flush(); } catch { }
                            Thread.Sleep(60);
                            var t = WaitForClipboardTextChange(seqBefore, perAttempt);
                            if (!string.IsNullOrWhiteSpace(t)) { result = t!; try { Logger.Log($"TryCopyAndRead success after {i + 1} SendKeys attempts: len={t.Length}"); } catch { } return true; }
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
                    SendChordScancode(new ushort[] { 0x12 /*ALT*/ }, 0x45 /*'E'*/);
                    Thread.Sleep(150);
                    SendChordScancode(Array.Empty<ushort>(), 0x43 /*'C'*/);
                    break;
            }
            var text = WaitForClipboardTextChange(seqBefore, timeoutMs);
            if (!string.IsNullOrWhiteSpace(text)) { result = text!; try { Logger.Log($"TryCopyAndRead success: method={method}, len={text.Length}"); } catch { } return true; }
            else { try { Logger.Log($"TryCopyAndRead no change: method={method}"); } catch { } }
        }
        catch (Exception ex) { try { Logger.Log($"TryCopyAndRead({method}) error: {ex.GetType().Name}: {ex.Message}"); } catch { } }
        return false;
    }

    private static string? WaitForClipboardTextChange(uint seqBefore, int timeoutMs)
    {
        var start = Environment.TickCount;
        while (Environment.TickCount - start < timeoutMs)
        {
            uint seqNow = 0; try { seqNow = GetClipboardSequenceNumber(); } catch { }
            if (seqNow != 0 && seqNow != seqBefore)
            {
                try
                {
                    if (Clipboard.ContainsText())
                    {
                        var txt = Clipboard.GetText(TextDataFormat.UnicodeText);
                        if (!string.IsNullOrWhiteSpace(txt)) return txt;
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
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, U = new INPUTUNION { ki = new KEYBDINPUT { wVk = 0, wScan = (ushort)sc, dwFlags = KEYEVENTF_SCANCODE } } });
        }
        // Key down
        uint scKey = MapVirtualKey(keyVk, MAPVK_VK_TO_VSC);
        uint flagsDown = KEYEVENTF_SCANCODE;
        if (keyVk == VK_INSERT) flagsDown |= 0x0001; // EXTENDEDKEY
        inputs.Add(new INPUT { type = INPUT_KEYBOARD, U = new INPUTUNION { ki = new KEYBDINPUT { wVk = 0, wScan = (ushort)scKey, dwFlags = flagsDown } } });
        // Key up
        uint flagsUp = KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP;
        if (keyVk == VK_INSERT) flagsUp |= 0x0001;
        inputs.Add(new INPUT { type = INPUT_KEYBOARD, U = new INPUTUNION { ki = new KEYBDINPUT { wVk = 0, wScan = (ushort)scKey, dwFlags = flagsUp } } });
        // Modifiers up (reverse)
        for (int i = modifiersVk.Length - 1; i >= 0; i--)
        {
            uint sc = MapVirtualKey(modifiersVk[i], MAPVK_VK_TO_VSC);
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, U = new INPUTUNION { ki = new KEYBDINPUT { wVk = 0, wScan = (ushort)sc, dwFlags = KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP } } });
        }
        try { SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>()); } catch { }
        Thread.Sleep(30); // Reduced from 80ms for faster response
    }

    private static void NormalizeInputState()
    {
        try
        {
            // Release potentially held modifiers from the hotkey (Ctrl/Alt/Shift/Win)
            ushort[] mods = new ushort[] { 0x11 /*CTRL*/, 0x12 /*ALT*/, 0x10 /*SHIFT*/, 0x5B /*LWIN*/, 0x5C /*RWIN*/ };
            var inputs = new System.Collections.Generic.List<INPUT>();
            foreach (var m in mods)
            {
                bool down = (GetAsyncKeyState(m) & 0x8000) != 0;
                if (down)
                {
                    inputs.Add(new INPUT { type = INPUT_KEYBOARD, U = new INPUTUNION { ki = new KEYBDINPUT { wVk = m, dwFlags = KEYEVENTF_KEYUP } } });
                }
            }
            if (inputs.Count > 0)
            {
                try { SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>()); } catch { }
                Thread.Sleep(20); // Reduced from 40ms
            }
        }
        catch { }
    }

    public System.Threading.Tasks.Task<string> CaptureSelectionOrClipboardAsync(bool useClipboardFallback = false)
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
            try { Logger.Log($"Capture stats: {string.Join(", ", _captureStats.Select(kvp => $"{kvp.Key}={kvp.Value}"))}"); } catch { }
        }
    }

    private static IntPtr GetCachedWindowHandle(string windowClass)
    {
        if (_windowHandleCache.TryGetValue(windowClass, out IntPtr cachedHandle))
        {
            // Verify handle is still valid
            if (IsWindow(cachedHandle)) return cachedHandle;
            else _windowHandleCache.Remove(windowClass);
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

    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);

    private static System.Threading.Tasks.Task<T> RunInSta<T>(Func<T> func)
    {
        var tcs = new System.Threading.Tasks.TaskCompletionSource<T>();
        var th = new Thread(() =>
        {
            try { var r = func(); tcs.SetResult(r); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        th.IsBackground = true;
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
        return tcs.Task;
    }
}
