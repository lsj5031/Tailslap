using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Moq;
using Xunit;

public class ClipboardServiceTests
{
    [Fact]
    public void ClipboardService_CreatesInstance()
    {
        // Arrange & Act
        var service = new ClipboardService(CreateMockConfigService().Object);

        // Assert
        Assert.NotNull(service);
    }

    [Fact]
    public void ClipboardService_MultipleInstances_CanBeCreated()
    {
        // Arrange & Act
        var configService = CreateMockConfigService();
        var service1 = new ClipboardService(configService.Object);
        var service2 = new ClipboardService(configService.Object);

        // Assert - should not throw
        Assert.NotNull(service1);
        Assert.NotNull(service2);
        Assert.NotSame(service1, service2);
    }

    [Fact]
    public void ClipboardService_EventsCanBeSubscribed()
    {
        // Arrange
        var service = new ClipboardService(CreateMockConfigService().Object);
        bool captureStartedFired = false;
        bool captureEndedFired = false;

        // Act
        service.CaptureStarted += () => captureStartedFired = true;
        service.CaptureEnded += () => captureEndedFired = true;

        // Assert
        Assert.False(captureStartedFired);
        Assert.False(captureEndedFired);
    }

    [Fact]
    public void CaptureSelectionOrClipboardAsync_HasExpectedReturnType()
    {
        var method = typeof(ClipboardService).GetMethod(
            nameof(ClipboardService.CaptureSelectionOrClipboardAsync)
        );

        Assert.NotNull(method);
        Assert.Equal(typeof(Task<string>), method.ReturnType);
        Assert.Equal(2, method.GetParameters().Length);
        Assert.Equal(typeof(IntPtr?), method.GetParameters()[1].ParameterType);
    }

    [Fact]
    public void BuildExcludedDataObject_AddsTextAndClipboardPrivacyFormats()
    {
        const string text = "private dictated text";

        DataObject dataObject = ClipboardService.BuildExcludedDataObject(text);

        Assert.True(
            dataObject.TryGetData<string>(
                DataFormats.UnicodeText,
                autoConvert: false,
                out var actualText
            )
        );
        Assert.Equal(text, actualText);
        AssertDwordFormat(dataObject, "ExcludeClipboardContentFromMonitorProcessing", 1);
        AssertDwordFormat(dataObject, "CanIncludeInClipboardHistory", 0);
        AssertDwordFormat(dataObject, "CanUploadToCloudClipboard", 0);
    }

    [Fact]
    public void AppConfig_ClipboardHistoryExclusion_DefaultsToTrue()
    {
        Assert.True(new AppConfig().ExcludeFromClipboardHistory);
    }

    [Theory]
    [InlineData(10, 10, false)]
    [InlineData(10, 11, true)]
    [InlineData(0, 11, true)]
    [InlineData(10, 0, false)]
    public void IsClipboardSequenceChanged_OnlyAcceptsNonZeroChanges(
        uint sequenceBefore,
        uint sequenceAfter,
        bool expected
    )
    {
        Assert.Equal(
            expected,
            ClipboardService.IsClipboardSequenceChanged(sequenceBefore, sequenceAfter)
        );
    }

    [Fact]
    public void NativeInputSize_MatchesWin32InputSize()
    {
        Assert.Equal(IntPtr.Size == 8 ? 40 : 28, ClipboardService.NativeInputSize);
    }

    [Theory]
    [InlineData("a\u0000b", "a\u0000bc", "c", true)] // second edit grew
    [InlineData("a\u0000b", "ab\u0000b", "b", true)] // first edit changed
    [InlineData("a\u0000b", "a\u0000b", "c", false)] // no edit changed
    [InlineData(null, "a", "a", false)] // unreadable before
    [InlineData("", "x", "x", true)] // single empty edit, paste grows it
    [InlineData("a\u0000", "a\u0000x", "x", true)] // trailing empty edit grew
    public void IsPasteDelivered_DetectsChangeInAnyEdit(
        string? before,
        string? after,
        string? clipboard,
        bool expected
    )
    {
        Assert.Equal(expected, ClipboardService.IsPasteDelivered(before, after, clipboard));
    }

    [Fact]
    public void PasteMethodOrder_UsesSendInputBeforeSendKeys()
    {
        Assert.Equal(
            new[] { "WM_PASTE", "SendInput Ctrl+V", "Ctrl+V", "Shift+Insert" },
            ClipboardService.PasteMethodOrder
        );
    }

