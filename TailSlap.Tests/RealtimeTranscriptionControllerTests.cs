using System;
using System.Threading.Tasks;
using Moq;
using Xunit;
using TailSlap;

public class RealtimeTranscriptionControllerTests
{
    private Mock<IConfigService> CreateMockConfigService(bool transcriberEnabled = true)
    {
        var mockConfig = new Mock<IConfigService>();
        var config = new AppConfig
        {
            Transcriber = new TranscriberConfig
            {
                Enabled = transcriberEnabled,
                BaseUrl = "http://localhost:18000/v1/audio/transcriptions",
                Model = "whisper-1",
                TimeoutSeconds = 30,
                AutoPaste = true,
                EnableVAD = true,
                SilenceThresholdMs = 2000,
            },
        };
        mockConfig.Setup(c => c.CreateValidatedCopy()).Returns(config);
        return mockConfig;
    }

    [Fact]
    public void RealtimeTranscriptionController_CreatesInstanceWithValidDependencies()
    {
        // Arrange
        var mockConfig = CreateMockConfigService();
        var mockClip = new Mock<IClipboardService>();
        var mockTranscriberFactory = new Mock<IRealtimeTranscriberFactory>();
        var mockAudioRecorderFactory = new Mock<IAudioRecorderFactory>();

        // Act
        var controller = new RealtimeTranscriptionController(
            mockConfig.Object,
            mockClip.Object,
            mockTranscriberFactory.Object,
            mockAudioRecorderFactory.Object
        );

        // Assert
        Assert.NotNull(controller);
        Assert.Equal(StreamingState.Idle, controller.State);
        Assert.False(controller.IsStreaming);
    }

    [Fact]
    public void RealtimeTranscriptionController_ThrowsWhenConfigIsNull()
    {
        // Arrange
        var mockClip = new Mock<IClipboardService>();
        var mockTranscriberFactory = new Mock<IRealtimeTranscriberFactory>();
        var mockAudioRecorderFactory = new Mock<IAudioRecorderFactory>();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new RealtimeTranscriptionController(
                null!,
                mockClip.Object,
                mockTranscriberFactory.Object,
                mockAudioRecorderFactory.Object
            )
        );
    }

    [Fact]
    public async Task TriggerStreamingAsync_WhenTranscriberDisabled_ReturnsEarly()
    {
        // Arrange
        var mockConfig = CreateMockConfigService(transcriberEnabled: false);
        var mockClip = new Mock<IClipboardService>();
        var mockTranscriberFactory = new Mock<IRealtimeTranscriberFactory>();
        var mockAudioRecorderFactory = new Mock<IAudioRecorderFactory>();

        var controller = new RealtimeTranscriptionController(
            mockConfig.Object,
            mockClip.Object,
            mockTranscriberFactory.Object,
            mockAudioRecorderFactory.Object
        );

        // Act
        await controller.TriggerStreamingAsync();

        // Assert - should remain in Idle state
        Assert.Equal(StreamingState.Idle, controller.State);
    }

    [Fact]
    public void State_InitiallyIdle()
    {
        // Arrange
        var mockConfig = CreateMockConfigService();
        var mockClip = new Mock<IClipboardService>();
        var mockTranscriberFactory = new Mock<IRealtimeTranscriberFactory>();
        var mockAudioRecorderFactory = new Mock<IAudioRecorderFactory>();

        var controller = new RealtimeTranscriptionController(
            mockConfig.Object,
            mockClip.Object,
            mockTranscriberFactory.Object,
            mockAudioRecorderFactory.Object
        );

        // Assert
        Assert.Equal(StreamingState.Idle, controller.State);
        Assert.False(controller.IsStreaming);
    }
}
