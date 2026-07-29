using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace TailSlap;

/// <summary>
/// Shared low-level keyboard input simulation using Win32 SendInput with
/// SendKeys fallback. Used by TextTyper and RealtimeTranscriptionController
/// to avoid duplicated P/Invoke and keystroke-sending logic.
/// </summary>
public static class NativeInputSimulator
{
    #region Win32 Structs and Constants

    public const int INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_SCANCODE = 0x0008;
    public const uint KEYEVENTF_UNICODE = 0x0004;
    public const uint MAPVK_VK_TO_VSC = 0x0;
    public const uint VK_BACK = 0x08;

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public int type;
        public INPUTUNION U;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION
    {
        [FieldOffset(0)]
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    #endregion

    #region P/Invoke

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    #endregion

    /// <summary>
    /// Waits for all hotkey modifier keys to be physically released.
    /// </summary>
    public static bool WaitForModifierRelease(int timeoutMs = 1000, int pollMs = 15)
    {
        return WaitForModifierRelease(IsKeyDown, timeoutMs, pollMs);
    }

    internal static bool WaitForModifierRelease(
        Func<ushort, bool> isKeyDown,
        int timeoutMs = 1000,
        int pollMs = 15
    )
    {
        ushort[] modifiers = { 0x11, 0x12, 0x10, 0x5B, 0x5C };
        long start = Environment.TickCount64;

        do
        {
            if (!modifiers.Any(isKeyDown))
            {
                return true;
            }

            Thread.Sleep(pollMs);
        } while (Environment.TickCount64 - start < timeoutMs);

        return !modifiers.Any(isKeyDown);
    }

    private static bool IsKeyDown(ushort virtualKey)
    {
        return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }

    /// <summary>
    /// Sends <paramref name="count"/> backspace keystrokes via scancode
    /// SendInput with a SendKeys fallback.
    /// </summary>
    public static void SendBackspace(int count)
    {
        if (count <= 0)
            return;

        try
        {
            var scanCode = (ushort)MapVirtualKey(VK_BACK, MAPVK_VK_TO_VSC);
            if (scanCode == 0)
            {
                scanCode = 0x0E; // Standard backspace scan code
            }

            var inputs = new INPUT[count * 2];
            for (int i = 0; i < count; i++)
            {
                int downIndex = i * 2;
                inputs[downIndex] = new INPUT
                {
                    type = INPUT_KEYBOARD,
                    U = new INPUTUNION
                    {
                        ki = new KEYBDINPUT { wScan = scanCode, dwFlags = KEYEVENTF_SCANCODE },
                    },
                };
                inputs[downIndex + 1] = new INPUT
                {
                    type = INPUT_KEYBOARD,
                    U = new INPUTUNION
                    {
                        ki = new KEYBDINPUT
                        {
                            wScan = scanCode,
                            dwFlags = KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP,
                        },
                    },
                };
            }

            var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
            if (sent != inputs.Length)
            {
                try
                {
                    Logger.Log(
                        $"NativeInputSimulator: SendInput sent {sent}/{inputs.Length} backspace events, falling back to SendKeys"
                    );
                }
                catch { }

                SendKeys.SendWait("{BS " + count + "}");
                SendKeys.Flush();
            }
        }
        catch (Exception ex)
        {
            try
            {
                Logger.Log($"NativeInputSimulator: SendBackspace failed: {ex.Message}");
            }
            catch { }
        }
    }

    /// <summary>
    /// Types <paramref name="text"/> into the foreground window via Unicode
    /// SendInput with a SendKeys fallback when SendInput is incomplete.
    /// Exceptions propagate to callers — they are responsible for fallback handling.
    /// </summary>
    public static void TypeTextDirectly(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var inputs = BuildUnicodeInputs(text);
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            try
            {
                Logger.Log(
                    $"NativeInputSimulator: Unicode SendInput sent {sent}/{inputs.Length} events, falling back to SendKeys"
                );
            }
            catch { }

            var escaped = EscapeForSendKeys(text);
            SendKeys.SendWait(escaped);
            SendKeys.Flush();
        }
    }

    /// <summary>
    /// Builds a paired key-down / key-up INPUT array for each character
    /// in <paramref name="text"/> using Unicode scan codes.
    /// </summary>
    public static INPUT[] BuildUnicodeInputs(string text)
    {
        var inputs = new INPUT[text.Length * 2];
        int inputIndex = 0;

        foreach (char c in text)
        {
            inputs[inputIndex++] = new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = c,
                        dwFlags = KEYEVENTF_UNICODE,
                    },
                },
            };
            inputs[inputIndex++] = new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = c,
                        dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP,
                    },
                },
            };
        }

        return inputs;
    }

    /// <summary>
    /// Escapes characters that are special in SendKeys syntax by wrapping
    /// them in braces. Converts newlines to {ENTER} and strips carriage returns.
    /// </summary>
    public static string EscapeForSendKeys(string text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var escaped = new StringBuilder(text.Length * 2);
        foreach (char c in text)
        {
            switch (c)
            {
                case '+':
                case '^':
                case '%':
                case '~':
                case '(':
                case ')':
                case '[':
                case ']':
                case '{':
                case '}':
                    escaped.Append('{').Append(c).Append('}');
                    break;
                case '\n':
                    escaped.Append("{ENTER}");
                    break;
                case '\r':
                    // Strip carriage return — \r\n becomes just {ENTER}
                    break;
                default:
                    escaped.Append(c);
                    break;
            }
        }

        return escaped.ToString();
    }
}
