using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TailSlap;

/// <summary>
/// Types text into the foreground application's focused input using a hybrid
/// clipboard/SendKeys approach. Supports backspace corrections via common-prefix
/// algorithm and foreground window monitoring.
/// </summary>
public class TextTyper
{
    private readonly IClipboardService _clip;
    private readonly int _clipboardThreshold;

    private string _baselineText = "";
    private IntPtr _targetWindow = IntPtr.Zero;
    private readonly object _stateLock = new();

    #region P/Invoke Declarations

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint MAPVK_VK_TO_VSC = 0x0;
    private const uint VK_BACK = 0x08;

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

    #endregion

    /// <summary>
    /// Result of a text typing operation.
    /// </summary>
    public sealed class TypeResult
    {
        /// <summary>Whether the text was successfully delivered to the target application.</summary>
        public bool DeliverySuccess { get; init; }

        /// <summary>Whether the text is available on the clipboard (either from paste or fallback).</summary>
        public bool TextOnClipboard { get; init; }

        /// <summary>The text that was attempted to be typed.</summary>
        public string Text { get; init; } = "";

        /// <summary>The new text that needed to be typed (after backspace corrections).</summary>
        public string NewText { get; init; } = "";

        /// <summary>Number of backspaces sent for correction.</summary>
        public int BackspaceCount { get; init; }

        /// <summary>Whether the foreground window changed during typing.</summary>
        public bool WindowChanged { get; init; }
    }

    /// <summary>
    /// Creates a new TextTyper instance.
    /// </summary>
    /// <param name="clip">Clipboard service for clipboard operations.</param>
    /// <param name="clipboardThreshold">Text longer than this many characters uses clipboard paste instead of SendKeys. Default: 5.</param>
    public TextTyper(IClipboardService clip, int clipboardThreshold = 5)
    {
        _clip = clip ?? throw new ArgumentNullException(nameof(clip));
        _clipboardThreshold = clipboardThreshold;
    }

