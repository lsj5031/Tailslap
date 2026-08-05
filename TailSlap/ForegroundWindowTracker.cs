using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TailSlap;

/// <summary>
/// Remembers the last external foreground window so a tray hotkey can recover the
/// user's target after Windows routes the hotkey message through TailSlap's hidden form.
/// </summary>
public sealed class ForegroundWindowTracker : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    /// <summary>
    /// Window classes that are never valid capture/paste targets: the taskbar,
    /// desktop, Start menu, and related shell surfaces. They become foreground
    /// whenever the tray menu or a notification is dismissed, and accepting them
    /// would make refinement target the shell instead of the user's app.
    /// </summary>
    internal static readonly string[] ShellWindowClasses =
    {
        "Shell_TrayWnd", // primary taskbar
        "Shell_SecondaryTrayWnd", // secondary monitor taskbar
        "Progman", // desktop (Program Manager)
        "WorkerW", // desktop icon/background layer
        "DV2ControlHost", // Windows 10 Start menu / search
        "TaskListThumbnailWnd", // taskbar preview thumbnails
        // NOTE: "Windows.UI.Core.CoreWindow" is deliberately NOT listed here: it is
        // the Win11 Start/search surface but also the inner window of legitimate
        // UWP apps (Calculator, Photos, Store), which we must not exclude.
        "NotifyIconOverflowWindow", // hidden-icons tray overflow
        "XamlExplorerHostIslandWindow", // Windows 11 desktop shell island
    };

    /// <summary>
    /// True when the window class belongs to the Windows shell and can never be
    /// the application the user selected text in.
    /// </summary>
    internal static bool IsShellWindowClass(string? windowClass)
    {
        if (string.IsNullOrWhiteSpace(windowClass))
        {
            return false;
        }

        foreach (var candidate in ShellWindowClasses)
        {
            if (string.Equals(windowClass, candidate, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private readonly uint _ownProcessId;
    private readonly object _sync = new();
    private readonly WinEventDelegate _callback;
    private IntPtr _hook;
    private IntPtr _lastExternalWindow;
    private bool _disposed;

    private delegate void WinEventDelegate(
        IntPtr hook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint eventThread,
        uint eventTime
    );

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint flags
    );

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    public ForegroundWindowTracker()
    {
        _ownProcessId = (uint)Environment.ProcessId;
        _callback = OnForegroundChanged;
        _lastExternalWindow = ReadExternalForegroundWindow();
        _hook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND,
            EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _callback,
            0,
            0,
            WINEVENT_OUTOFCONTEXT
        );

        if (_hook == IntPtr.Zero)
        {
            Logger.LogWarning(
                $"ForegroundWindowTracker: SetWinEventHook failed (error={Marshal.GetLastPInvokeError()}); using startup snapshot only"
            );
        }
    }

    /// <summary>
    /// Returns the last external window, or the current external foreground window
    /// when no event has been observed yet. Never returns TailSlap's own window
    /// or a shell surface. A stale (closed) remembered window is dropped.
    /// </summary>
    public IntPtr GetTargetWindow()
    {
        lock (_sync)
        {
            _lastExternalWindow = ResolveTarget(
                ReadExternalForegroundWindow(),
                _lastExternalWindow,
                IsExternalWindow
            );
            return _lastExternalWindow;
        }
    }

    /// <summary>
    /// Chooses the capture/paste target: prefer the current valid foreground window;
    /// otherwise fall back to the last remembered good window, dropping it when it
    /// has become invalid (e.g. the application closed since it was recorded).
    /// </summary>
    internal static IntPtr ResolveTarget(
        IntPtr currentForeground,
        IntPtr lastGoodWindow,
        Func<IntPtr, bool> isValidWindow
    )
    {
        if (currentForeground != IntPtr.Zero && isValidWindow(currentForeground))
        {
            return currentForeground;
        }

        return isValidWindow(lastGoodWindow) ? lastGoodWindow : IntPtr.Zero;
    }

    private void OnForegroundChanged(
        IntPtr hook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint eventThread,
        uint eventTime
    )
    {
        if (Volatile.Read(ref _disposed) || hwnd == IntPtr.Zero || !IsExternalWindow(hwnd))
        {
            return;
        }

        lock (_sync)
        {
            _lastExternalWindow = hwnd;
        }
    }

    private IntPtr ReadExternalForegroundWindow()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        return IsExternalWindow(hwnd) ? hwnd : IntPtr.Zero;
    }

    private bool IsExternalWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (
                !NativeMethods.TryGetWindowIdentity(
                    hwnd,
                    out uint processId,
                    out string windowClass
                )
            )
            {
                return false;
            }

            return processId != _ownProcessId && !IsShellWindowClass(windowClass);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_hook != IntPtr.Zero)
        {
            try
            {
                UnhookWinEvent(_hook);
            }
            catch { }
            _hook = IntPtr.Zero;
        }
    }
}
