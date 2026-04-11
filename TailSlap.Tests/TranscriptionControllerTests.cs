using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using TailSlap;
using Xunit;

namespace TailSlap.Tests;

internal sealed class TestableStreamingTextTyper : TextTyper
{
    public List<string> TypedTexts { get; } = new();

    public TestableStreamingTextTyper(IClipboardService clip)
        : base(clip) { }

    public override async Task<TypeResult> TypeAsync(
        string text,
        bool autoPaste = true,
        IntPtr? foregroundWindow = null,
        CancellationToken cancellationToken = default
    )
    {
        TypedTexts.Add(text);
        await Task.Yield();
        return new TypeResult
        {
            DeliverySuccess = true,
            TextOnClipboard = !autoPaste,
            Text = text,
            NewText = text,
        };
    }

    internal override void SendBackspace(int count) { }

    internal override void TypeTextDirectly(string text) { }
}

public class TranscriptionControllerTests
{
    private static AppConfig CreateConfig(bool streamResults)
    {
        return new AppConfig
        {
            Llm = new LlmConfig
            {
                Enabled = false,
                BaseUrl = "http://localhost:11434/v1",
                Model = "llama3.1",
                Temperature = 0.2,
            },
            Transcriber = new TranscriberConfig
            {
                Enabled = true,
                BaseUrl = "http://localhost:18000/v1",
                Model = "glm-nano-2512",
                TimeoutSeconds = 30,
                AutoPaste = false,
                EnableAutoEnhance = false,
                EnableVAD = false,
                StreamResults = streamResults,
            },
        };
    }

    private static Mock<IRemoteTranscriber> CreateStreamingTranscriber(params string[] chunks)
    {
        var transcriberMock = new Mock<IRemoteTranscriber>();

        async IAsyncEnumerable<string> Stream(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default
        )
        {
            foreach (var chunk in chunks)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return chunk;
            }
        }

        transcriberMock
            .Setup(t =>
                t.TranscribeStreamingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())
            )
            .Returns((string _, CancellationToken ct) => Stream(ct));
        transcriberMock
            .Setup(t => t.TranscribeAudioAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("unused");

        return transcriberMock;
    }

    private static TranscriptionController CreateController(
        TestableStreamingTextTyper textTyper,
        Mock<IClipboardService>? clipboardService = null
    )
    {
        clipboardService ??= new Mock<IClipboardService>();
        clipboardService.Setup(c => c.SetTextAsync(It.IsAny<string>())).ReturnsAsync(true);
        clipboardService.Setup(c => c.PasteAsync()).ReturnsAsync(true);
        clipboardService.Setup(c => c.SetTextAndPasteAsync(It.IsAny<string>())).ReturnsAsync(true);

        return new TranscriptionController(
            new Mock<IConfigService>().Object,
            new ClipboardHelper(clipboardService.Object),
            new Mock<IRemoteTranscriberFactory>().Object,
            new Mock<IAudioRecorderFactory>().Object,
            new Mock<IHistoryService>().Object,
            new Mock<ITextRefinerFactory>().Object,
            textTyper
        );
    }

    [Fact]
    public async Task StreamingTranscription_TypesAccumulatedChunks()
    {
        var clipboardService = new Mock<IClipboardService>();
        var textTyper = new TestableStreamingTextTyper(clipboardService.Object);
        var controller = CreateController(textTyper, clipboardService);
        var transcriberMock = CreateStreamingTranscriber("hello ", "world");
        var tempFile = Path.GetTempFileName();
        await File.WriteAllBytesAsync(tempFile, new byte[] { 0, 1, 2, 3 });

        try
        {
            var result = await InvokeStreamingTranscriptionAsync(
                controller,
                transcriberMock.Object,
                tempFile,
                CreateConfig(streamResults: true)
            );

            transcriberMock.Verify(
                t => t.TranscribeStreamingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Once
            );
            Assert.Equal("hello world", result);
            Assert.Equal(new[] { "hello ", "hello world" }, textTyper.TypedTexts);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ApplyFinalTextAsync_WhenStreamedAndChanged_RetypesEnhancedText()
    {
        var clipboardService = new Mock<IClipboardService>();
        var textTyper = new TestableStreamingTextTyper(clipboardService.Object);
        var controller = CreateController(textTyper, clipboardService);

        await InvokeApplyFinalTextAsync(
            controller,
            "hello world!",
            "hello world",
            CreateConfig(streamResults: true),
            streamedResults: true
        );

        Assert.Equal(new[] { "hello world!" }, textTyper.TypedTexts);
    }

    [Fact]
    public async Task ApplyFinalTextAsync_WhenNotStreamed_UsesClipboardHelperPath()
    {
        var clipboardService = new Mock<IClipboardService>();
        clipboardService.Setup(c => c.SetTextAsync(It.IsAny<string>())).ReturnsAsync(true);
        var textTyper = new TestableStreamingTextTyper(clipboardService.Object);
        var controller = CreateController(textTyper, clipboardService);

        await InvokeApplyFinalTextAsync(
            controller,
            "hello world",
            "hello world",
            CreateConfig(streamResults: false),
            streamedResults: false
        );

        clipboardService.Verify(c => c.SetTextAsync("hello world"), Times.Once);
        Assert.Empty(textTyper.TypedTexts);
    }

    private static async Task<string> InvokeStreamingTranscriptionAsync(
        TranscriptionController controller,
        IRemoteTranscriber transcriber,
        string audioFilePath,
        AppConfig cfg
    )
    {
        var method = typeof(TranscriptionController).GetMethod(
            "TranscribeRecordedAudioStreamingAsync",
            BindingFlags.Instance | BindingFlags.NonPublic
        );

        Assert.NotNull(method);

        var task =
            (Task<string>)
                method!.Invoke(controller, new object[] { transcriber, audioFilePath, cfg })!;
        return await task;
    }

    private static async Task InvokeApplyFinalTextAsync(
        TranscriptionController controller,
        string finalText,
        string originalText,
        AppConfig cfg,
        bool streamedResults
    )
    {
        var method = typeof(TranscriptionController).GetMethod(
            "ApplyFinalTextAsync",
            BindingFlags.Instance | BindingFlags.NonPublic
        );

        Assert.NotNull(method);

        var task = (Task)
            method!.Invoke(
                controller,
                new object[] { finalText, originalText, cfg, streamedResults }
            )!;
        await task;
    }
}
