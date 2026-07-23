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

    #region RightAltOnly modifier-only hotkey tests

    private const uint VK_RMENU = 0xA5;
    private const uint VK_LMENU = 0xA4;

    private static HotkeyConfig CreateRightAltOnlyHotkey()
    {
        return new HotkeyConfig
        {
            Modifiers = 0x0001, // MOD_ALT
            Key = 0, // modifier-only
            RightAltOnly = true,
        };
    }

    [Fact]
    public void RightAltOnly_FiresOnKeyDown_WhenRightAltHeld()
    {
        // Arrange
        using var hook = new KeyboardHook(CreateRightAltOnlyHotkey());
        bool keyDownFired = false;
        hook.OnKeyDown += () => keyDownFired = true;

        // Simulate hook callback setting _rightAltHeld before key-down
        SetRightAltHeld(hook, true);

        // Act — simulate key-down for right Alt (modifiers passed don't matter for RightAltOnly)
        SimulateKeyDown(hook, 0x0001, VK_RMENU);

        // Assert
        Assert.True(keyDownFired);
    }

    [Fact]
    public void RightAltOnly_FiresOnKeyDown_WhenRightAltVkPassed()
    {
        // Arrange — test the fallback path: vk==VK_RMENU works even if _rightAltHeld is stale
        using var hook = new KeyboardHook(CreateRightAltOnlyHotkey());
        bool keyDownFired = false;
        hook.OnKeyDown += () => keyDownFired = true;

        // Don't set _rightAltHeld — rely on the vk fallback
        // Act
        SimulateKeyDown(hook, 0x0001, VK_RMENU);

        // Assert
        Assert.True(keyDownFired);
    }

    [Fact]
    public void RightAltOnly_DoesNotFire_WhenLeftAltPressed()
    {
        // Arrange
        using var hook = new KeyboardHook(CreateRightAltOnlyHotkey());
        bool keyDownFired = false;
        hook.OnKeyDown += () => keyDownFired = true;

        // _rightAltHeld is false, vk is left Alt — neither condition should match
        // Act
        SimulateKeyDown(hook, 0x0001, VK_LMENU);

        // Assert
        Assert.False(keyDownFired);
    }

    [Fact]
    public void RightAltOnly_FiresOnKeyUp_WhenRightAltReleased()
    {
        // Arrange
        using var hook = new KeyboardHook(CreateRightAltOnlyHotkey());
        bool keyUpFired = false;
        hook.OnKeyUp += () => keyUpFired = true;

        // Activate recording first
        SetRightAltHeld(hook, true);
        SimulateKeyDown(hook, 0x0001, VK_RMENU);

        // Clear _rightAltHeld BEFORE calling ProcessModifierChange (as HookCallback does)
        SetRightAltHeld(hook, false);

        // Act — simulate right Alt release via modifier change
        SimulateModifierChange(hook, 0x0000, VK_RMENU);

        // Assert
        Assert.True(keyUpFired);
    }

    [Fact]
    public void RightAltOnly_DoesNotFireOnKeyUp_WhenLeftAltReleased()
    {
        // Arrange
        using var hook = new KeyboardHook(CreateRightAltOnlyHotkey());
        bool keyUpFired = false;
        hook.OnKeyUp += () => keyUpFired = true;

        // Activate recording via right Alt
        SetRightAltHeld(hook, true);
        SimulateKeyDown(hook, 0x0001, VK_RMENU);

        // Simulate left Alt release — should not deactivate
        SimulateModifierChange(hook, 0x0000, VK_LMENU);

        // Assert — recording still active, no key-up fired
        Assert.False(keyUpFired);
        Assert.True(IsRecordingActive(hook));
    }

    [Fact]
    public void RightAltOnly_AutoRepeat_SuppressedWhileAltHeld()
    {
        // Arrange
        using var hook = new KeyboardHook(CreateRightAltOnlyHotkey());
        int keyDownCount = 0;
        hook.OnKeyDown += () => keyDownCount++;

        SetRightAltHeld(hook, true);

        // Act — first key-down
        SimulateKeyDown(hook, 0x0001, VK_RMENU);
        // Repeated key-down while still held
        SimulateKeyDown(hook, 0x0001, VK_RMENU);
        SimulateKeyDown(hook, 0x0001, VK_RMENU);

        // Assert
        Assert.Equal(1, keyDownCount);
    }

    [Fact]
    public void RightAltOnly_Reconfigure_PreservesRightAltBehavior()
    {
        // Arrange
        using var hook = new KeyboardHook(CreateRightAltOnlyHotkey());

        // Act — reconfigure to a new RightAltOnly config
        var newConfig = CreateRightAltOnlyHotkey();
        newConfig.RightAltOnly = true;
        hook.Reconfigure(newConfig);

        // Assert — hook should still respond to right Alt
        bool keyDownFired = false;
        hook.OnKeyDown += () => keyDownFired = true;
        SetRightAltHeld(hook, true);
        SimulateKeyDown(hook, 0x0001, VK_RMENU);
        Assert.True(keyDownFired);
    }

    #endregion

    #region ForceStop latch tests

    [Fact]
    public void ForceStop_PreventsReTrigger_WhileModifiersHeld()
    {
        // Arrange — non-RightAltOnly modifier-only hotkey
        using var hook = new KeyboardHook(
            new HotkeyConfig
            {
                Modifiers = 0x000A, // Ctrl+Win
                Key = 0,
            }
        );
        int keyDownCount = 0;
        hook.OnKeyDown += () => keyDownCount++;

        // Activate recording
        SimulateKeyDown(hook, 0x000A, 0);
        Assert.Equal(1, keyDownCount);

        // Force stop (simulates max duration expiry)
        hook.ForceStop();

        // Act — attempt re-trigger while Ctrl+Win still held
        SimulateKeyDown(hook, 0x000A, 0);

        // Assert — no new key-down
        Assert.Equal(1, keyDownCount);
        Assert.False(IsRecordingActive(hook));
    }

    [Fact]
    public void ForceStop_ReArmsAfterModifierRelease()
    {
        // Arrange
        using var hook = new KeyboardHook(
            new HotkeyConfig
            {
                Modifiers = 0x000A, // Ctrl+Win
                Key = 0,
            }
        );
        int keyDownCount = 0;
        hook.OnKeyDown += () => keyDownCount++;

        // Activate and force stop
        SimulateKeyDown(hook, 0x000A, 0);
        hook.ForceStop();

        // Release all modifiers
        SimulateModifierChange(hook, 0x0000, 0);

        // Re-press the hotkey
        SimulateKeyDown(hook, 0x000A, 0);

        // Assert
        Assert.Equal(2, keyDownCount);
        Assert.True(IsRecordingActive(hook));
    }

    [Fact]
    public void ForceStop_PreventsReTrigger_WhileRightAltHeld()
    {
        // Arrange
        using var hook = new KeyboardHook(CreateRightAltOnlyHotkey());
        int keyDownCount = 0;
        hook.OnKeyDown += () => keyDownCount++;

        // Activate via right Alt
        SetRightAltHeld(hook, true);
        SimulateKeyDown(hook, 0x0001, VK_RMENU);
        Assert.Equal(1, keyDownCount);

        // Force stop while right Alt still held
        hook.ForceStop();

        // Attempt re-trigger while right Alt held
        SimulateKeyDown(hook, 0x0001, VK_RMENU);

        // Assert
        Assert.Equal(1, keyDownCount);
        Assert.False(IsRecordingActive(hook));
    }

    [Fact]
    public void ForceStop_ReArmsAfterRightAltRelease()
    {
        // Arrange
        using var hook = new KeyboardHook(CreateRightAltOnlyHotkey());
        int keyDownCount = 0;
        hook.OnKeyDown += () => keyDownCount++;

        // Activate and force stop
        SetRightAltHeld(hook, true);
        SimulateKeyDown(hook, 0x0001, VK_RMENU);
        hook.ForceStop();

        // Release right Alt
        SetRightAltHeld(hook, false);
        SimulateModifierChange(hook, 0x0000, VK_RMENU);

        // Re-press right Alt
        SetRightAltHeld(hook, true);
        SimulateKeyDown(hook, 0x0001, VK_RMENU);

        // Assert
        Assert.Equal(2, keyDownCount);
        Assert.True(IsRecordingActive(hook));
    }

    #endregion

    #region Helper methods for testing via reflection

    private static void SetRightAltHeld(KeyboardHook hook, bool held)
    {
        var field = typeof(KeyboardHook).GetField(
            "_rightAltHeld",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.NotNull(field);
        field!.SetValue(hook, held);
    }

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

    private static void SimulateModifierChange(
        KeyboardHook hook,
        uint currentModifiers,
        uint vk = 0
    )
    {
        var method = typeof(KeyboardHook).GetMethod(
            "ProcessModifierChange",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.NotNull(method);
        method!.Invoke(hook, new object[] { currentModifiers, vk });
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