    /// <summary>
    /// Types text into the foreground application's focused input.
    /// Uses clipboard paste for text > clipboardThreshold chars, SendKeys for shorter text.
    /// Handles corrections via backspace when text differs from baseline.
    /// </summary>
    /// <param name="text">The full text that should be on screen after typing.</param>
    /// <param name="autoPaste">Whether to automatically paste via Ctrl+V. If false, text is placed on clipboard only.</param>
    /// <param name="foregroundWindow">The current foreground window handle. If null, uses GetForegroundWindow().</param>
    /// <param name="cancellationToken">Cancellation token for async operations.</param>
    /// <returns>A TypeResult describing what happened.</returns>
    public async Task<TypeResult> TypeAsync(
        string text,
        bool autoPaste = true,
        IntPtr? foregroundWindow = null,
        CancellationToken cancellationToken = default
    )
    {
        // Handle empty/null text
        if (string.IsNullOrEmpty(text))
        {
            return new TypeResult { DeliverySuccess = true, Text = text ?? "" };
        }

        var currentWindow = foregroundWindow ?? GetForegroundWindow();

        lock (_stateLock)
        {
            // Check if foreground window changed
            if (_targetWindow != IntPtr.Zero && currentWindow != _targetWindow)
            {
                try
                {
                    Logger.Log(
                        $"TextTyper: Foreground window changed from 0x{_targetWindow:X} to 0x{currentWindow:X}, resetting baseline"
                    );
                }
                catch { }

                // Reset baseline on window change
                _baselineText = "";
                _targetWindow = currentWindow;

                return new TypeResult
                {
                    WindowChanged = true,
                    DeliverySuccess = false,
                    Text = text,
                    TextOnClipboard = _clip.SetText(text),
                };
            }

            // Capture target window if not set
            if (_targetWindow == IntPtr.Zero)
            {
                _targetWindow = currentWindow;
            }
        }

        // Calculate corrections
        int backspaceCount = CalculateBackspaceCount(text);
        string newText = GetNewTextAfterCorrection(text);

        // Send backspaces if needed
        if (backspaceCount > 0)
        {
            try
            {
                Logger.Log($"TextTyper: Sending {backspaceCount} backspaces for correction");
            }
            catch { }

            SendBackspace(backspaceCount);
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }

        // Type the new text
        bool deliverySuccess = false;
        bool textOnClipboard = false;

        if (newText.Length > 0)
        {
            // Determine delivery method
            bool useClipboard =
                newText.Length > _clipboardThreshold
                || ContainsUnicode(newText)
                || ContainsNewline(newText);

            if (!autoPaste)
            {
                // AutoPaste disabled — just put text on clipboard
                textOnClipboard = _clip.SetText(text);
                deliverySuccess = textOnClipboard;
            }
            else if (useClipboard)
            {
                // Use clipboard paste for long text, Unicode, or multi-line
                deliverySuccess = await _clip.SetTextAndPasteAsync(newText).ConfigureAwait(false);
                textOnClipboard = true;

                if (!deliverySuccess)
                {
                    // Clipboard paste failed, try SendKeys fallback for non-Unicode, non-multiline
                    if (!ContainsUnicode(newText) && !ContainsNewline(newText))
                    {
                        try
                        {
                            TypeTextDirectly(newText);
                            deliverySuccess = true;
                        }
                        catch (Exception ex)
                        {
                            try
                            {
                                Logger.Log($"TextTyper: SendKeys fallback failed: {ex.Message}");
                            }
                            catch { }
                        }
                    }

                    if (!deliverySuccess)
                    {
                        // Ensure text is at least on clipboard as fallback
                        if (!textOnClipboard)
                        {
                            textOnClipboard = _clip.SetText(newText);
                        }

                        try
                        {
                            Logger.Log(
                                "TextTyper: All delivery methods failed, text preserved on clipboard"
                            );
                        }
                        catch { }

                        NotificationService.ShowInfo(
                            "Text delivery failed. The text is on your clipboard — paste manually with Ctrl+V."
                        );
                    }
                }
            }
            else
            {
                // Use SendKeys for short ASCII text (preserves clipboard)
                try
                {
                    TypeTextDirectly(newText);
                    deliverySuccess = true;
                }
                catch (Exception ex)
                {
                    try
                    {
                        Logger.Log($"TextTyper: SendKeys failed: {ex.Message}");
                    }
                    catch { }

                    // SendKeys failed, try clipboard as fallback
                    deliverySuccess = await _clip
                        .SetTextAndPasteAsync(newText)
                        .ConfigureAwait(false);
                    textOnClipboard = deliverySuccess;

                    if (!deliverySuccess)
                    {
                        // All methods failed — ensure text is at least on clipboard
                        textOnClipboard = _clip.SetText(newText);
                        try
                        {
                            Logger.Log(
                                "TextTyper: All delivery methods failed, text preserved on clipboard"
                            );
                        }
                        catch { }

                        NotificationService.ShowInfo(
                            "Text delivery failed. The text is on your clipboard — paste manually with Ctrl+V."
                        );
                    }
                }
            }
        }
        else
        {
            // No new text to type (only backspaces were needed)
            deliverySuccess = true;
        }

        // Update baseline
        lock (_stateLock)
        {
            _baselineText = text;
        }

        return new TypeResult
        {
            DeliverySuccess = deliverySuccess,
            TextOnClipboard = textOnClipboard,
            Text = text,
            NewText = newText,
            BackspaceCount = backspaceCount,
        };
    }

