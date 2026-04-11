using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using TailSlap;

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
            string? text = request!.Mode switch
            {
                UiaProbeMode.Focused => TryGetFocusedSelection(request.ForegroundWindowHandle),
                UiaProbeMode.Caret => TryGetCaretSelection(),
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

    private static string? TryGetCaretSelection()
    {
        var uiaTask = RunInMtaForUia(() =>
        {
            var foreground = NativeMethods.GetForegroundWindow();
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
            string? caretSelection = TryGetSelectionAtCaretPoint();
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

    private static string? TryGetSelectionAtCaretPoint()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
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
                int hr = NativeMethods.CoInitializeEx(IntPtr.Zero, NativeMethods.COINIT_MULTITHREADED);
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
