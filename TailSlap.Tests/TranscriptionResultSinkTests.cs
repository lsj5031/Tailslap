using System;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using TailSlap;
using Xunit;

namespace TailSlap.Tests;

public class TranscriptionResultSinkTests
{
    private sealed class RecordingTextTyper : TextTyper
    {
        private readonly TypeResult _result;

        public RecordingTextTyper(IClipboardService clipboard, bool deliverySuccess = true)
            : base(clipboard)
        {
            _result = new TypeResult
            {
                DeliverySuccess = deliverySuccess,
                TextOnClipboard = !deliverySuccess,
            };
        }

        public string? TypedText { get; private set; }
        public bool? AutoPaste { get; private set; }

        public override Task<TypeResult> TypeAsync(
            string text,
            bool autoPaste = true,
            IntPtr? foregroundWindow = null,
            CancellationToken cancellationToken = default
        )
        {
            TypedText = text;
            AutoPaste = autoPaste;
            return Task.FromResult(_result);
        }
    }

    private static AppConfig CreateConfig(bool autoPaste = true, bool enhance = false)
    {
        return new AppConfig
        {
            Llm = new LlmConfig
            {
                Enabled = enhance,
                Model = "test-model",
                BaseUrl = "http://localhost/v1",
            },
            Transcriber = new TranscriberConfig
            {
                Enabled = true,
                AutoPaste = autoPaste,
                EnableAutoEnhance = enhance,
                AutoEnhanceThresholdChars = 0,
            },
        };
    }