    /// <summary>
    /// Resets the text baseline and target window. Should be called when starting
    /// a new transcription session or when the foreground window changes.
    /// </summary>
    public void ResetBaseline()
    {
        lock (_stateLock)
        {
            _baselineText = "";
            _targetWindow = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Sets the baseline text (e.g., previously typed text that should be preserved).
    /// </summary>
    public void SetBaseline(string text, IntPtr targetWindow)
    {
        lock (_stateLock)
        {
            _baselineText = text ?? "";
            _targetWindow = targetWindow;
        }
    }

    #region Static Utility Methods

    /// <summary>
    /// Escapes special characters for use with SendKeys.SendWait.
    /// Characters +, ^, %, ~, (, ), [, ], {, } are wrapped in {}.
    /// Newlines are converted to {ENTER}. Carriage returns are stripped.
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

    /// <summary>
    /// Checks if the text contains non-ASCII characters (Unicode beyond basic ASCII range).
    /// Unicode text must be delivered via clipboard paste.
    /// </summary>
    public static bool ContainsUnicode(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        foreach (char c in text)
        {
            if (c > 127)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if the text contains newline characters.
    /// Multi-line text must be delivered via clipboard paste to preserve line breaks.
    /// </summary>
    public static bool ContainsNewline(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        return text.Contains('\n') || text.Contains('\r');
    }

    /// <summary>
    /// Calculates the length of the common prefix between two strings.
    /// Used by the correction algorithm to minimize backspace keystrokes.
    /// </summary>
    public static int GetCommonPrefixLength(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            return 0;

        int minLen = Math.Min(a.Length, b.Length);
        int commonLen = 0;
        for (int i = 0; i < minLen; i++)
        {
            if (a[i] == b[i])
                commonLen++;
            else
                break;
        }

        return commonLen;
    }

    #endregion

    #region Internal Methods (Testable)

    /// <summary>
    /// Calculates how many backspace keystrokes are needed to correct the baseline
    /// text to match the beginning of the new text.
    /// </summary>
    internal int CalculateBackspaceCount(string newText)
    {
        lock (_stateLock)
        {
            if (string.IsNullOrEmpty(_baselineText))
                return 0;

            int commonPrefixLen = GetCommonPrefixLength(_baselineText, newText);
            int backspaceCount = _baselineText.Length - commonPrefixLen;
            return Math.Max(0, backspaceCount);
        }
    }

    /// <summary>
    /// Gets the text that needs to be typed after backspace corrections.
    /// </summary>
    private string GetNewTextAfterCorrection(string newText)
    {
        lock (_stateLock)
        {
            if (string.IsNullOrEmpty(_baselineText))
                return newText;

            int commonPrefixLen = GetCommonPrefixLength(_baselineText, newText);
            if (commonPrefixLen >= newText.Length)
                return "";

            return newText.Substring(commonPrefixLen);
        }
    }

    /// <summary>
    /// Checks if the foreground window has changed since the target was set.
    /// </summary>
    internal bool IsForegroundWindowChanged()
    {
        lock (_stateLock)
        {
            if (_targetWindow == IntPtr.Zero)
                return false;

            var current = GetForegroundWindow();
            return current != _targetWindow;
        }
    }

    /// <summary>
    /// Checks if a specific window handle differs from the target window.
    /// </summary>
    internal bool CheckWindowChanged(IntPtr currentWindow)
    {
        lock (_stateLock)
        {
            if (_targetWindow == IntPtr.Zero)
                return false;

            return currentWindow != _targetWindow;
        }
    }

    #endregion

    #region Private Implementation

    internal virtual void SendBackspace(int count)
    {
        if (count <= 0)
            return;

        // Check window safety before sending keystrokes
        if (IsForegroundWindowChanged())
        {
            try
            {
                Logger.Log($"TextTyper: Skipping {count} backspaces, foreground window changed");
            }
            catch { }
            return;
        }

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
                        $"TextTyper: SendInput sent {sent}/{inputs.Length} events, falling back to SendKeys"
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
                Logger.Log($"TextTyper: SendBackspace failed: {ex.Message}");
            }
            catch { }
        }
    }

    internal virtual void TypeTextDirectly(string text)
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
                    $"TextTyper: Unicode SendInput sent {sent}/{inputs.Length} events, falling back to SendKeys"
                );
            }
            catch { }

            var escaped = EscapeForSendKeys(text);
            SendKeys.SendWait(escaped);
            SendKeys.Flush();
        }
    }

    private static INPUT[] BuildUnicodeInputs(string text)
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

    #endregion
}
