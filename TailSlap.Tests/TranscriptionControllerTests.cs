using System;
using System.Reflection;
using System.Threading;
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
        var clipboardHelper = new ClipboardHelper(mockClip.Object);

        // Act
        var controller = new TranscriptionController(
            mockConfig.Object,
            clipboardHelper,
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
        var clipboardHelper = new ClipboardHelper(mockClip.Object);

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new TranscriptionController(
                null!,
                clipboardHelper,
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
        var clipboardHelper = new ClipboardHelper(mockClip.Object);

        var controller = new TranscriptionController(
            mockConfig.Object,
            clipboardHelper,
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
        var clipboardHelper = new ClipboardHelper(mockClip.Object);

        var controller = new TranscriptionController(
            mockConfig.Object,
            clipboardHelper,
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

    [Fact]
    public void ShouldUseEnhancedText_RejectsAggressiveShrink()
    {
        var original =
            "Yes, you should rethink and revise your plan carefully because this flow is still fragile and needs more review.";

        var accepted = TranscriptionController.ShouldUseEnhancedText(
            original,
            "OK.",
            out var rejectionReason
        );

        Assert.False(accepted);
        Assert.Contains("shrank too far", rejectionReason);
    }

    [Fact]
    public void ShouldUseEnhancedText_AcceptsConservativeRewrite()
    {
        var original =
            "Yes, you should rethink and revise your plan carefully because this flow is still fragile and needs more review.";
        var enhanced =
            "Yes, you should rethink and revise your plan carefully because the flow is still fragile and needs more review.";

        var accepted = TranscriptionController.ShouldUseEnhancedText(
            original,
            enhanced,
            out var rejectionReason
        );

        Assert.True(accepted);
        Assert.Equal(string.Empty, rejectionReason);
    }

    [Fact]
    public async Task MaybeEnhanceTranscriptionAsync_IgnoresCanceledRecordingToken()
    {
        var mockConfig = CreateMockConfigService();
        var mockClip = new Mock<IClipboardService>();
        var mockTranscriberFactory = new Mock<IRemoteTranscriberFactory>();
        var mockAudioRecorderFactory = new Mock<IAudioRecorderFactory>();
        var mockHistory = new Mock<IHistoryService>();
        var mockRefinerFactory = new Mock<ITextRefinerFactory>();
        var mockRefiner = new Mock<ITextRefiner>();
        var clipboardHelper = new ClipboardHelper(mockClip.Object);

        CancellationToken observedToken = default;
        const string enhancedText =
            "This rewrite keeps the core meaning while cleaning up the dictation artifacts for readability.";

        mockRefiner
            .Setup(r => r.RefineAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((_, ct) => observedToken = ct)
            .ReturnsAsync(enhancedText);
        mockRefinerFactory.Setup(f => f.Create(It.IsAny<LlmConfig>())).Returns(mockRefiner.Object);

        var controller = new TranscriptionController(
            mockConfig.Object,
            clipboardHelper,
            mockTranscriberFactory.Object,
            mockAudioRecorderFactory.Object,
            mockHistory.Object,
            mockRefinerFactory.Object
        );

        using var canceledRecordingCts = new CancellationTokenSource();
        canceledRecordingCts.Cancel();
        SetPrivateField(controller, "_recordingCts", canceledRecordingCts);

        var original =
            "This is a long enough transcript to trigger enhancement because it is comfortably above the threshold and still needs cleanup from dictation artifacts.";

        var result = await InvokeMaybeEnhanceTranscriptionAsync(
            controller,
            original,
            mockConfig.Object.CreateValidatedCopy()
        );

        Assert.Equal(enhancedText, result);
        Assert.False(observedToken.IsCancellationRequested);
    }

    [Fact]
    public async Task TranscribeRecordedAudioAsync_WhenStreamResultsEnabled_UsesNonStreamingPath()
    {
        var mockConfig = CreateMockConfigService();
        var cfg = mockConfig.Object.CreateValidatedCopy();
        cfg.Transcriber.StreamResults = true;

        var mockClip = new Mock<IClipboardService>();
        var mockTranscriberFactory = new Mock<IRemoteTranscriberFactory>();
        var mockAudioRecorderFactory = new Mock<IAudioRecorderFactory>();
        var mockHistory = new Mock<IHistoryService>();
        var mockRefinerFactory = new Mock<ITextRefinerFactory>();
        var mockTranscriber = new Mock<IRemoteTranscriber>();
        var clipboardHelper = new ClipboardHelper(mockClip.Object);

        mockTranscriber
            .Setup(t => t.TranscribeAudioAsync("test.wav", It.IsAny<CancellationToken>()))
            .ReturnsAsync("full transcript");

        var controller = new TranscriptionController(
            mockConfig.Object,
            clipboardHelper,
            mockTranscriberFactory.Object,
            mockAudioRecorderFactory.Object,
            mockHistory.Object,
            mockRefinerFactory.Object
        );

        var result = await InvokeTranscribeRecordedAudioAsync(
            controller,
            mockTranscriber.Object,
            "test.wav",
            cfg
        );

        Assert.Equal("full transcript", result);
        mockTranscriber.Verify(
            t => t.TranscribeAudioAsync("test.wav", It.IsAny<CancellationToken>()),
            Times.Once
        );
        mockTranscriber.Verify(
            t => t.TranscribeStreamingAsync("test.wav", It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Fact]
    public void PersistHistoryEntries_WhenEnhancedTextDiffers_StoresRawAndRefinedVersions()
    {
        var mockConfig = CreateMockConfigService();
        var mockClip = new Mock<IClipboardService>();
        var mockTranscriberFactory = new Mock<IRemoteTranscriberFactory>();
        var mockAudioRecorderFactory = new Mock<IAudioRecorderFactory>();
        var mockHistory = new Mock<IHistoryService>();
        var mockRefinerFactory = new Mock<ITextRefinerFactory>();
        var clipboardHelper = new ClipboardHelper(mockClip.Object);

        var controller = new TranscriptionController(
            mockConfig.Object,
            clipboardHelper,
            mockTranscriberFactory.Object,
            mockAudioRecorderFactory.Object,
            mockHistory.Object,
            mockRefinerFactory.Object
        );

        const string rawText =
            "This is the raw transcript that should remain in transcription history.";
        const string refinedText =
            "This is the refined transcript that should appear in refinement history.";
        var cfg = mockConfig.Object.CreateValidatedCopy();

        InvokePersistHistoryEntries(controller, rawText, refinedText, cfg, 1234);

        mockHistory.Verify(h => h.AppendTranscription(rawText, 1234), Times.Once);
        mockHistory.Verify(h => h.Append(rawText, refinedText, cfg.Llm.Model), Times.Once);
    }

    [Fact]
    public void PersistHistoryEntries_WhenTextIsUnchanged_OnlyStoresTranscriptionHistory()
    {
        var mockConfig = CreateMockConfigService();
        var mockClip = new Mock<IClipboardService>();
        var mockTranscriberFactory = new Mock<IRemoteTranscriberFactory>();
        var mockAudioRecorderFactory = new Mock<IAudioRecorderFactory>();
        var mockHistory = new Mock<IHistoryService>();
        var mockRefinerFactory = new Mock<ITextRefinerFactory>();
        var clipboardHelper = new ClipboardHelper(mockClip.Object);

        var controller = new TranscriptionController(
            mockConfig.Object,
            clipboardHelper,
            mockTranscriberFactory.Object,
            mockAudioRecorderFactory.Object,
            mockHistory.Object,
            mockRefinerFactory.Object
        );

        const string rawText =
            "This transcript was not enhanced and should stay only in transcription history.";
        var cfg = mockConfig.Object.CreateValidatedCopy();

        InvokePersistHistoryEntries(controller, rawText, rawText, cfg, 4321);

        mockHistory.Verify(h => h.AppendTranscription(rawText, 4321), Times.Once);
        mockHistory.Verify(
            h => h.Append(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never
        );
    }

    private static async Task<string> InvokeMaybeEnhanceTranscriptionAsync(
        TranscriptionController controller,
        string transcriptionText,
        AppConfig cfg
    )
    {
        var method = typeof(TranscriptionController).GetMethod(
            "MaybeEnhanceTranscriptionAsync",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.NotNull(method);

        var task = (Task<string>?)
            method!.Invoke(controller, new object?[] { transcriptionText, cfg });

        Assert.NotNull(task);
        return await task!;
    }

    private static void InvokePersistHistoryEntries(
        TranscriptionController controller,
        string transcriptionText,
        string finalText,
        AppConfig cfg,
        int recordingDurationMs
    )
    {
        var method = typeof(TranscriptionController).GetMethod(
            "PersistHistoryEntries",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.NotNull(method);

        method!.Invoke(
            controller,
            new object?[] { transcriptionText, finalText, cfg, recordingDurationMs }
        );
    }

    private static async Task<string> InvokeTranscribeRecordedAudioAsync(
        TranscriptionController controller,
        IRemoteTranscriber transcriber,
        string audioFilePath,
        AppConfig cfg
    )
    {
        var method = typeof(TranscriptionController).GetMethod(
            "TranscribeRecordedAudioAsync",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.NotNull(method);

        var task = (Task<string>?)
            method!.Invoke(controller, new object?[] { transcriber, audioFilePath, cfg });

        Assert.NotNull(task);
        return await task!;
    }

    private static void SetPrivateField(object target, string fieldName, object? value)
    {
        var field = target
            .GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }
}
