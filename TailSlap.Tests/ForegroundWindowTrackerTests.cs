using TailSlap;
using Xunit;

namespace TailSlap.Tests;

public sealed class ForegroundWindowTrackerTests
{
    [Theory]
    [InlineData("Shell_TrayWnd")]
    [InlineData("Shell_SecondaryTrayWnd")]
    [InlineData("Progman")]
    [InlineData("WorkerW")]
    [InlineData("DV2ControlHost")]
    [InlineData("TaskListThumbnailWnd")]
    [InlineData("NotifyIconOverflowWindow")]
    [InlineData("XamlExplorerHostIslandWindow")]
    public void IsShellWindowClass_RejectsShellWindows(string windowClass)
    {
        Assert.True(ForegroundWindowTracker.IsShellWindowClass(windowClass));
    }

    [Theory]
    [InlineData("MozillaWindowClass")]
    [InlineData("Chrome_WidgetWin_1")]
    [InlineData("WeChatMainWndForPC")]
    [InlineData("Notepad")]
    [InlineData("Windows.UI.Core.CoreWindow")] // real UWP apps share this class with the Start menu
    [InlineData("")]
    [InlineData(null)]
    public void IsShellWindowClass_AcceptsRealApplicationWindows(string? windowClass)
    {
        Assert.False(ForegroundWindowTracker.IsShellWindowClass(windowClass));
    }

    [Fact]
    public void ResolveTarget_PrefersValidCurrentForeground()
    {
        var current = new IntPtr(0x100);
        var last = new IntPtr(0x200);

        var result = ForegroundWindowTracker.ResolveTarget(current, last, hwnd => hwnd == current);

        Assert.Equal(current, result);
    }

    [Fact]
    public void ResolveTarget_FallsBackToLastGoodWhenCurrentIsShell()
    {
        var current = new IntPtr(0x100);
        var last = new IntPtr(0x200);

        // Current foreground is a shell window (invalid); the last real app wins.
        var result = ForegroundWindowTracker.ResolveTarget(current, last, hwnd => hwnd == last);

        Assert.Equal(last, result);
    }

    [Fact]
    public void ResolveTarget_DropsStaleLastWindow()
    {
        var last = new IntPtr(0x200);

        var result = ForegroundWindowTracker.ResolveTarget(IntPtr.Zero, last, _ => false);

        Assert.Equal(IntPtr.Zero, result);
    }

    [Fact]
    public void ResolveTarget_ReturnsZeroWhenNoValidWindow()
    {
        var result = ForegroundWindowTracker.ResolveTarget(IntPtr.Zero, IntPtr.Zero, _ => true);

        Assert.Equal(IntPtr.Zero, result);
    }
}
