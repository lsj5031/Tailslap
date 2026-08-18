using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

internal static class UiaProbeCommand
{
    public static bool IsProbeInvocation(string[] args) => UiaProbeProtocol.IsProbeInvocation(args);

    public static int Run(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (!UiaProbeProtocol.TryParseArgs(args, out var request, out var error))
        {
            Console.WriteLine(
                UiaProbeProtocol.Serialize(UiaProbeResponse.FromError(error ?? "Invalid args."))
            );
            return 2;
        }

        try
        {
            if (request!.Mode == UiaProbeMode.ControlValue)
            {
                // "value" must distinguish a readable-but-empty control (success
                // with empty text) from "no readable control found" (empty), so
                // paste verification can tell an empty field from an unreadable one.
                var (found, value) = TryGetFocusedControlValue(request.ForegroundWindowHandle);
                Console.WriteLine(
                    UiaProbeProtocol.Serialize(
                        found
                            ? UiaProbeResponse.Success(value ?? string.Empty)
                            : UiaProbeResponse.Empty()
                    )
                );
                return 0;
            }

            string? text = request.Mode switch
            {
                UiaProbeMode.Focused => TryGetFocusedSelection(request.ForegroundWindowHandle),
                UiaProbeMode.Caret => TryGetCaretSelection(request.ForegroundWindowHandle),
                UiaProbeMode.Deep => TryGetDeepSelection(request.ForegroundWindowHandle),
                _ => null,
            };

            Console.WriteLine(
                UiaProbeProtocol.Serialize(
                    string.IsNullOrWhiteSpace(text)
                        ? UiaProbeResponse.Empty()
                        : UiaProbeResponse.Success(text)
                )
            );
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                UiaProbeProtocol.Serialize(
                    UiaProbeResponse.FromError($"{ex.GetType().Name}: {ex.Message}")
                )
            );
            return 3;
        }
    }

    private static string? TryGetFocusedSelection(long? foregroundWindowHandle)
    {
        var uiaTask = RunInMtaForUia(() =>
        {
            var focused = AutomationElement.FocusedElement;
            if (focused != null)
            {
                var focusedSelection = TryReadSelectionFromElement(focused);
                if (!string.IsNullOrWhiteSpace(focusedSelection))
                {
                    return focusedSelection;
                }
            }

            var hwnd =
                foregroundWindowHandle.HasValue && foregroundWindowHandle.Value != 0
                    ? new IntPtr(foregroundWindowHandle.Value)
                    : NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return null;
            }

            var root = AutomationElement.FromHandle(hwnd);
            if (root == null)
            {
                return null;
            }

            var cond = new PropertyCondition(
                AutomationElement.IsTextPatternAvailableProperty,
                true
            );
            var element = root.FindFirst(TreeScope.Subtree, cond);
            return element == null ? null : TryReadSelectionFromElement(element);
        });

        return uiaTask.Wait(TimeSpan.FromMilliseconds(800)) ? uiaTask.Result : null;
    }

    private static string? TryGetCaretSelection(long? foregroundWindowHandle)
    {
        var uiaTask = RunInMtaForUia(() =>
        {
            var foreground =
                foregroundWindowHandle.HasValue && foregroundWindowHandle.Value != 0
                    ? new IntPtr(foregroundWindowHandle.Value)
                    : NativeMethods.GetForegroundWindow();
            if (foreground == IntPtr.Zero)
            {
                return null;
            }

            var info = new NativeMethods.GUITHREADINFO
            {
                cbSize = Marshal.SizeOf<NativeMethods.GUITHREADINFO>(),
            };
            uint threadId = NativeMethods.GetWindowThreadProcessId(foreground, out _);
            if (
                threadId == 0
                || !NativeMethods.GetGUIThreadInfo(threadId, ref info)
                || info.hwndCaret == IntPtr.Zero
            )
            {
                return null;
            }

            var point = new NativeMethods.POINT
            {
                X = info.rcCaret.Left + 1,
                Y = info.rcCaret.Top + ((info.rcCaret.Bottom - info.rcCaret.Top) / 2),
            };
            NativeMethods.ClientToScreen(info.hwndCaret, ref point);

            var element = AutomationElement.FromPoint(new System.Windows.Point(point.X, point.Y));
            for (
                AutomationElement? current = element;
                current != null;
                current = TreeWalker.RawViewWalker.GetParent(current)
            )
            {
                string? text = TryReadSelectionFromElement(current);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }

            return null;
        });

        return uiaTask.Wait(TimeSpan.FromMilliseconds(500)) ? uiaTask.Result : null;
    }

    private static string? TryGetDeepSelection(long? foregroundWindowHandle)
    {
        var uiaTask = RunInMtaForUia(() =>
        {
            string? caretSelection = TryGetSelectionAtCaretPoint(foregroundWindowHandle);
            if (!string.IsNullOrWhiteSpace(caretSelection))
            {
                return caretSelection;
            }

            var hwnd =
                foregroundWindowHandle.HasValue && foregroundWindowHandle.Value != 0
                    ? new IntPtr(foregroundWindowHandle.Value)
                    : NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return null;
            }

            var root = AutomationElement.FromHandle(hwnd);
            if (root == null)
            {
                return null;
            }

            var sw = Stopwatch.StartNew();
            int visited = 0;
            var stack = new Stack<AutomationElement>();
            stack.Push(root);

            while (stack.Count > 0 && visited < 3000 && sw.ElapsedMilliseconds < 400)
            {
                var element = stack.Pop();
                visited++;

                string? text = TryReadSelectionFromElement(element);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }

                AutomationElementCollection? children = null;
                try
                {
                    children = element.FindAll(TreeScope.Children, Condition.TrueCondition);
                }
                catch
                {
                    children = null;
                }

                if (children == null)
                {
                    continue;
                }

                for (int i = 0; i < children.Count; i++)
                {
                    var child = children[i];
                    if (child != null)
                    {
                        stack.Push(child);
                    }
                }
            }

            return null;
        });

        return uiaTask.Wait(TimeSpan.FromMilliseconds(800)) ? uiaTask.Result : null;
    }

    private static (bool Found, string? Value) TryGetFocusedControlValue(
        long? foregroundWindowHandle
    )
    {
        var uiaTask = RunInMtaForUia(() =>
        {
            // Browsers are hostile to single-element reads: the browser chrome
            // exposes ValuePatterns holding the page URL on chrome:// documents,
            // AutomationElement.FocusedElement resolves to that chrome element,
            // and empty ValuePatterns appear on toolbars. The paste target is
            // the Edit control with keyboard focus; search the window and its
            // descendants (browsers host the page tree in a renderer child
            // window), then fall back to the caret point and finally to the
            // joined values of every Edit so the caller can detect a change in
            // ANY of them.
            var hwnd =
                foregroundWindowHandle.HasValue && foregroundWindowHandle.Value != 0
                    ? new IntPtr(foregroundWindowHandle.Value)
                    : NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return null;
            }

            string? focusedEditValue = TryGetFocusedEditValue(hwnd);
            if (focusedEditValue != null)
            {
                return focusedEditValue;
            }

            // Custom editors without Edit controls: try the element at the
            // caret point, then the focused element, skipping chrome noise.
            string? caretValue = TryGetControlValueAtCaretPoint(hwnd);
            if (caretValue != null)
            {
                return caretValue;
            }

            string? editsValue = TryGetEditsValue(hwnd);
            if (editsValue != null)
            {
                return editsValue;
            }

            var focused = AutomationElement.FocusedElement;
            if (focused != null)
            {
                string? value = TryReadFullValue(focused);
                if (value != null && !LooksLikeLocation(value))
                {
                    return value;
                }
            }

            var root = AutomationElement.FromHandle(hwnd);
            if (root != null)
            {
                var cond = new PropertyCondition(
                    AutomationElement.IsTextPatternAvailableProperty,
                    true
                );
                var element = root.FindFirst(TreeScope.Subtree, cond);
                string? value = element == null ? null : TryReadFullValue(element);
                if (value != null && !LooksLikeLocation(value))
                {
                    return value;
                }
            }

            return null;
        });

        return uiaTask.Wait(TimeSpan.FromMilliseconds(800))
            ? (uiaTask.Result != null, uiaTask.Result)
            : (false, null);
    }

    private const string ValueSeparator = "\u0000";

    private static string? TryGetFocusedEditValue(IntPtr hwnd)
    {
        // The paste target is the Edit control that currently has keyboard
        // focus. Search the window and every descendant window tree (browsers
        // host the page tree in a renderer-process child window).
        var focusCond = new PropertyCondition(AutomationElement.HasKeyboardFocusProperty, true);
        var editCond = new PropertyCondition(
            AutomationElement.ControlTypeProperty,
            ControlType.Edit
        );
        var focusedEditCond = new AndCondition(editCond, focusCond);

        foreach (AutomationElement root in CollectTrees(hwnd))
        {
            try
            {
                var element = root.FindFirst(TreeScope.Subtree, focusedEditCond);
                if (element == null)
                {
                    continue;
                }

                string? value = TryReadFullValue(element);
                if (value != null)
                {
                    return value;
                }
            }
            catch
            {
                // Tree may be invalidated mid-walk; try the next window.
            }
        }

        return null;
    }

    private static string? TryGetEditsValue(IntPtr hwnd)
    {
        var values = new List<string>();
        var editCond = new PropertyCondition(
            AutomationElement.ControlTypeProperty,
            ControlType.Edit
        );

        foreach (AutomationElement root in CollectTrees(hwnd))
        {
            try
            {
                var edits = root.FindAll(TreeScope.Subtree, editCond);
                if (edits == null)
                {
                    continue;
                }

                foreach (AutomationElement edit in edits)
                {
                    if (edit == null)
                    {
                        continue;
                    }

                    string? value = TryReadFullValue(edit);
                    if (value != null)
                    {
                        values.Add(value);
                    }
                }
            }
            catch
            {
                // Tree may be invalidated mid-walk; try the next window.
            }
        }

        return values.Count == 0 ? null : string.Join(ValueSeparator, values);
    }

    private static List<AutomationElement> CollectTrees(IntPtr hwnd)
    {
        var roots = new List<AutomationElement>();

        AutomationElement? topLevel = null;
        try
        {
            topLevel = AutomationElement.FromHandle(hwnd);
        }
        catch
        {
            topLevel = null;
        }
        if (topLevel != null)
        {
            roots.Add(topLevel);
        }

        try
        {
            NativeMethods.EnumChildWindows(
                hwnd,
                (child, _) =>
                {
                    try
                    {
                        AutomationElement? childRoot = AutomationElement.FromHandle(child);
                        if (childRoot != null)
                        {
                            roots.Add(childRoot);
                        }
                    }
                    catch
                    {
                        // Window may not expose UIA; skip it.
                    }
                    return true;
                },
                IntPtr.Zero
            );
        }
        catch
        {
            // No child windows; the top-level tree alone is sufficient.
        }

        return roots;
    }

    private static bool LooksLikeLocation(string value)
    {
        string v = value.Trim();
        return v.Length == 0
            || v.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
            || v.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || v.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || v.StartsWith("chrome://", StringComparison.OrdinalIgnoreCase)
            || v.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
            || v.StartsWith("moz-extension://", StringComparison.OrdinalIgnoreCase)
            || v.StartsWith("edge://", StringComparison.OrdinalIgnoreCase)
            || v.StartsWith("brave://", StringComparison.OrdinalIgnoreCase)
            || v.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)
            || v.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetControlValueAtCaretPoint(IntPtr hwnd)
    {
        var info = new NativeMethods.GUITHREADINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.GUITHREADINFO>(),
        };
        uint threadId = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
        if (threadId == 0 || !NativeMethods.GetGUIThreadInfo(threadId, ref info))
        {
            return null;
        }

        IntPtr owner = info.hwndCaret != IntPtr.Zero ? info.hwndCaret : hwnd;
        var point = new NativeMethods.POINT
        {
            X = info.rcCaret.Left + ((info.rcCaret.Right - info.rcCaret.Left) / 2),
            Y = info.rcCaret.Top + ((info.rcCaret.Bottom - info.rcCaret.Top) / 2),
        };
        NativeMethods.ClientToScreen(owner, ref point);

        var element = AutomationElement.FromPoint(new System.Windows.Point(point.X, point.Y));
        for (
            AutomationElement? current = element;
            current != null;
            current = TreeWalker.RawViewWalker.GetParent(current)
        )
        {
            string? value = TryReadFullValue(current);
            if (value == null)
            {
                continue;
            }

            // Skip chrome noise: empty toolbars and browser locations. An
            // empty value is only accepted from an editable control.
            if (LooksLikeLocation(value))
            {
                continue;
            }

            var ctrl = current.Current.ControlType;
            if (value.Length > 0 || ctrl == ControlType.Edit || ctrl == ControlType.Document)
            {
                return value;
            }
        }

        return null;
    }

    private const int MaxControlValueChars = 2_000_000;

    private static string? TryReadFullValue(AutomationElement element)
    {
        try
        {
            // ValuePattern is the whole-value source for inputs/textareas; it is
            // preferred because it is a single string property, not a range.
            if (
                element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePatternObject)
                && valuePatternObject is ValuePattern valuePattern
            )
            {
                return valuePattern.Current.Value;
            }

            if (
                element.TryGetCurrentPattern(TextPattern.Pattern, out var textPatternObject)
                && textPatternObject is TextPattern textPattern
            )
            {
                var range = textPattern.DocumentRange;
                return range?.GetText(MaxControlValueChars);
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static string? TryGetSelectionAtCaretPoint(long? foregroundWindowHandle)
    {
        var hwnd =
            foregroundWindowHandle.HasValue && foregroundWindowHandle.Value != 0
                ? new IntPtr(foregroundWindowHandle.Value)
                : NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        var info = new NativeMethods.GUITHREADINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.GUITHREADINFO>(),
        };
        uint threadId = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
        if (threadId == 0 || !NativeMethods.GetGUIThreadInfo(threadId, ref info))
        {
            return null;
        }

        IntPtr owner = info.hwndCaret != IntPtr.Zero ? info.hwndCaret : hwnd;
        var point = new NativeMethods.POINT
        {
            X = info.rcCaret.Left + ((info.rcCaret.Right - info.rcCaret.Left) / 2),
            Y = info.rcCaret.Top + ((info.rcCaret.Bottom - info.rcCaret.Top) / 2),
        };
        NativeMethods.ClientToScreen(owner, ref point);

        var element = AutomationElement.FromPoint(new System.Windows.Point(point.X, point.Y));
        return element == null ? null : TryReadSelectionFromElement(element);
    }

    private static string? TryReadSelectionFromElement(AutomationElement element)
    {
        try
        {
            if (
                element.TryGetCurrentPattern(TextPattern.Pattern, out var textPatternObject)
                && textPatternObject is TextPattern textPattern
            )
            {
                var selection = textPattern.GetSelection();
                if (selection != null && selection.Length > 0)
                {
                    var text = selection[0].GetText(int.MaxValue);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }
                }
            }

            if (
                element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePatternObject)
                && valuePatternObject is ValuePattern valuePattern
            )
            {
                var value = valuePattern.Current.Value;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static Task<T> RunInMtaForUia<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>();
        var thread = new Thread(() =>
        {
            bool comInitialized = false;
            try
            {
                int hr = NativeMethods.CoInitializeEx(
                    IntPtr.Zero,
                    NativeMethods.COINIT_MULTITHREADED
                );
                comInitialized = hr == 0 || hr == 1;
                tcs.SetResult(func());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                if (comInitialized)
                {
                    try
                    {
                        NativeMethods.CoUninitialize();
                    }
                    catch { }
                }
            }
        });

        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return tcs.Task;
    }
}