    [Fact]
    public void CustomEditorPasteMethodOrder_AppendsPlainTextPasteAsLastResort()
    {
        Assert.Equal(
            new[] { "WM_PASTE", "SendInput Ctrl+V", "Ctrl+V", "Shift+Insert", "Ctrl+Shift+V" },
            ClipboardService.CustomEditorPasteMethodOrder
        );
    }

    [Theory]
    [InlineData("", "hello", "hello", true)] // empty field, paste grows it
    [InlineData("abc", "abchello", "hello", true)] // append at caret
    [InlineData("abc", "axc", "x", true)] // selection replace, value shrinks
    [InlineData("abc", "abc", "hello", false)] // unchanged
    [InlineData("abc", "abx", null, false)] // no clipboard text, no growth
    [InlineData("abc", "abcd", null, true)] // no clipboard text, growth
    [InlineData("abc", "x", null, true)] // selection replace shrinks value
    [InlineData("abc", "ab", "hello", true)] // normalized paste, length shrank
    [InlineData(null, "hello", "hello", false)] // unreadable before
    [InlineData("abc", null, "hello", false)] // unreadable after
    [InlineData("abc", "abcxyz", "xyz", true)] // paste inside existing text
    public void IsPasteDelivered_JudgesValueChange(
        string? before,
        string? after,
        string? clipboard,
        bool expected
    )
    {
        Assert.Equal(expected, ClipboardService.IsPasteDelivered(before, after, clipboard));
    }

    [Theory]
    [InlineData(false, "WM_PASTE", false)]
    [InlineData(false, "SendInput Ctrl+V", true)]
    [InlineData(false, "Ctrl+V", true)]
    [InlineData(false, "Shift+Insert", true)]
    [InlineData(false, "Ctrl+Shift+V", true)]
    [InlineData(true, "SendInput Ctrl+V", false)]
    public void ShouldStopAfterUnverifiedPasteAttempt_PreventsDuplicateCustomEditorPaste(
        bool supportsNativePaste,
        string method,
        bool expected
    )
    {
        Assert.Equal(
            expected,
            ClipboardService.ShouldStopAfterUnverifiedPasteAttempt(supportsNativePaste, method)
        );
    }

    [Theory]
    [InlineData(false, "WM_PASTE", true, false)]
    [InlineData(false, "SendInput Ctrl+V", true, true)]
    [InlineData(false, "Ctrl+V", true, true)]
    [InlineData(false, "Shift+Insert", true, true)]
    [InlineData(false, "Ctrl+Shift+V", true, true)]
    [InlineData(false, "SendInput Ctrl+V", false, false)]
    [InlineData(true, "SendInput Ctrl+V", true, false)]
    public void ShouldTrustUnverifiedPasteAttempt_WhenInjectionSucceeded(
        bool supportsNativePaste,
        string method,
        bool injectionSucceeded,
        bool expected
    )
    {
        Assert.Equal(
            expected,
            ClipboardService.ShouldTrustUnverifiedPasteAttempt(
                supportsNativePaste,
                method,
                injectionSucceeded
            )
        );
    }

    [Fact]
    public void AppConfig_ClipboardFallback_DefaultsToDisabled()
    {
        Assert.False(new AppConfig().UseClipboardFallback);
    }

    [Fact]
    public void AppConfig_Clone_PreservesClipboardFallback()
    {
        var config = new AppConfig { UseClipboardFallback = true };

        AppConfig clone = config.Clone();

        Assert.True(clone.UseClipboardFallback);
    }

    [Fact]
    public void AppConfig_Clone_PreservesClipboardHistoryExclusion()
    {
        var config = new AppConfig { ExcludeFromClipboardHistory = false };

        AppConfig clone = config.Clone();

        Assert.False(clone.ExcludeFromClipboardHistory);
    }

    private static Mock<IConfigService> CreateMockConfigService()
    {
        var mock = new Mock<IConfigService>();
        mock.Setup(service => service.CreateValidatedCopy()).Returns(new AppConfig());
        return mock;
    }

    private static void AssertDwordFormat(DataObject dataObject, string format, int expected)
    {
        Assert.True(dataObject.GetDataPresent(format, autoConvert: false));
        Assert.True(
            dataObject.TryGetData<MemoryStream>(format, autoConvert: false, out var stream)
        );
        Assert.NotNull(stream);
        Assert.Equal(expected, BitConverter.ToInt32(stream.ToArray(), 0));
    }
}
