using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

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
    public virtual async Task<TypeResult> TypeAsync(
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
        bool windowChanged = false;

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
                windowChanged = true;
            }

            // Capture target window if not set
            if (_targetWindow == IntPtr.Zero)
            {
                _targetWindow = currentWindow;
            }
        }

        if (windowChanged)
        {
            return new TypeResult
            {
                WindowChanged = true,
                DeliverySuccess = false,
                Text = text,
                TextOnClipboard = await _clip.SetTextAsync(text).ConfigureAwait(false),
            };
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
                textOnClipboard = await _clip.SetTextAsync(text).ConfigureAwait(false);
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
                            textOnClipboard = await _clip
                                .SetTextAsync(newText)
                                .ConfigureAwait(false);
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
                        textOnClipboard = await _clip.SetTextAsync(newText).ConfigureAwait(false);
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

        // Update baseline only when delivery succeeded so a later chunk
        // retries undelivered text via common-prefix logic.
        if (deliverySuccess)
        {
            lock (_stateLock)
            {
                _baselineText = text;
            }
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
        return NativeInputSimulator.EscapeForSendKeys(text);
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

        NativeInputSimulator.SendBackspace(count);
    }

    internal virtual void TypeTextDirectly(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        NativeInputSimulator.TypeTextDirectly(text);
    }

    #endregion
}
