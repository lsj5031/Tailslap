using System;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Moq;
using TailSlap;
using Xunit;

public class KeyboardHookTests
{
    private static HotkeyConfig CreateTestHotkey(uint modifiers = 0x0003, uint key = 0x54) // Ctrl+Alt+T
    {
        return new HotkeyConfig { Modifiers = modifiers, Key = key };
    }

    [Fact]
    public void KeyboardHook_CreatesInstanceWithValidConfig()
    {
        // Arrange & Act
        using var hook = new KeyboardHook(CreateTestHotkey());

        // Assert
        Assert.False(hook.IsInstalled);
    }

    [Fact]
    public void KeyboardHook_ThrowsWhenConfigIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new KeyboardHook(null!));
    }

    [Fact]
    public void Install_SetsIsInstalledTrue()
    {
        // Arrange
        using var hook = new KeyboardHook(CreateTestHotkey());

        // Act
        hook.Install();

        // Assert
        Assert.True(hook.IsInstalled);

        hook.Uninstall();
    }

    [Fact]
    public void Uninstall_SetsIsInstalledFalse()
    {
        // Arrange
        using var hook = new KeyboardHook(CreateTestHotkey());
        hook.Install();

        // Act
        hook.Uninstall();

        // Assert
        Assert.False(hook.IsInstalled);
    }

    [Fact]
    public void Uninstall_DoesNotThrowWhenNotInstalled()
    {
        // Arrange
        using var hook = new KeyboardHook(CreateTestHotkey());

        // Act & Assert — should not throw
        hook.Uninstall();
    }

    [Fact]
    public void Dispose_CleansUpHook()
    {
        // Arrange
        var hook = new KeyboardHook(CreateTestHotkey());
        hook.Install();

        // Act
        hook.Dispose();

        // Assert
        Assert.False(hook.IsInstalled);
    }

    [Fact]
    public void Dispose_CalledMultipleTimesDoesNotThrow()
    {
        // Arrange
        var hook = new KeyboardHook(CreateTestHotkey());
        hook.Install();

        // Act & Assert
        hook.Dispose();
        hook.Dispose();
    }

    [Fact]
    public void Reconfigure_UpdatesHotkeyConfig()
    {
        // Arrange
        using var hook = new KeyboardHook(CreateTestHotkey(0x0003, 0x54)); // Ctrl+Alt+T
        var newConfig = CreateTestHotkey(0x0003, 0x59); // Ctrl+Alt+Y

        // Act
        hook.Reconfigure(newConfig);

        // Assert — no exception means success; the hook should now match the new config
        // We verify indirectly via the MatchesConfig internal method
        Assert.True(InvokeMatchesConfig(hook, 0x0003, 0x59));
        Assert.False(InvokeMatchesConfig(hook, 0x0003, 0x54));
    }

    [Fact]
    public void Reconfigure_WhenInstalled_ReinstallsHook()
    {
        // Arrange
        using var hook = new KeyboardHook(CreateTestHotkey());
        hook.Install();
        var newConfig = CreateTestHotkey(0x0003, 0x59);

        // Act
        hook.Reconfigure(newConfig);

        // Assert — hook should still be installed
        Assert.True(hook.IsInstalled);
    }

    [Fact]
    public void Reconfigure_ThrowsWhenConfigIsNull()
    {
        // Arrange
        using var hook = new KeyboardHook(CreateTestHotkey());

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => hook.Reconfigure(null!));
    }

    [Fact]
    public void OnKeyDown_FiredWhenMatchingKeyCombination()
    {
        // Arrange
        using var hook = new KeyboardHook(CreateTestHotkey(0x0003, 0x54));
        bool keyDownFired = false;
        hook.OnKeyDown += () => keyDownFired = true;

        // Act — simulate the callback
        SimulateKeyDown(hook, 0x0003, 0x54);

        // Assert
        Assert.True(keyDownFired);
    }

    [Fact]
    public void OnKeyDown_NotFiredForNonMatchingKey()
    {
        // Arrange
        using var hook = new KeyboardHook(CreateTestHotkey(0x0003, 0x54));
        bool keyDownFired = false;
        hook.OnKeyDown += () => keyDownFired = true;

        // Act — simulate a different key (Ctrl+Alt+R instead of T)
        SimulateKeyDown(hook, 0x0003, 0x52);

        // Assert
        Assert.False(keyDownFired);
    }

    [Fact]
    public void OnKeyDown_NotFiredForNonMatchingModifiers()
    {
        // Arrange
        using var hook = new KeyboardHook(CreateTestHotkey(0x0003, 0x54)); // Ctrl+Alt
        bool keyDownFired = false;
        hook.OnKeyDown += () => keyDownFired = true;

        // Act — simulate with wrong modifiers (Ctrl only)
        SimulateKeyDown(hook, 0x0002, 0x54);

        // Assert
        Assert.False(keyDownFired);
    }

    [Fact]
    public void OnKeyUp_FiredWhenPrimaryKeyReleased()
    {
        // Arrange
        using var hook = new KeyboardHook(CreateTestHotkey(0x0003, 0x54));
        bool keyUpFired = false;
        hook.OnKeyUp += () => keyUpFired = true;

        // First trigger key-down
        SimulateKeyDown(hook, 0x0003, 0x54);

        // Act — simulate key-up for primary key
        SimulateKeyUp(hook, 0x54);

        // Assert
        Assert.True(keyUpFired);
    }

    [Fact]
    public void OnKeyUp_NotFiredWhenPrimaryKeyNotActive()
    {
        // Arrange
        using var hook = new KeyboardHook(CreateTestHotkey(0x0003, 0x54));
        bool keyUpFired = false;
        hook.OnKeyUp += () => keyUpFired = true;

        // Act — key-up without matching key-down
        SimulateKeyUp(hook, 0x54);

        // Assert
        Assert.False(keyUpFired);
    }

    [Fact]
    public void AutoRepeat_KeyDownSuppressedWhileKeyHeld()
    {
        // Arrange
        using var hook = new KeyboardHook(CreateTestHotkey(0x0003, 0x54));
        int keyDownCount = 0;
        hook.OnKeyDown += () => keyDownCount++;

        // Act — first key-down
        SimulateKeyDown(hook, 0x0003, 0x54);
        // Auto-repeat key-down (same key still held)
        SimulateKeyDown(hook, 0x0003, 0x54);
        SimulateKeyDown(hook, 0x0003, 0x54);

        // Assert — only one OnKeyDown should have fired
        Assert.Equal(1, keyDownCount);
    }

    [Fact]
    public void AutoRepeat_AllowedAfterKeyUp()
    {
        // Arrange
        using var hook = new KeyboardHook(CreateTestHotkey(0x0003, 0x54));
        int keyDownCount = 0;
        hook.OnKeyDown += () => keyDownCount++;

        // Act — first press cycle
        SimulateKeyDown(hook, 0x0003, 0x54);
        SimulateKeyUp(hook, 0x54);

        // Second press cycle
        SimulateKeyDown(hook, 0x0003, 0x54);
        SimulateKeyUp(hook, 0x54);

        // Assert — two OnKeyDown events should have fired
        Assert.Equal(2, keyDownCount);
    }

    [Fact]
    public void ModifierRelease_BeforePrimaryKey_KeyUpStillFires()
    {
        // Arrange
        using var hook = new KeyboardHook(CreateTestHotkey(0x0003, 0x54)); // Ctrl+Alt+T
        bool keyUpFired = false;
        hook.OnKeyUp += () => keyUpFired = true;

        // Trigger key-down with all modifiers
        SimulateKeyDown(hook, 0x0003, 0x54);

        // Release modifiers before primary key
        SimulateModifierChange(hook, 0x0000); // All modifiers released

        // Act — release primary key
        SimulateKeyUp(hook, 0x54);

        // Assert — key-up should still fire
        Assert.True(keyUpFired);
    }

    [Fact]
    public void Recording_ContinuesWhenModifiersReleased()
    {
        // Arrange
        using var hook = new KeyboardHook(CreateTestHotkey(0x0003, 0x54));
        bool keyUpFired = false;
        hook.OnKeyUp += () => keyUpFired = true;

        // Key-down with all modifiers
        SimulateKeyDown(hook, 0x0003, 0x54);

        // Release Ctrl modifier
        SimulateModifierChange(hook, 0x0001); // Only Alt remains

        // Assert — recording should still be active
        Assert.True(IsRecordingActive(hook));

        // Release Alt modifier too
        SimulateModifierChange(hook, 0x0000);

        // Assert — recording should still be active
        Assert.True(IsRecordingActive(hook));

        // Release primary key
        SimulateKeyUp(hook, 0x54);

        // Assert — key-up should fire
        Assert.True(keyUpFired);
        Assert.False(IsRecordingActive(hook));
    }

    [Fact]
    public void MaxDurationSafetyNet_FiresOnKeyUpAfterTimeout()
    {
        // Arrange
        using var hook = new KeyboardHook(CreateTestHotkey(0x0003, 0x54));

        // Simulate key-down
        SimulateKeyDown(hook, 0x0003, 0x54);

        // Manually set the key-down timestamp to be older than 60 seconds
        SetKeyDownTimestamp(hook, DateTime.UtcNow.AddSeconds(-61));

        // Act — simulate auto-stop check
        bool shouldAutoStop = InvokeShouldAutoStop(hook);

        // Assert
        Assert.True(shouldAutoStop);
    }

    [Fact]
    public void MaxDurationSafetyNet_DoesNotFireWithinTimeout()
    {
        // Arrange
        using var hook = new KeyboardHook(CreateTestHotkey(0x0003, 0x54));

        // Simulate key-down
        SimulateKeyDown(hook, 0x0003, 0x54);

        // Act — check immediately (should not auto-stop)
        bool shouldAutoStop = InvokeShouldAutoStop(hook);

        // Assert
        Assert.False(shouldAutoStop);
    }

    [Fact]
    public void MaxDuration_ReturnsCorrectValue()
    {
        // Arrange & Act
        using var hook = new KeyboardHook(CreateTestHotkey());

        // Assert — default max duration should be 60 seconds
        Assert.Equal(TimeSpan.FromSeconds(60), hook.MaxRecordingDuration);
    }

    [Fact]
    public void MaxDuration_CanBeCustomized()
    {
        // Arrange & Act
        using var hook = new KeyboardHook(CreateTestHotkey())
        {
            MaxRecordingDuration = TimeSpan.FromSeconds(30),
        };

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(30), hook.MaxRecordingDuration);
    }

    [Fact]
    public void IsRecordingActive_FalseInitially()
    {
        // Arrange
        using var hook = new KeyboardHook(CreateTestHotkey());

        // Assert
        Assert.False(IsRecordingActive(hook));
    }

    [Fact]
    public void IsRecordingActive_TrueAfterKeyDown()
    {
        // Arrange
        using var hook = new KeyboardHook(CreateTestHotkey(0x0003, 0x54));

        // Act
        SimulateKeyDown(hook, 0x0003, 0x54);

        // Assert
        Assert.True(IsRecordingActive(hook));
    }

    [Fact]
    public void IsRecordingActive_FalseAfterKeyUp()
    {
        // Arrange
        using var hook = new KeyboardHook(CreateTestHotkey(0x0003, 0x54));
        SimulateKeyDown(hook, 0x0003, 0x54);

        // Act
        SimulateKeyUp(hook, 0x54);

        // Assert
        Assert.False(IsRecordingActive(hook));
    }

    [Fact]
    public void Install_CalledMultipleTimesDoesNotLeak()
    {
        // Arrange
        using var hook = new KeyboardHook(CreateTestHotkey());

        // Act — install multiple times
        hook.Install();
        hook.Install();
        hook.Install();

        // Assert — should be installed
        Assert.True(hook.IsInstalled);

        // Cleanup
        hook.Uninstall();
    }

    [Fact]
    public void ForceStop_FiresOnKeyUpAndResetsState()
    {
        // Arrange
        using var hook = new KeyboardHook(CreateTestHotkey(0x0003, 0x54));
        bool keyUpFired = false;
        hook.OnKeyUp += () => keyUpFired = true;

        SimulateKeyDown(hook, 0x0003, 0x54);
        Assert.True(IsRecordingActive(hook));

        // Act — force stop (e.g., from max duration timer)
        hook.ForceStop();

        // Assert
        Assert.True(keyUpFired);
        Assert.False(IsRecordingActive(hook));
    }

    [Fact]
    public void ForceStop_DoesNothingWhenNotRecording()
    {
        // Arrange
        using var hook = new KeyboardHook(CreateTestHotkey(0x0003, 0x54));
        bool keyUpFired = false;
        hook.OnKeyUp += () => keyUpFired = true;

        // Act — force stop without any recording
        hook.ForceStop();

        // Assert — no event fired
        Assert.False(keyUpFired);
    }

    #region Helper methods for testing via reflection

    private static void SimulateKeyDown(KeyboardHook hook, uint modifiers, uint vk)
    {
        var method = typeof(KeyboardHook).GetMethod(
            "ProcessKeyDown",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.NotNull(method);
        method!.Invoke(hook, new object[] { modifiers, vk });
    }

    private static void SimulateKeyUp(KeyboardHook hook, uint vk)
    {
        var method = typeof(KeyboardHook).GetMethod(
            "ProcessKeyUp",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.NotNull(method);
        method!.Invoke(hook, new object[] { vk });
    }

    private static void SimulateModifierChange(KeyboardHook hook, uint currentModifiers)
    {
        var method = typeof(KeyboardHook).GetMethod(
            "ProcessModifierChange",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.NotNull(method);
        method!.Invoke(hook, new object[] { currentModifiers });
    }

    private static bool IsRecordingActive(KeyboardHook hook)
    {
        var prop = typeof(KeyboardHook).GetProperty(
            "IsRecordingActive",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public
        );
        if (prop != null)
            return (bool)prop.GetValue(hook)!;

        var field = typeof(KeyboardHook).GetField(
            "_isRecordingActive",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.NotNull(field);
        return (bool)field!.GetValue(hook)!;
    }

    private static void SetKeyDownTimestamp(KeyboardHook hook, DateTime timestamp)
    {
        var field = typeof(KeyboardHook).GetField(
            "_keyDownTimestamp",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.NotNull(field);
        field!.SetValue(hook, timestamp);
    }

    private static bool InvokeShouldAutoStop(KeyboardHook hook)
    {
        var method = typeof(KeyboardHook).GetMethod(
            "ShouldAutoStop",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.NotNull(method);
        return (bool)method!.Invoke(hook, null)!;
    }

    private static bool InvokeMatchesConfig(KeyboardHook hook, uint modifiers, uint vk)
    {
        var method = typeof(KeyboardHook).GetMethod(
            "MatchesConfig",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.NotNull(method);
        return (bool)method!.Invoke(hook, new object[] { modifiers, vk })!;
    }

    #endregion
}
