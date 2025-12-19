using System;
using System.Threading.Tasks;
using Moq;
using Xunit;

public class ClipboardServiceTests
{
    [Fact]
    public void ClipboardService_CreatesInstance()
    {
        // Arrange & Act
        var service = new ClipboardService();

        // Assert
        Assert.NotNull(service);
    }

    [Fact]
    public void ClipboardService_MultipleInstances_CanBeCreated()
    {
        // Arrange & Act
        var service1 = new ClipboardService();
        var service2 = new ClipboardService();

        // Assert - should not throw
        Assert.NotNull(service1);
        Assert.NotNull(service2);
        Assert.NotSame(service1, service2);
    }

    [Fact]
    public void ClipboardService_EventsCanBeSubscribed()
    {
        // Arrange
        var service = new ClipboardService();
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
        var service = new ClipboardService();

        // Act
        var task = service.CaptureSelectionOrClipboardAsync();

        // Assert
        Assert.IsType<Task<string>>(task);

        // Note: the operation requires actual window focus which we can't simulate in unit test
    }
}
