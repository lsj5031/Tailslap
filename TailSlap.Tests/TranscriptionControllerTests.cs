using System;
using System.Threading.Tasks;
using Moq;
using Xunit;
using TailSlap;

public class TranscriptionControllerTests
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
    public void TranscriptionController_CreatesInstanceWithValidDependencies()
    {
        // Arrange
        var mockConfig = CreateMockConfigService();
        var mockClip = new Mock<IClipboardService>();
        var mockTranscriberFactory = new Mock<IRemoteTranscriberFactory>();
        var mockAudioRecorderFactory = new Mock<IAudioRecorderFactory>();
        var mockHistory = new Mock<IHistoryService>();

        // Act
        var controller = new TranscriptionController(
            mockConfig.Object,
            mockClip.Object,
            mockTranscriberFactory.Object,
            mockAudioRecorderFactory.Object,
            mockHistory.Object
        );

        // Assert
        Assert.NotNull(controller);
        Assert.False(controller.IsTranscribing);
        Assert.False(controller.IsRecording);
    }

    [Fact]
    public void TranscriptionController_ThrowsWhenConfigIsNull()
    {
        // Arrange
        var mockClip = new Mock<IClipboardService>();
        var mockTranscriberFactory = new Mock<IRemoteTranscriberFactory>();
        var mockAudioRecorderFactory = new Mock<IAudioRecorderFactory>();
        var mockHistory = new Mock<IHistoryService>();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new TranscriptionController(
                null!,
                mockClip.Object,
                mockTranscriberFactory.Object,
                mockAudioRecorderFactory.Object,
                mockHistory.Object
            )
        );
    }

    [Fact]
    public async Task TriggerTranscribeAsync_WhenTranscriberDisabled_ReturnsFalse()
    {
        // Arrange
        var mockConfig = CreateMockConfigService(transcriberEnabled: false);
        var mockClip = new Mock<IClipboardService>();
        var mockTranscriberFactory = new Mock<IRemoteTranscriberFactory>();
        var mockAudioRecorderFactory = new Mock<IAudioRecorderFactory>();
        var mockHistory = new Mock<IHistoryService>();

        var controller = new TranscriptionController(
            mockConfig.Object,
            mockClip.Object,
            mockTranscriberFactory.Object,
            mockAudioRecorderFactory.Object,
            mockHistory.Object
        );

        // Act
        var result = await controller.TriggerTranscribeAsync();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void StopRecording_DoesNotThrowWhenNotRecording()
    {
        // Arrange
        var mockConfig = CreateMockConfigService();
        var mockClip = new Mock<IClipboardService>();
        var mockTranscriberFactory = new Mock<IRemoteTranscriberFactory>();
        var mockAudioRecorderFactory = new Mock<IAudioRecorderFactory>();
        var mockHistory = new Mock<IHistoryService>();

        var controller = new TranscriptionController(
            mockConfig.Object,
            mockClip.Object,
            mockTranscriberFactory.Object,
            mockAudioRecorderFactory.Object,
            mockHistory.Object
        );

        // Act - should not throw
        controller.StopRecording();

        // Assert - no exception means success
        Assert.False(controller.IsRecording);
    }
}