    private static TranscriptionResultSink CreateSink(
        Mock<IHistoryService> history,
        Mock<IClipboardService> clipboard,
        RecordingTextTyper textTyper,
        string? enhancedText = null,
        Action<CancellationToken>? captureToken = null,
        Func<IntPtr>? getForegroundWindow = null,
        Func<IntPtr, uint, string, bool>? isWindowIdentityMatch = null,
        Func<IntPtr, uint, string, bool>? tryRestoreWindow = null
    )
    {
        clipboard.Setup(c => c.SetTextAsync(It.IsAny<string>())).ReturnsAsync(true);
        clipboard.Setup(c => c.PasteAsync(It.IsAny<IntPtr?>())).ReturnsAsync(true);

        var refiner = new Mock<ITextRefiner>();
        refiner
            .Setup(r => r.RefineAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(
                (string text, CancellationToken ct) =>
                {
                    captureToken?.Invoke(ct);
                    return Task.FromResult(enhancedText ?? text);
                }
            );
        var refinerFactory = new Mock<ITextRefinerFactory>();
        refinerFactory.Setup(f => f.Create(It.IsAny<LlmConfig>())).Returns(refiner.Object);

        return new TranscriptionResultSink(
            history.Object,
            refinerFactory.Object,
            new ClipboardHelper(clipboard.Object),
            clipboard.Object,
            textTyper,
            getForegroundWindow,
            isWindowIdentityMatch,
            tryRestoreWindow
        );
    }

    [Fact]
    public async Task ProcessAsync_UnchangedNonStreamedToggle_DeliversRawAndWritesTranscription()
    {
        var history = new Mock<IHistoryService>();
        var clipboard = new Mock<IClipboardService>();
        var typer = new RecordingTextTyper(clipboard.Object);
        var sink = CreateSink(history, clipboard, typer);

        var result = await sink.ProcessAsync(
            new TranscriptionResultRequest(
                "raw",
                CreateConfig(autoPaste: true),
                123,
                TranscriptionDeliveryPolicy.DeliverFinalText
            )
        );

        Assert.Equal("raw", result.FinalText);
        Assert.False(result.WasEnhanced);
        clipboard.Verify(c => c.SetTextAsync("raw"), Times.Once);
        clipboard.Verify(c => c.PasteAsync(), Times.Once);
        history.Verify(h => h.AppendTranscription("raw", 123), Times.Once);
        history.Verify(
            h => h.Append(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never
        );
    }

    [Fact]
    public async Task ProcessAsync_EnhancedNonStreamedToggle_DeliversFinalAndWritesBothHistories()
    {
        var history = new Mock<IHistoryService>();
        var clipboard = new Mock<IClipboardService>();
        var typer = new RecordingTextTyper(clipboard.Object);
        var sink = CreateSink(history, clipboard, typer, "raw improved");

        var result = await sink.ProcessAsync(
            new TranscriptionResultRequest(
                "raw",
                CreateConfig(enhance: true),
                456,
                TranscriptionDeliveryPolicy.DeliverFinalText
            )
        );

        Assert.True(result.WasEnhanced);
        clipboard.Verify(c => c.SetTextAsync("raw improved"), Times.Once);
        history.Verify(h => h.AppendTranscription("raw", 456), Times.Once);
        history.Verify(h => h.Append("raw", "raw improved", "test-model"), Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_UnchangedStreamedToggle_DoesNotDuplicateDelivery()
    {
        var history = new Mock<IHistoryService>();
        var clipboard = new Mock<IClipboardService>();
        var typer = new RecordingTextTyper(clipboard.Object);
        var sink = CreateSink(history, clipboard, typer);

        await sink.ProcessAsync(
            new TranscriptionResultRequest(
                "raw",
                CreateConfig(),
                1,
                TranscriptionDeliveryPolicy.DeliverFinalText,
                ResultsAlreadyStreamed: true
            )
        );

        Assert.Null(typer.TypedText);
        clipboard.Verify(c => c.SetTextAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ProcessAsync_EnhancedStreamedToggle_DeliversReplacementThroughTextTyper()
    {
        var history = new Mock<IHistoryService>();
        var clipboard = new Mock<IClipboardService>();
        var typer = new RecordingTextTyper(clipboard.Object);
        var sink = CreateSink(history, clipboard, typer, "raw improved");

        await sink.ProcessAsync(
            new TranscriptionResultRequest(
                "raw",
                CreateConfig(autoPaste: false, enhance: true),
                1,
                TranscriptionDeliveryPolicy.DeliverFinalText,
                ResultsAlreadyStreamed: true
            )
        );

        Assert.Equal("raw improved", typer.TypedText);
        Assert.False(typer.AutoPaste);
    }

    [Fact]
    public async Task ProcessAsync_UnchangedTypeless_DoesNotDeliverAndWritesRawHistory()
    {
        var history = new Mock<IHistoryService>();
        var clipboard = new Mock<IClipboardService>();
        var typer = new RecordingTextTyper(clipboard.Object);
        var sink = CreateSink(history, clipboard, typer);

        await sink.ProcessAsync(
            new TranscriptionResultRequest(
                "raw",
                CreateConfig(),
                789,
                TranscriptionDeliveryPolicy.DeliverOnlyIfEnhanced
            )
        );

        Assert.Null(typer.TypedText);
        clipboard.Verify(c => c.SetTextAsync(It.IsAny<string>()), Times.Never);
        history.Verify(h => h.AppendTranscription("raw", 789), Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_EnhancedTypelessWithAutoPaste_UsesTextTyper()
    {
        var history = new Mock<IHistoryService>();
        var clipboard = new Mock<IClipboardService>();
        var typer = new RecordingTextTyper(clipboard.Object);
        var sink = CreateSink(history, clipboard, typer, "raw improved");

        await sink.ProcessAsync(
            new TranscriptionResultRequest(
                "raw",
                CreateConfig(autoPaste: true, enhance: true),
                1,
                TranscriptionDeliveryPolicy.DeliverOnlyIfEnhanced
            )
        );

        Assert.Equal("raw improved", typer.TypedText);
        Assert.True(typer.AutoPaste);
        clipboard.Verify(c => c.SetTextAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ProcessAsync_EnhancedTypelessWithoutAutoPaste_UsesClipboardOnly()
    {
        var history = new Mock<IHistoryService>();
        var clipboard = new Mock<IClipboardService>();
        var typer = new RecordingTextTyper(clipboard.Object);
        var sink = CreateSink(history, clipboard, typer, "raw improved");

        await sink.ProcessAsync(
            new TranscriptionResultRequest(
                "raw",
                CreateConfig(autoPaste: false, enhance: true),
                1,
                TranscriptionDeliveryPolicy.DeliverOnlyIfEnhanced
            )
        );

        Assert.Null(typer.TypedText);
        clipboard.Verify(c => c.SetTextAsync("raw improved"), Times.Once);
        clipboard.Verify(c => c.PasteAsync(), Times.Never);
    }

    [Fact]
    public async Task ProcessAsync_EnhancedRealtime_UsesClipboardOnlyAndWritesBothHistories()
    {
        var history = new Mock<IHistoryService>();
        var clipboard = new Mock<IClipboardService>();
        var typer = new RecordingTextTyper(clipboard.Object);
        var sink = CreateSink(history, clipboard, typer, "raw improved");

        await sink.ProcessAsync(
            new TranscriptionResultRequest(
                "raw",
                CreateConfig(enhance: true),
                101,
                TranscriptionDeliveryPolicy.EnhancedToClipboardWithNotice
            )
        );

        clipboard.Verify(c => c.SetTextAsync("raw improved"), Times.Once);
        clipboard.Verify(c => c.PasteAsync(), Times.Never);
        Assert.Null(typer.TypedText);
        history.Verify(h => h.AppendTranscription("raw", 101), Times.Once);
        history.Verify(h => h.Append("raw", "raw improved", "test-model"), Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_UnchangedRealtime_DoesNotRewriteClipboard()
    {
        var history = new Mock<IHistoryService>();
        var clipboard = new Mock<IClipboardService>();
        var typer = new RecordingTextTyper(clipboard.Object);
        var sink = CreateSink(history, clipboard, typer);

        await sink.ProcessAsync(
            new TranscriptionResultRequest(
                "raw",
                CreateConfig(),
                202,
                TranscriptionDeliveryPolicy.EnhancedToClipboardWithNotice
            )
        );

        clipboard.Verify(c => c.SetTextAsync(It.IsAny<string>()), Times.Never);
        history.Verify(h => h.AppendTranscription("raw", 202), Times.Once);
        history.Verify(
            h => h.Append(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never
        );
    }

    [Fact]
    public async Task ProcessAsync_TranscriptionHistoryFailure_StillAttemptsRefinementHistory()
    {
        var history = new Mock<IHistoryService>();
        history
            .Setup(h => h.AppendTranscription(It.IsAny<string>(), It.IsAny<int>()))
            .Throws<InvalidOperationException>();
        var clipboard = new Mock<IClipboardService>();
        var typer = new RecordingTextTyper(clipboard.Object);
        var sink = CreateSink(history, clipboard, typer, "raw improved");

        await sink.ProcessAsync(
            new TranscriptionResultRequest(
                "raw",
                CreateConfig(enhance: true),
                1,
                TranscriptionDeliveryPolicy.DeliverOnlyIfEnhanced
            )
        );

        history.Verify(h => h.Append("raw", "raw improved", "test-model"), Times.Once);
    }

    [Theory]
    [InlineData(true, 0x11L, 123u, "NotFirefox", true)]
    [InlineData(true, 0x11L, 0u, "NotFirefox", false)]
    [InlineData(true, 0x11L, 123u, "MozillaWindowClass", false)]
    [InlineData(true, 0x11L, 123u, "", false)]
    [InlineData(false, 0x11L, 123u, "NotFirefox", false)]
    public void ShouldAttemptBestEffortRestore_RequiresTrustedNonFirefoxTarget(
        bool foregroundChanged,
        long targetWindowValue,
        uint targetProcessId,
        string targetWindowClass,
        bool expected
    )
    {
        Assert.Equal(
            expected,
            TranscriptionResultSink.ShouldAttemptBestEffortRestore(
                new IntPtr(targetWindowValue),
                targetProcessId,
                targetWindowClass,
                foregroundChanged
            )
        );
    }

    [Fact]
    public async Task ProcessAsync_TargetIdentityLost_LeavesTextOnClipboardWithoutPaste()
    {
        var history = new Mock<IHistoryService>();
        var clipboard = new Mock<IClipboardService>();
        var typer = new RecordingTextTyper(clipboard.Object);
        var sink = CreateSink(
            history,
            clipboard,
            typer,
            getForegroundWindow: () => new IntPtr(0x99),
            isWindowIdentityMatch: (_, _, _) => false
        );

        await sink.ProcessAsync(
            new TranscriptionResultRequest(
                "raw",
                CreateConfig(autoPaste: true),
                123,
                TranscriptionDeliveryPolicy.DeliverFinalText,
                TargetWindow: new IntPtr(0x11),
                TargetProcessId: 123,
                TargetWindowClass: "NotFirefox"
            )
        );

        // Guard triggered: text is preserved on the clipboard, but no paste fires.
        clipboard.Verify(c => c.SetTextAsync("raw"), Times.Once);
        clipboard.Verify(c => c.PasteAsync(It.IsAny<IntPtr?>()), Times.Never);
        history.Verify(h => h.AppendTranscription("raw", 123), Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_TargetWindowChanged_RestoresTargetAndPastes()
    {
        var history = new Mock<IHistoryService>();
        var clipboard = new Mock<IClipboardService>();
        var typer = new RecordingTextTyper(clipboard.Object);
        var foreground = new IntPtr(0x99);
        var sink = CreateSink(
            history,
            clipboard,
            typer,
            getForegroundWindow: () => foreground,
            isWindowIdentityMatch: (_, _, _) => true,
            tryRestoreWindow: (window, _, _) =>
            {
                foreground = window;
                return true;
            }
        );

        await sink.ProcessAsync(
            new TranscriptionResultRequest(
                "raw",
                CreateConfig(autoPaste: true),
                123,
                TranscriptionDeliveryPolicy.DeliverFinalText,
                TargetWindow: new IntPtr(0x11),
                TargetProcessId: 123,
                TargetWindowClass: "NotFirefox"
            )
        );

        clipboard.Verify(c => c.SetTextAsync("raw"), Times.Once);
        clipboard.Verify(c => c.PasteAsync(new IntPtr(0x11)), Times.Once);
        history.Verify(h => h.AppendTranscription("raw", 123), Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_TargetWindowChanged_RestoreFails_LeavesTextOnClipboard()
    {
        var history = new Mock<IHistoryService>();
        var clipboard = new Mock<IClipboardService>();
        var typer = new RecordingTextTyper(clipboard.Object);
        var sink = CreateSink(
            history,
            clipboard,
            typer,
            getForegroundWindow: () => new IntPtr(0x99),
            isWindowIdentityMatch: (_, _, _) => true,
            tryRestoreWindow: (_, _, _) => false
        );

        await sink.ProcessAsync(
            new TranscriptionResultRequest(
                "raw",
                CreateConfig(autoPaste: true),
                123,
                TranscriptionDeliveryPolicy.DeliverFinalText,
                TargetWindow: new IntPtr(0x11),
                TargetProcessId: 123,
                TargetWindowClass: "NotFirefox"
            )
        );

        clipboard.Verify(c => c.SetTextAsync("raw"), Times.Once);
        clipboard.Verify(c => c.PasteAsync(It.IsAny<IntPtr?>()), Times.Never);
    }

    [Fact]
    public async Task ProcessAsync_FirefoxTargetChanged_LeavesTextOnClipboardWithoutRestore()
    {
        var history = new Mock<IHistoryService>();
        var clipboard = new Mock<IClipboardService>();
        var typer = new RecordingTextTyper(clipboard.Object);
        bool restoreAttempted = false;
        var sink = CreateSink(
            history,
            clipboard,
            typer,
            getForegroundWindow: () => new IntPtr(0x99),
            isWindowIdentityMatch: (_, _, _) => true,
            tryRestoreWindow: (_, _, _) =>
            {
                restoreAttempted = true;
                return true;
            }
        );

        await sink.ProcessAsync(
            new TranscriptionResultRequest(
                "raw",
                CreateConfig(autoPaste: true),
                123,
                TranscriptionDeliveryPolicy.DeliverFinalText,
                TargetWindow: new IntPtr(0x11),
                TargetProcessId: 123,
                TargetWindowClass: "MozillaWindowClass"
            )
        );

        Assert.False(restoreAttempted);
        clipboard.Verify(c => c.SetTextAsync("raw"), Times.Once);
        clipboard.Verify(c => c.PasteAsync(It.IsAny<IntPtr?>()), Times.Never);
    }

    [Fact]
    public async Task ProcessAsync_TargetUnchangedWithoutIdentity_StillPastes()
    {
        var history = new Mock<IHistoryService>();
        var clipboard = new Mock<IClipboardService>();
        var typer = new RecordingTextTyper(clipboard.Object);
        var sink = CreateSink(
            history,
            clipboard,
            typer,
            getForegroundWindow: () => new IntPtr(0x11),
            isWindowIdentityMatch: (_, _, _) => false
        );

        await sink.ProcessAsync(
            new TranscriptionResultRequest(
                "raw",
                CreateConfig(autoPaste: true),
                123,
                TranscriptionDeliveryPolicy.DeliverFinalText,
                TargetWindow: new IntPtr(0x11)
            )
        );

        // Identity capture can fail benignly; an unchanged foreground must still paste.
        clipboard.Verify(c => c.SetTextAsync("raw"), Times.Once);
        clipboard.Verify(c => c.PasteAsync(new IntPtr(0x11)), Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_TargetWindowUnchanged_PastesNormally()
    {
        var history = new Mock<IHistoryService>();
        var clipboard = new Mock<IClipboardService>();
        var typer = new RecordingTextTyper(clipboard.Object);
        var sink = CreateSink(
            history,
            clipboard,
            typer,
            getForegroundWindow: () => new IntPtr(0x11),
            isWindowIdentityMatch: (_, _, _) => true
        );

        await sink.ProcessAsync(
            new TranscriptionResultRequest(
                "raw",
                CreateConfig(autoPaste: true),
                123,
                TranscriptionDeliveryPolicy.DeliverFinalText,
                TargetWindow: new IntPtr(0x11),
                TargetProcessId: 123,
                TargetWindowClass: "NotFirefox"
            )
        );

        clipboard.Verify(c => c.SetTextAsync("raw"), Times.Once);
        clipboard.Verify(c => c.PasteAsync(new IntPtr(0x11)), Times.Once);
        history.Verify(h => h.AppendTranscription("raw", 123), Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_CancellationToken_IsPassedToEnhancementAndRawHistoryRemainsValid()
    {
        var history = new Mock<IHistoryService>();
        var clipboard = new Mock<IClipboardService>();
        var typer = new RecordingTextTyper(clipboard.Object);
        CancellationToken observedToken = default;
        var sink = CreateSink(history, clipboard, typer, "raw improved", ct => observedToken = ct);
        using var cts = new CancellationTokenSource();

        await sink.ProcessAsync(
            new TranscriptionResultRequest(
                "raw",
                CreateConfig(enhance: true),
                303,
                TranscriptionDeliveryPolicy.DeliverOnlyIfEnhanced
            ),
            cts.Token
        );

        Assert.Equal(cts.Token, observedToken);
        history.Verify(h => h.AppendTranscription("raw", 303), Times.Once);
    }
}
