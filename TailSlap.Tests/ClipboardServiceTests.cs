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

    [Fact]
    public void PasteMethodOrder_UsesSendInputBeforeSendKeys()
    {
        Assert.Equal(
            new[] { "WM_PASTE", "SendInput Ctrl+V", "Ctrl+V", "Shift+Insert" },
            ClipboardService.PasteMethodOrder
        );
    }

    [Fact]
    public void FirefoxPasteMethodOrder_UsesSendKeysBeforeSendInput()
    {
        Assert.Equal(
            new[] { "WM_PASTE", "Ctrl+V", "SendInput Ctrl+V", "Shift+Insert" },
            ClipboardService.FirefoxPasteMethodOrder
        );
    }

    [Theory]
    [InlineData(false, "SendInput Ctrl+V", true)]
    [InlineData(false, "Ctrl+V", true)]
    [InlineData(false, "Shift+Insert", false)]
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
    [InlineData(0u, 4u, true)]
    [InlineData(1u, 4u, false)]
    [InlineData(3u, 4u, false)]
    [InlineData(4u, 4u, false)]
    public void ShouldTrySendKeysAfterSendInputFailure_OnlyRetriesZeroEvents(
        uint sentInputEvents,
        uint expectedInputEvents,
        bool expected
    )
    {
        Assert.Equal(
            expected,
            ClipboardService.ShouldTrySendKeysAfterSendInputFailure(
                sentInputEvents,
                expectedInputEvents
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
