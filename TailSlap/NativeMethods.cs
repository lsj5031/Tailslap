using System;
using System.Runtime.InteropServices;
using System.Text;

internal static class NativeMethods
{
    public const uint COINIT_MULTITHREADED = 0x0;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint TOKEN_QUERY = 0x0008;
    public const int TokenElevation = 20;

    private const int SW_RESTORE = 9;

    [StructLayout(LayoutKind.Sequential)]
    internal struct GUITHREADINFO
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
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    internal static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [DllImport("user32.dll")]
    internal static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    internal static bool TryGetWindowIdentity(
        IntPtr hWnd,
        out uint processId,
        out string windowClass
    )
    {
        processId = 0;
        windowClass = string.Empty;

        if (hWnd == IntPtr.Zero || !IsWindow(hWnd))
            return false;

        try
        {
            if (GetWindowThreadProcessId(hWnd, out processId) == 0 || processId == 0)
                return false;

            var className = new StringBuilder(128);
            int length = GetClassName(hWnd, className, className.Capacity);
            windowClass = length > 0 ? className.ToString() : string.Empty;
            return true;
        }
        catch
        {
            processId = 0;
            windowClass = string.Empty;
            return false;
        }
    }

    internal static bool IsWindowIdentityMatch(
        IntPtr hWnd,
        uint expectedProcessId,
        string expectedWindowClass
    )
    {
        return TryGetWindowIdentity(hWnd, out uint actualProcessId, out string actualWindowClass)
            && actualProcessId == expectedProcessId
            && string.Equals(actualWindowClass, expectedWindowClass, StringComparison.Ordinal);
    }

    /// <summary>
    /// Restores and foregrounds a previously captured window only when its HWND
    /// still belongs to the same process. The caller must verify foreground focus
    /// after this call because Windows may reject focus changes.
    /// </summary>
    internal static bool TryRestoreWindow(
        IntPtr hWnd,
        uint expectedProcessId,
        string expectedWindowClass
    )
    {
        if (
            hWnd == IntPtr.Zero
            || expectedProcessId == 0
            || string.IsNullOrWhiteSpace(expectedWindowClass)
            || !IsWindowIdentityMatch(hWnd, expectedProcessId, expectedWindowClass)
        )
        {
            return false;
        }

        try
        {
            if (IsIconic(hWnd))
                ShowWindow(hWnd, SW_RESTORE);

            if (SetForegroundWindowWithInputAttach(hWnd))
                return true;

            return GetForegroundWindow() == hWnd;
        }
        catch
        {
            return false;
        }
    }

    private static bool SetForegroundWindowWithInputAttach(IntPtr hWnd)
    {
        uint currentThreadId = GetCurrentThreadId();
        uint targetThreadId = GetWindowThreadProcessId(hWnd, out _);
        IntPtr foreground = GetForegroundWindow();
        uint foregroundThreadId =
            foreground != IntPtr.Zero ? GetWindowThreadProcessId(foreground, out _) : 0;

        // A background tray process is normally denied SetForegroundWindow by the
        // foreground lock; temporarily attaching to the input queues of the current
        // foreground thread (and the target thread) lifts that restriction.
        bool attachedForeground =
            foregroundThreadId != 0
            && foregroundThreadId != currentThreadId
            && AttachThreadInput(currentThreadId, foregroundThreadId, true);
        bool attachedTarget =
            targetThreadId != 0
            && targetThreadId != currentThreadId
            && targetThreadId != foregroundThreadId
            && AttachThreadInput(currentThreadId, targetThreadId, true);

        try
        {
            BringWindowToTop(hWnd);
            return SetForegroundWindow(hWnd);
        }
        finally
        {
            if (attachedTarget)
                AttachThreadInput(currentThreadId, targetThreadId, false);
            if (attachedForeground)
                AttachThreadInput(currentThreadId, foregroundThreadId, false);
        }
    }

    [DllImport("ole32.dll")]
    internal static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    internal static extern void CoUninitialize();

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(
        uint dwDesiredAccess,
        bool bInheritHandle,
        uint dwProcessId
    );

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool OpenProcessToken(
        IntPtr processHandle,
        uint desiredAccess,
        out IntPtr tokenHandle
    );

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        out int tokenInformation,
        int tokenInformationLength,
        out int returnLength
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr hObject);
}
