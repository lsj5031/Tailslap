using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using TailSlap;
using Xunit;

namespace TailSlap.Tests;

public class TypelessControllerTests
{
    private static Mock<IConfigService> CreateMockConfigService(bool transcriberEnabled = true)
    {
        var mockConfig = new Mock<IConfigService>();
        var config = new AppConfig
        {
            Llm = new LlmConfig
            {
                Enabled = true,
                BaseUrl = "http://localhost:11434/v1",
                Model = "llama3.1",
                Temperature = 0.2,
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
                PreferredMicrophoneIndex = -1,
            },
        };
        mockConfig.Setup(c => c.CreateValidatedCopy()).Returns(config);
        return mockConfig;
    }

    /// <summary>
    /// Creates a mock recording function that blocks until cancelled, then returns the specified duration.
    /// Also creates a dummy WAV file so the transcription step can find it.
    /// </summary>
    private static Func<
        AppConfig,
        string,
        CancellationToken,
        Task<RecordingStats>
    > CreateRecordFunc(int durationMs = 1500)
    {
        return (cfg, path, ct) =>
            Task.Run(async () =>
            {
                // Create a dummy WAV file so TranscribeAsync finds it
                CreateDummyWavFile(path);

                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    // Expected when key is released
                }

                return new RecordingStats { DurationMs = durationMs, BytesRecorded = 32000 };
            });
    }

    /// <summary>
    /// Creates a mock recording function that throws an exception.
    /// </summary>
    private static Func<
        AppConfig,
        string,
        CancellationToken,
        Task<RecordingStats>
    > CreateErrorRecordFunc(Exception exception)
    {
        return (cfg, path, ct) => Task.FromException<RecordingStats>(exception);
    }

    /// <summary>
    /// Creates a minimal valid WAV file at the given path.
    /// </summary>
    private static void CreateDummyWavFile(string path)
    {
        // Minimal WAV: 44-byte header + 2 bytes of silence data
        var bytes = new byte[46];
        // RIFF header
        bytes[0] = (byte)'R';
        bytes[1] = (byte)'I';
        bytes[2] = (byte)'F';
        bytes[3] = (byte)'F';
        BitConverter.GetBytes(36 + 2).CopyTo(bytes, 4); // File size - 8
        bytes[8] = (byte)'W';
        bytes[9] = (byte)'A';
        bytes[10] = (byte)'V';
        bytes[11] = (byte)'E';
        // fmt chunk
        bytes[12] = (byte)'f';
        bytes[13] = (byte)'m';
        bytes[14] = (byte)'t';
        bytes[15] = (byte)' ';
        BitConverter.GetBytes(16).CopyTo(bytes, 16); // Subchunk1Size
        BitConverter.GetBytes((short)1).CopyTo(bytes, 20); // AudioFormat PCM
        BitConverter.GetBytes((short)1).CopyTo(bytes, 22); // NumChannels
        BitConverter.GetBytes(16000).CopyTo(bytes, 24); // SampleRate
        BitConverter.GetBytes(32000).CopyTo(bytes, 28); // ByteRate
        BitConverter.GetBytes((short)2).CopyTo(bytes, 32); // BlockAlign
        BitConverter.GetBytes((short)16).CopyTo(bytes, 34); // BitsPerSample
        // data chunk
        bytes[36] = (byte)'d';
        bytes[37] = (byte)'a';
        bytes[38] = (byte)'t';
        bytes[39] = (byte)'a';
        BitConverter.GetBytes(2).CopyTo(bytes, 40); // Subchunk2Size
        bytes[44] = 0;
        bytes[45] = 0; // 1 sample of silence

        File.WriteAllBytes(path, bytes);
    }

    private static Mock<IRemoteTranscriberFactory> CreateMockTranscriberFactory(
        out Mock<IRemoteTranscriber> mockTranscriber,
        params string[] chunks
    )
    {
        mockTranscriber = new Mock<IRemoteTranscriber>();

        async IAsyncEnumerable<string> StreamChunks(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default
        )
        {
            foreach (var chunk in chunks)
            {
                await Task.Yield();
                yield return chunk;
            }
        }

        mockTranscriber
            .Setup(t =>
                t.TranscribeStreamingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())
            )
            .Returns((string path, CancellationToken ct) => StreamChunks(ct));

        var mockFactory = new Mock<IRemoteTranscriberFactory>();
        mockFactory
            .Setup(f => f.Create(It.IsAny<TranscriberConfig>()))
            .Returns(mockTranscriber.Object);
        return mockFactory;
    }

    private static Mock<IRemoteTranscriberFactory> CreateErrorTranscriberFactory(
        Exception exception
    )
    {
        var mockTranscriber = new Mock<IRemoteTranscriber>();

        async IAsyncEnumerable<string> ThrowError(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default
        )
        {
            await Task.Yield();
            yield return ""; // Satisfy compiler requirement for yield in async iterator
            throw exception;
        }

        mockTranscriber
            .Setup(t =>
                t.TranscribeStreamingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())
            )
            .Returns((string path, CancellationToken ct) => ThrowError(ct));

        var mockFactory = new Mock<IRemoteTranscriberFactory>();
        mockFactory
            .Setup(f => f.Create(It.IsAny<TranscriberConfig>()))
            .Returns(mockTranscriber.Object);
        return mockFactory;
    }

    private TypelessController CreateController(
        Mock<IConfigService>? configMock = null,
        Mock<IClipboardService>? clipboardMock = null,
        Mock<IRemoteTranscriberFactory>? transcriberFactoryMock = null,
        Mock<IHistoryService>? historyMock = null,
        Mock<ITextRefinerFactory>? refinerFactoryMock = null,
        Func<AppConfig, string, CancellationToken, Task<RecordingStats>>? recordFunc = null
    )
    {
        configMock ??= CreateMockConfigService();
        clipboardMock ??= new Mock<IClipboardService>();
        transcriberFactoryMock ??= new Mock<IRemoteTranscriberFactory>();
        var recorderFactoryMock = new Mock<IAudioRecorderFactory>();
        historyMock ??= new Mock<IHistoryService>();
        refinerFactoryMock ??= new Mock<ITextRefinerFactory>();
        recordFunc ??= CreateRecordFunc();

        var clipboardHelper = new ClipboardHelper(clipboardMock.Object);

        return new TypelessController(
            configMock.Object,
            clipboardHelper,
            transcriberFactoryMock.Object,
            recorderFactoryMock.Object,
            historyMock.Object,
            refinerFactoryMock.Object,
            recordFunc
        );
    }

    #region Constructor Validation

    [Fact]
    public void Constructor_NullConfig_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new TypelessController(
                null!,
                new ClipboardHelper(new Mock<IClipboardService>().Object),
                new Mock<IRemoteTranscriberFactory>().Object,
                new Mock<IAudioRecorderFactory>().Object,
                new Mock<IHistoryService>().Object,
                new Mock<ITextRefinerFactory>().Object,
                CreateRecordFunc()
            )
        );
    }

    [Fact]
    public void Constructor_NullClipboardHelper_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new TypelessController(
                null!,
                new ClipboardHelper(new Mock<IClipboardService>().Object),
                new Mock<IRemoteTranscriberFactory>().Object,
                new Mock<IAudioRecorderFactory>().Object,
                new Mock<IHistoryService>().Object,
                new Mock<ITextRefinerFactory>().Object,
                CreateRecordFunc()
            )
        );
    }

    [Fact]
    public void Constructor_NullTranscriberFactory_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new TypelessController(
                CreateMockConfigService().Object,
                new ClipboardHelper(new Mock<IClipboardService>().Object),
                null!,
                new Mock<IAudioRecorderFactory>().Object,
                new Mock<IHistoryService>().Object,
                new Mock<ITextRefinerFactory>().Object,
                CreateRecordFunc()
            )
        );
    }

    [Fact]
    public void Constructor_NullRecorderFactory_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new TypelessController(
                CreateMockConfigService().Object,
                new ClipboardHelper(new Mock<IClipboardService>().Object),
                new Mock<IRemoteTranscriberFactory>().Object,
                null!,
                new Mock<IHistoryService>().Object,
                new Mock<ITextRefinerFactory>().Object,
                CreateRecordFunc()
            )
        );
    }

    [Fact]
    public void Constructor_NullHistoryService_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new TypelessController(
                CreateMockConfigService().Object,
                new ClipboardHelper(new Mock<IClipboardService>().Object),
                new Mock<IRemoteTranscriberFactory>().Object,
                new Mock<IAudioRecorderFactory>().Object,
                null!,
                new Mock<ITextRefinerFactory>().Object,
                CreateRecordFunc()
            )
        );
    }

    [Fact]
    public void Constructor_NullRefinerFactory_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new TypelessController(
                CreateMockConfigService().Object,
                new ClipboardHelper(new Mock<IClipboardService>().Object),
                new Mock<IRemoteTranscriberFactory>().Object,
                new Mock<IAudioRecorderFactory>().Object,
                new Mock<IHistoryService>().Object,
                null!,
                CreateRecordFunc()
            )
        );
    }

    [Fact]
    public void Constructor_NullRecordFunc_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new TypelessController(
                CreateMockConfigService().Object,
                new ClipboardHelper(new Mock<IClipboardService>().Object),
                new Mock<IRemoteTranscriberFactory>().Object,
                new Mock<IAudioRecorderFactory>().Object,
                new Mock<IHistoryService>().Object,
                new Mock<ITextRefinerFactory>().Object,
                null!
            )
        );
    }

    [Fact]
    public void Constructor_ValidArgs_InitializesCorrectly()
    {
        var controller = CreateController();
        Assert.False(controller.IsRecording);
        Assert.False(controller.IsProcessing);
    }

    #endregion

    #region HandleKeyDown

    [Fact]
    public async Task HandleKeyDown_WhenIdleAndEnabled_StartsRecording()
    {
        var controller = CreateController();

        bool startedFired = false;
        controller.OnStarted += () => startedFired = true;

        await controller.HandleKeyDownAsync();

        Assert.True(controller.IsRecording);
        Assert.True(startedFired);
    }

    [Fact]
    public async Task HandleKeyDown_WhenAlreadyRecording_IgnoresAutoRepeat()
    {
        var controller = CreateController();

        await controller.HandleKeyDownAsync();
        Assert.True(controller.IsRecording);

        // Second key-down should be ignored (auto-repeat)
        int startedCount = 0;
        controller.OnStarted += () => startedCount++;

        await controller.HandleKeyDownAsync();

        Assert.True(controller.IsRecording);
        Assert.Equal(0, startedCount);
    }

    [Fact]
    public async Task HandleKeyDown_WhenProcessing_ShowsNotificationAndIgnores()
    {
        var mockTranscriberFactory = CreateMockTranscriberFactory(out _, "hello");
        var mockClipboard = new Mock<IClipboardService>();
        mockClipboard.Setup(c => c.SetText(It.IsAny<string>())).Returns(true);

        var controller = CreateController(
            transcriberFactoryMock: mockTranscriberFactory,
            clipboardMock: mockClipboard
        );

        // Start recording
        await controller.HandleKeyDownAsync();

        // Start key-up in background (it will process)
        var keyUpTask = controller.HandleKeyUpAsync();

        // Small delay to let processing start
        await Task.Delay(100);

        // While processing, try key-down — should be rejected with notification
        if (controller.IsProcessing)
        {
            await controller.HandleKeyDownAsync();
            // Should not crash
        }

        await keyUpTask;
    }

    [Fact]
    public async Task HandleKeyDown_WhenTranscriberDisabled_Ignores()
    {
        var configMock = CreateMockConfigService(transcriberEnabled: false);
        var controller = CreateController(configMock: configMock);

        bool startedFired = false;
        controller.OnStarted += () => startedFired = true;

        await controller.HandleKeyDownAsync();

        Assert.False(controller.IsRecording);
        Assert.False(startedFired);
    }

    #endregion

    #region HandleKeyUp - Transcription

    [Fact]
    public async Task HandleKeyUp_StopsRecordingAndTranscribes()
    {
        var mockTranscriberFactory = CreateMockTranscriberFactory(out _, "hello world");
        var mockClipboard = new Mock<IClipboardService>();
        mockClipboard.Setup(c => c.SetText(It.IsAny<string>())).Returns(true);
        var mockHistory = new Mock<IHistoryService>();

        var controller = CreateController(
            transcriberFactoryMock: mockTranscriberFactory,
            clipboardMock: mockClipboard,
            historyMock: mockHistory
        );

        await controller.HandleKeyDownAsync();
        Assert.True(controller.IsRecording);

        await controller.HandleKeyUpAsync();

        Assert.False(controller.IsRecording);
        Assert.False(controller.IsProcessing);
    }

    [Fact]
    public async Task HandleKeyUp_ShortRecording_DiscardsWithoutTranscription()
    {
        var mockTranscriberFactory = CreateMockTranscriberFactory(
            out var mockTranscriber,
            "result"
        );

        var controller = CreateController(
            transcriberFactoryMock: mockTranscriberFactory,
            recordFunc: CreateRecordFunc(durationMs: 200)
        );

        await controller.HandleKeyDownAsync();
        await controller.HandleKeyUpAsync();

        // Transcriber should not have been called
        mockTranscriber.Verify(
            t => t.TranscribeStreamingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Fact]
    public async Task HandleKeyUp_EmptyResult_NoPasteNoHistory()
    {
        var mockTranscriberFactory = CreateMockTranscriberFactory(out _, "");
        var mockClipboard = new Mock<IClipboardService>();
        var mockHistory = new Mock<IHistoryService>();

        var controller = CreateController(
            transcriberFactoryMock: mockTranscriberFactory,
            clipboardMock: mockClipboard,
            historyMock: mockHistory
        );

        await controller.HandleKeyDownAsync();
        await controller.HandleKeyUpAsync();

        mockClipboard.Verify(c => c.SetText(It.IsAny<string>()), Times.Never);
        mockHistory.Verify(
            h => h.AppendTranscription(It.IsAny<string>(), It.IsAny<int>()),
            Times.Never
        );
    }

    [Fact]
    public async Task HandleKeyUp_WhitespaceResult_NoPasteNoHistory()
    {
        var mockTranscriberFactory = CreateMockTranscriberFactory(out _, "   ", "\n\n");
        var mockClipboard = new Mock<IClipboardService>();
        var mockHistory = new Mock<IHistoryService>();

        var controller = CreateController(
            transcriberFactoryMock: mockTranscriberFactory,
            clipboardMock: mockClipboard,
            historyMock: mockHistory
        );

        await controller.HandleKeyDownAsync();
        await controller.HandleKeyUpAsync();

        mockClipboard.Verify(c => c.SetText(It.IsAny<string>()), Times.Never);
        mockHistory.Verify(
            h => h.AppendTranscription(It.IsAny<string>(), It.IsAny<int>()),
            Times.Never
        );
    }

    #endregion

    #region Server Error / Timeout

    [Fact]
    public async Task HandleKeyUp_ServerError_ReturnsToIdle()
    {
        var mockTranscriberFactory = CreateErrorTranscriberFactory(
            new TranscriberException(
                TranscriberErrorType.HttpError,
                "Server error",
                statusCode: 500
            )
        );

        var controller = CreateController(transcriberFactoryMock: mockTranscriberFactory);

        await controller.HandleKeyDownAsync();
        await controller.HandleKeyUpAsync();

        Assert.False(controller.IsRecording);
        Assert.False(controller.IsProcessing);
    }

    [Fact]
    public async Task HandleKeyUp_Timeout_ReturnsToIdle()
    {
        var mockTranscriberFactory = CreateErrorTranscriberFactory(
            new TranscriberException(TranscriberErrorType.NetworkTimeout, "Timeout")
        );

        var controller = CreateController(transcriberFactoryMock: mockTranscriberFactory);

        await controller.HandleKeyDownAsync();
        await controller.HandleKeyUpAsync();

        Assert.False(controller.IsRecording);
        Assert.False(controller.IsProcessing);
    }

    #endregion

    #region Temp WAV Cleanup

    [Fact]
    public async Task HandleKeyUp_CleansUpTempWavFile()
    {
        string? recordedPath = null;

        var controller = CreateController(
            recordFunc: (cfg, path, ct) =>
                Task.Run(async () =>
                {
                    recordedPath = path;
                    CreateDummyWavFile(path);
                    try
                    {
                        await Task.Delay(Timeout.Infinite, ct);
                    }
                    catch (OperationCanceledException) { }

                    return new RecordingStats { DurationMs = 200, BytesRecorded = 32000 };
                })
        );

        await controller.HandleKeyDownAsync();
        await controller.HandleKeyUpAsync();

        Assert.NotNull(recordedPath);
        Assert.Contains("tailslap_typeless_", recordedPath!);
        // File should have been cleaned up
        Assert.False(File.Exists(recordedPath!));
    }

    #endregion

    #region History Entry

    [Fact]
    public async Task HandleKeyUp_Success_CreatesHistoryEntry()
    {
        var mockTranscriberFactory = CreateMockTranscriberFactory(out _, "hello world");
        var mockClipboard = new Mock<IClipboardService>();
        mockClipboard.Setup(c => c.SetText(It.IsAny<string>())).Returns(true);
        var mockHistory = new Mock<IHistoryService>();

        var controller = CreateController(
            transcriberFactoryMock: mockTranscriberFactory,
            clipboardMock: mockClipboard,
            historyMock: mockHistory
        );

        await controller.HandleKeyDownAsync();
        await controller.HandleKeyUpAsync();

        mockHistory.Verify(h => h.AppendTranscription("hello world", 1500), Times.Once);
    }

    [Fact]
    public async Task HandleKeyUp_Failure_NoHistoryEntry()
    {
        var mockTranscriberFactory = CreateErrorTranscriberFactory(
            new Exception("Something went wrong")
        );
        var mockHistory = new Mock<IHistoryService>();

        var controller = CreateController(
            transcriberFactoryMock: mockTranscriberFactory,
            historyMock: mockHistory
        );

        await controller.HandleKeyDownAsync();
        await controller.HandleKeyUpAsync();

        mockHistory.Verify(
            h => h.AppendTranscription(It.IsAny<string>(), It.IsAny<int>()),
            Times.Never
        );
    }

    #endregion

    #region Partial SSE Results

    [Fact]
    public async Task HandleKeyUp_MultipleSSEChunks_ConcatenatesCorrectly()
    {
        var mockTranscriberFactory = CreateMockTranscriberFactory(out _, "hello ", "world");
        var mockClipboard = new Mock<IClipboardService>();
        mockClipboard.Setup(c => c.SetText(It.IsAny<string>())).Returns(true);
        var mockHistory = new Mock<IHistoryService>();

        var controller = CreateController(
            transcriberFactoryMock: mockTranscriberFactory,
            clipboardMock: mockClipboard,
            historyMock: mockHistory
        );

        await controller.HandleKeyDownAsync();
        await controller.HandleKeyUpAsync();

        // History should contain the full concatenated text
        mockHistory.Verify(h => h.AppendTranscription("hello world", 1500), Times.Once);
    }

    #endregion

    #region OnStarted / OnCompleted Events

    [Fact]
    public async Task OnStarted_FiresOnSuccessfulKeyDown()
    {
        var controller = CreateController();

        bool startedFired = false;
        controller.OnStarted += () => startedFired = true;

        await controller.HandleKeyDownAsync();
        Assert.True(startedFired);
    }

    [Fact]
    public async Task OnCompleted_FiresAfterSuccessfulTranscription()
    {
        var mockTranscriberFactory = CreateMockTranscriberFactory(out _, "test result");
        var mockClipboard = new Mock<IClipboardService>();
        mockClipboard.Setup(c => c.SetText(It.IsAny<string>())).Returns(true);

        var controller = CreateController(
            transcriberFactoryMock: mockTranscriberFactory,
            clipboardMock: mockClipboard
        );

        bool completedFired = false;
        controller.OnCompleted += () => completedFired = true;

        await controller.HandleKeyDownAsync();
        Assert.False(completedFired); // Not yet

        await controller.HandleKeyUpAsync();
        Assert.True(completedFired);
    }

    [Fact]
    public async Task OnCompleted_FiresAfterShortRecordingDiscard()
    {
        var controller = CreateController(recordFunc: CreateRecordFunc(durationMs: 100));

        bool completedFired = false;
        controller.OnCompleted += () => completedFired = true;

        await controller.HandleKeyDownAsync();
        await controller.HandleKeyUpAsync();

        Assert.True(completedFired);
    }

    [Fact]
    public async Task OnCompleted_FiresAfterTranscriptionError()
    {
        var mockTranscriberFactory = CreateErrorTranscriberFactory(new Exception("Error"));

        var controller = CreateController(transcriberFactoryMock: mockTranscriberFactory);

        bool completedFired = false;
        controller.OnCompleted += () => completedFired = true;

        await controller.HandleKeyDownAsync();
        await controller.HandleKeyUpAsync();

        Assert.True(completedFired);
    }

    #endregion

    #region Multiple Sequential Sessions

    [Fact]
    public async Task MultipleSequentialSessions_WorkCorrectly()
    {
        var mockTranscriberFactory = CreateMockTranscriberFactory(out _, "first result");
        var mockClipboard = new Mock<IClipboardService>();
        mockClipboard.Setup(c => c.SetText(It.IsAny<string>())).Returns(true);

        var controller = CreateController(
            transcriberFactoryMock: mockTranscriberFactory,
            clipboardMock: mockClipboard
        );

        // First session
        await controller.HandleKeyDownAsync();
        Assert.True(controller.IsRecording);
        await controller.HandleKeyUpAsync();
        Assert.False(controller.IsRecording);
        Assert.False(controller.IsProcessing);

        // Second session should work
        await controller.HandleKeyDownAsync();
        Assert.True(controller.IsRecording);
        await controller.HandleKeyUpAsync();
        Assert.False(controller.IsRecording);
        Assert.False(controller.IsProcessing);
    }

    [Fact]
    public async Task SessionAfterError_StillWorks()
    {
        var callCount = 0;
        var mockTranscriberFactory = new Mock<IRemoteTranscriberFactory>();
        var mockTranscriber = new Mock<IRemoteTranscriber>();

        async IAsyncEnumerable<string> StreamOrThrow(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default
        )
        {
            await Task.Yield();
            callCount++;
            if (callCount == 1)
            {
                yield return ""; // Satisfy compiler
                throw new Exception("First call fails");
            }
            yield return "second result";
        }

        mockTranscriber
            .Setup(t =>
                t.TranscribeStreamingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())
            )
            .Returns((string p, CancellationToken ct) => StreamOrThrow(ct));
        mockTranscriberFactory
            .Setup(f => f.Create(It.IsAny<TranscriberConfig>()))
            .Returns(mockTranscriber.Object);

        var mockClipboard = new Mock<IClipboardService>();
        mockClipboard.Setup(c => c.SetText(It.IsAny<string>())).Returns(true);
        var mockHistory = new Mock<IHistoryService>();

        var controller = CreateController(
            transcriberFactoryMock: mockTranscriberFactory,
            clipboardMock: mockClipboard,
            historyMock: mockHistory
        );

        // First session fails
        await controller.HandleKeyDownAsync();
        await controller.HandleKeyUpAsync();
        Assert.False(controller.IsProcessing);

        // Second session succeeds
        await controller.HandleKeyDownAsync();
        await controller.HandleKeyUpAsync();
        Assert.False(controller.IsProcessing);

        mockHistory.Verify(h => h.AppendTranscription("second result", 1500), Times.Once);
    }

    #endregion

    #region HandleKeyUp When Idle

    [Fact]
    public async Task HandleKeyUp_WhenIdle_DoesNothing()
    {
        var controller = CreateController();

        // Key-up without key-down should be a no-op
        await controller.HandleKeyUpAsync();

        Assert.False(controller.IsRecording);
        Assert.False(controller.IsProcessing);
    }

    #endregion

    #region Microphone Init Failure

    [Fact]
    public async Task HandleKeyDown_MicInitFailure_ReturnsToIdle()
    {
        var controller = CreateController(
            recordFunc: CreateErrorRecordFunc(
                new InvalidOperationException("No audio input device found.")
            )
        );

        bool completedFired = false;
        controller.OnCompleted += () => completedFired = true;

        await controller.HandleKeyDownAsync();

        // Wait for the fire-and-forget recording task to complete
        await Task.Delay(200);

        Assert.False(controller.IsRecording);
        Assert.True(completedFired);
    }

    #endregion

    #region Recording Duration at Threshold

    [Fact]
    public async Task HandleKeyUp_Exactly500ms_StartsTranscription()
    {
        var mockTranscriberFactory = CreateMockTranscriberFactory(out _, "test");
        var mockClipboard = new Mock<IClipboardService>();
        mockClipboard.Setup(c => c.SetText(It.IsAny<string>())).Returns(true);
        var mockHistory = new Mock<IHistoryService>();

        var controller = CreateController(
            transcriberFactoryMock: mockTranscriberFactory,
            clipboardMock: mockClipboard,
            historyMock: mockHistory,
            recordFunc: CreateRecordFunc(durationMs: 500)
        );

        await controller.HandleKeyDownAsync();
        await controller.HandleKeyUpAsync();

        // Exactly 500ms should be transcribed (>=500 threshold)
        mockHistory.Verify(h => h.AppendTranscription("test", 500), Times.Once);
    }

    [Fact]
    public async Task HandleKeyUp_499ms_DiscardsWithoutTranscription()
    {
        var mockTranscriberFactory = CreateMockTranscriberFactory(out var mockTranscriber, "test");

        var controller = CreateController(
            transcriberFactoryMock: mockTranscriberFactory,
            recordFunc: CreateRecordFunc(durationMs: 499)
        );

        await controller.HandleKeyDownAsync();
        await controller.HandleKeyUpAsync();

        mockTranscriber.Verify(
            t => t.TranscribeStreamingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    #endregion
}
