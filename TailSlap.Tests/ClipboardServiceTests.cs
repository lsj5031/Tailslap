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
    public void CaptureSelectionOrClipboardAsync_ReturnsTask()
    {
        // Arrange
        var service = new ClipboardService(CreateMockConfigService().Object);

        // Act
        var task = service.CaptureSelectionOrClipboardAsync();

        // Assert
        Assert.IsType<Task<string>>(task);

        // Note: the operation requires actual window focus which we can't simulate in unit test
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
            dataObject.TryGetData<MemoryStream>(
                format,
                autoConvert: false,
                out var stream
            )
        );
        Assert.NotNull(stream);
        Assert.Equal(expected, BitConverter.ToInt32(stream.ToArray(), 0));
    }
}
