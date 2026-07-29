using System.Diagnostics;
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
}
