using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace TailSlap;

/// <summary>
/// Low-level keyboard hook (WH_KEYBOARD_LL) that detects key-down and key-up events
/// for the configured transcriber hotkey. Handles auto-repeat suppression, modifier
/// key tracking, and maximum recording duration safety net.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    #region P/Invoke Declarations

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYUP = 0x0105;

    // Modifier virtual key codes
    private const uint VK_LSHIFT = 0xA0;
    private const uint VK_RSHIFT = 0xA1;
    private const uint VK_LCONTROL = 0xA2;
    private const uint VK_RCONTROL = 0xA3;
    private const uint VK_LMENU = 0xA4; // Left Alt
    private const uint VK_RMENU = 0xA5; // Right Alt
    private const uint VK_LWIN = 0x5B;
    private const uint VK_RWIN = 0x5C;

    // RegisterHotKey modifier flags
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook,
        HookProc lpfn,
        IntPtr hMod,
        uint dwThreadId
    );

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(
        IntPtr hhk,
        int nCode,
        IntPtr wParam,
        IntPtr lParam
    );

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    #endregion

    private IntPtr _hookId = IntPtr.Zero;
    private HookProc? _hookCallback;
    private HotkeyConfig _config;
    private bool _disposed;
    private bool _isRecordingActive;
    private bool _primaryKeyHeld;
    private DateTime _keyDownTimestamp;
    private uint _lastKnownModifiers;
    private System.Threading.Timer? _maxDurationTimer;

    /// <summary>
    /// Maximum recording duration before auto-stop (safety net for missing key-up).
    /// Default: 60 seconds.
    /// </summary>
    public TimeSpan MaxRecordingDuration { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Whether the hook is currently installed.
    /// </summary>
    public bool IsInstalled => _hookId != IntPtr.Zero;

    /// <summary>
    /// Fired when the configured hotkey combination is pressed (first occurrence only,
    /// auto-repeat is suppressed).
    /// </summary>
    public event Action? OnKeyDown;

    /// <summary>
    /// Fired when the primary key of the configured hotkey is released,
    /// or when the max recording duration safety net triggers.
    /// </summary>
    public event Action? OnKeyUp;

    public KeyboardHook(HotkeyConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Installs the low-level keyboard hook. Must be called from the UI thread
    /// to ensure callbacks are processed on the UI thread.
    /// </summary>
    public void Install()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_hookId != IntPtr.Zero)
            return; // Already installed

        _hookCallback = HookCallback;

        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        var moduleHandle = GetModuleHandle(module?.ModuleName ?? "");

        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _hookCallback, moduleHandle, 0);

        if (_hookId == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            _hookCallback = null;
            string message =
                $"Failed to install keyboard hook (SetWindowsHookEx returned error {error}). "
                + "The transcriber hotkey feature will be disabled.";
            try
            {
                Logger.Log($"KeyboardHook.Install failed: {message}");
            }
            catch { }

            NotificationService.ShowError(
                "Failed to install keyboard hook. Transcription hotkey will not work."
            );
            return;
        }

        try
        {
            Logger.Log("KeyboardHook installed successfully");
        }
        catch { }
    }

    /// <summary>
    /// Uninstalls the low-level keyboard hook.
    /// </summary>
    public void Uninstall()
    {
        if (_hookId != IntPtr.Zero)
        {
            try
            {
                UnhookWindowsHookEx(_hookId);
            }
            catch { }

            _hookId = IntPtr.Zero;
            _hookCallback = null;

            try
            {
                Logger.Log("KeyboardHook uninstalled");
            }
            catch { }
        }

        StopMaxDurationTimer();
        _isRecordingActive = false;
        _primaryKeyHeld = false;
    }

    /// <summary>
    /// Updates the hotkey configuration and reinstalls the hook if currently installed.
    /// </summary>
    public void Reconfigure(HotkeyConfig newConfig)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(newConfig);

        _config = newConfig;

        if (_hookId != IntPtr.Zero)
        {
            Uninstall();
        }
        Install();

        try
        {
            Logger.Log(
                $"KeyboardHook reconfigured: mods={newConfig.Modifiers}, key={newConfig.Key}"
            );
        }
        catch { }
    }

    /// <summary>
    /// Forces an auto-stop (e.g., from max duration timer). Fires OnKeyUp and resets state.
    /// </summary>
    public void ForceStop()
    {
        if (!_isRecordingActive)
            return;

        try
        {
            Logger.Log("KeyboardHook force stop triggered (max duration or external)");
        }
        catch { }

        _isRecordingActive = false;
        _primaryKeyHeld = false;
        StopMaxDurationTimer();
        OnKeyUp?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Uninstall();
    }

    /// <summary>
    /// Whether the configured hotkey is modifier-only (Key == 0), meaning it triggers
    /// when the modifier combination is fully held rather than requiring a primary key.
    /// </summary>
    internal bool IsModifierOnlyHotkey => _config.Key == 0;

    #region Internal methods (testable via reflection)

    /// <summary>
    /// Processes a key-down event. Fires OnKeyDown if the configured hotkey combination
    /// matches and auto-repeat is suppressed.
    /// For modifier-only hotkeys (Key == 0), triggers when all configured modifiers are held.
    /// </summary>
    internal void ProcessKeyDown(uint currentModifiers, uint vk)
    {
        if (_disposed)
            return;

        if (IsModifierOnlyHotkey)
        {
            ProcessModifierOnlyKeyDown(currentModifiers);
            return;
        }

        // Check if this key matches our configured hotkey
        if (!MatchesConfig(currentModifiers, vk))
            return;

        // Auto-repeat suppression: ignore repeated key-down while key is held
        if (_primaryKeyHeld)
            return;

        _primaryKeyHeld = true;
        _isRecordingActive = true;
        _lastKnownModifiers = currentModifiers;
        _keyDownTimestamp = DateTime.UtcNow;

        StartMaxDurationTimer();

        try
        {
            Logger.Log("KeyboardHook: configured hotkey key-down detected");
        }
        catch { }

        OnKeyDown?.Invoke();
    }

    /// <summary>
    /// Handles key-down for modifier-only hotkeys. Fires OnKeyDown when all configured
    /// modifiers are simultaneously held.
    /// </summary>
    private void ProcessModifierOnlyKeyDown(uint currentModifiers)
    {
        // Check if all configured modifiers are now held
        if ((currentModifiers & _config.Modifiers) != _config.Modifiers)
            return;

        // Auto-repeat suppression
        if (_primaryKeyHeld)
            return;

        _primaryKeyHeld = true;
        _isRecordingActive = true;
        _lastKnownModifiers = currentModifiers;
        _keyDownTimestamp = DateTime.UtcNow;

        StartMaxDurationTimer();

        try
        {
            Logger.Log("KeyboardHook: modifier-only hotkey activated");
        }
        catch { }

        OnKeyDown?.Invoke();
    }

    /// <summary>
    /// Processes a key-up event. Fires OnKeyUp if the primary key of the configured
    /// hotkey is released (regardless of current modifier state).
    /// For modifier-only hotkeys, delegates to modifier change handling.
    /// </summary>
    internal void ProcessKeyUp(uint vk)
    {
        if (_disposed)
            return;

        if (IsModifierOnlyHotkey)
        {
            // For modifier-only hotkeys, key-up of non-modifiers is not relevant
            return;
        }

        // Only fire key-up for our configured primary key
        if (vk != _config.Key)
            return;

        // Only fire if we had an active recording
        if (!_primaryKeyHeld)
            return;

        _primaryKeyHeld = false;
        _isRecordingActive = false;
        StopMaxDurationTimer();

        try
        {
            Logger.Log("KeyboardHook: configured hotkey key-up detected");
        }
        catch { }

        OnKeyUp?.Invoke();
    }

    /// <summary>
    /// Updates the tracked modifier state. Called when modifier keys change
    /// (pressed or released).
    /// For modifier-only hotkeys (Key == 0), fires OnKeyUp when any required modifier is released.
    /// For standard hotkeys, does NOT affect recording state — recording continues
    /// even if all modifiers are released before the primary key.
    /// </summary>
    internal void ProcessModifierChange(uint currentModifiers)
    {
        _lastKnownModifiers = currentModifiers;

        // For modifier-only hotkeys, releasing any required modifier triggers key-up
        if (IsModifierOnlyHotkey && _primaryKeyHeld)
        {
            bool anyRequiredReleased = (_config.Modifiers & currentModifiers) != _config.Modifiers;
            if (anyRequiredReleased)
            {
                _primaryKeyHeld = false;
                _isRecordingActive = false;
                StopMaxDurationTimer();

                try
                {
                    Logger.Log(
                        "KeyboardHook: modifier-only hotkey deactivated (modifier released)"
                    );
                }
                catch { }

                OnKeyUp?.Invoke();
            }
        }
    }

    /// <summary>
    /// Checks whether the recording should be auto-stopped due to max duration.
    /// </summary>
    internal bool ShouldAutoStop()
    {
        if (!_isRecordingActive)
            return false;

        return DateTime.UtcNow - _keyDownTimestamp >= MaxRecordingDuration;
    }

    /// <summary>
    /// Checks if the given modifiers and virtual key code match the configured hotkey.
    /// </summary>
    internal bool MatchesConfig(uint modifiers, uint vk)
    {
        return vk == _config.Key && modifiers == _config.Modifiers;
    }

    #endregion

    #region Private Implementation

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int message = wParam.ToInt32();
            uint vk = (uint)Marshal.ReadInt32(lParam);

            if (message == WM_KEYDOWN || message == WM_SYSKEYDOWN)
            {
                uint currentModifiers = GetCurrentModifiers();
                ProcessKeyDown(currentModifiers, vk);
            }
            else if (message == WM_KEYUP || message == WM_SYSKEYUP)
            {
                if (IsModifierKey(vk))
                {
                    // Modifier key released — update tracking but don't affect recording
                    uint currentModifiers = GetCurrentModifiers();
                    ProcessModifierChange(currentModifiers);
                }
                else
                {
                    ProcessKeyUp(vk);
                }
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private static uint GetCurrentModifiers()
    {
        uint modifiers = 0;

        if (
            (GetAsyncKeyState(VK_LCONTROL) & 0x8000) != 0
            || (GetAsyncKeyState(VK_RCONTROL) & 0x8000) != 0
        )
        {
            modifiers |= MOD_CONTROL;
        }

        if (
            (GetAsyncKeyState(VK_LMENU) & 0x8000) != 0
            || (GetAsyncKeyState(VK_RMENU) & 0x8000) != 0
        )
        {
            modifiers |= MOD_ALT;
        }

        if (
            (GetAsyncKeyState(VK_LSHIFT) & 0x8000) != 0
            || (GetAsyncKeyState(VK_RSHIFT) & 0x8000) != 0
        )
        {
            modifiers |= MOD_SHIFT;
        }

        if ((GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0)
        {
            modifiers |= MOD_WIN;
        }

        return modifiers;
    }

    private static bool IsModifierKey(uint vk)
    {
        return vk is >= VK_LSHIFT and <= VK_RMENU || vk == VK_LWIN || vk == VK_RWIN;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(uint vKey);

    private void StartMaxDurationTimer()
    {
        StopMaxDurationTimer();

        _maxDurationTimer = new System.Threading.Timer(
            _ =>
            {
                try
                {
                    if (ShouldAutoStop())
                    {
                        ForceStop();
                    }
                }
                catch { }
            },
            null,
            (int)MaxRecordingDuration.TotalMilliseconds,
            Timeout.Infinite
        );
    }

    private void StopMaxDurationTimer()
    {
        var timer = _maxDurationTimer;
        if (timer != null)
        {
            _maxDurationTimer = null;
            try
            {
                timer.Dispose();
            }
            catch { }
        }
    }

    #endregion
}
