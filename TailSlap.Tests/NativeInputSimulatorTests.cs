using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using TailSlap;
using Xunit;

public class NativeInputSimulatorTests
{
    [Fact]
    public void WaitForModifierRelease_AllModifiersUp_ReturnsImmediately()
    {
        var stopwatch = Stopwatch.StartNew();

        bool released = NativeInputSimulator.WaitForModifierRelease(_ => false, 1000, 15);

        stopwatch.Stop();
        Assert.True(released);
        Assert.True(stopwatch.ElapsedMilliseconds < 200);
    }

    [Fact]
    public void WaitForModifierRelease_HeldThenReleased_ReturnsTrue()
    {
        var stopwatch = Stopwatch.StartNew();

        bool released = NativeInputSimulator.WaitForModifierRelease(
            _ => stopwatch.ElapsedMilliseconds < 50,
            1000,
            10
        );

        Assert.True(released);
        Assert.True(stopwatch.ElapsedMilliseconds >= 50);
    }

    [Fact]
    public void WaitForModifierRelease_HeldForever_TimesOut()
    {
        var stopwatch = Stopwatch.StartNew();

        bool released = NativeInputSimulator.WaitForModifierRelease(_ => true, 100, 10);

        stopwatch.Stop();
        Assert.False(released);
        Assert.True(stopwatch.ElapsedMilliseconds >= 100);
    }

    [Fact]
    public void InputStructMatchesWin32InputSize()
    {
        int expectedSize = IntPtr.Size == 8 ? 40 : 28;

        Assert.Equal(expectedSize, Marshal.SizeOf<NativeInputSimulator.INPUT>());
    }

    [Theory]
    [InlineData(4, 0, 4)]
    [InlineData(4, 2, 3)]
    [InlineData(4, 3, 2)]
    [InlineData(4, 8, 0)]
    public void GetRemainingKeyPressCount_DoesNotRepeatPartiallySentInput(
        int requestedCount,
        int sentEventCount,
        int expectedRemaining
    )
    {
        Assert.Equal(
            expectedRemaining,
            NativeInputSimulator.GetRemainingKeyPressCount(requestedCount, sentEventCount)
        );
    }
}
