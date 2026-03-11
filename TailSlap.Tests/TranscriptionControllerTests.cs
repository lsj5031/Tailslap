using System;
using System.Threading.Tasks;
using Moq;
using TailSlap;
using Xunit;

public class TranscriptionControllerTests
{
    private Mock<IConfigService> CreateMockConfigService(bool transcriberEnabled = true)
    {
        var mockConfig = new Mock<IConfigService>();
        var config = new AppConfig
        {
            Llm = new LlmConfig
            {
                Enabled = true,
                BaseUrl = "http://localhost:11434/v1",
                Model = "llama2",
                Temperature = 0.7,
            },
            Transcriber = new TranscriberConfig
            {
                Enabled = transcriberEnabled,
                BaseUrl = "http://localhost:18000/v1",
                Model = "whisper-1",
                TimeoutSeconds = 30,
                AutoPaste = true,
                EnableVAD = true,
                SilenceThresholdMs = 2000,
                EnableAutoEnhance = true,
                AutoEnhanceThresholdChars = 100,
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
        var mockRefinerFactory = new Mock<ITextRefinerFactory>();

        // Act
        var controller = new TranscriptionController(
            mockConfig.Object,
            mockClip.Object,
            mockTranscriberFactory.Object,
            mockAudioRecorderFactory.Object,
            mockHistory.Object,
            mockRefinerFactory.Object
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
        var mockRefinerFactory = new Mock<ITextRefinerFactory>();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new TranscriptionController(
                null!,
                mockClip.Object,
                mockTranscriberFactory.Object,
                mockAudioRecorderFactory.Object,
                mockHistory.Object,
                mockRefinerFactory.Object
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
        var mockRefinerFactory = new Mock<ITextRefinerFactory>();

        var controller = new TranscriptionController(
            mockConfig.Object,
            mockClip.Object,
            mockTranscriberFactory.Object,
            mockAudioRecorderFactory.Object,
            mockHistory.Object,
            mockRefinerFactory.Object
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
        var mockRefinerFactory = new Mock<ITextRefinerFactory>();

        var controller = new TranscriptionController(
            mockConfig.Object,
            mockClip.Object,
            mockTranscriberFactory.Object,
            mockAudioRecorderFactory.Object,
            mockHistory.Object,
            mockRefinerFactory.Object
        );

        // Act - should not throw
        controller.StopRecording();

        // Assert - no exception means success
        Assert.False(controller.IsRecording);
    }
}
