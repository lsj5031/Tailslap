using System;
using System.Reflection;
using System.Threading.Tasks;
using Moq;
using TailSlap;
using Xunit;

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
                BaseUrl = "http://localhost:18000/v1",
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
            mockAudioRecorderFactory.Object,
            new Mock<IHistoryService>().Object,
            new Mock<ITextRefinerFactory>().Object
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
                mockAudioRecorderFactory.Object,
                new Mock<IHistoryService>().Object,
                new Mock<ITextRefinerFactory>().Object
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
            mockAudioRecorderFactory.Object,
            new Mock<IHistoryService>().Object,
            new Mock<ITextRefinerFactory>().Object
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
            mockAudioRecorderFactory.Object,
            new Mock<IHistoryService>().Object,
            new Mock<ITextRefinerFactory>().Object
        );

        // Assert
        Assert.Equal(StreamingState.Idle, controller.State);
        Assert.False(controller.IsStreaming);
    }

    [Fact]
    public void Factory_CreatesCustomTranscriber_ByDefault()
    {
        var factory = new RealtimeTranscriberFactory();
        var config = new TranscriberConfig
        {
            BaseUrl = "http://localhost:18000/v1",
            RealtimeProvider = "custom",
        };
        var transcriber = factory.Create(config);
        Assert.IsType<RealtimeTranscriber>(transcriber);
    }

    [Fact]
    public void Factory_CreatesOpenAITranscriber_WhenProviderIsOpenAI()
    {
        var factory = new RealtimeTranscriberFactory();
        var config = new TranscriberConfig
        {
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o-transcribe",
            RealtimeProvider = "openai",
        };
        var transcriber = factory.Create(config);
        Assert.IsType<OpenAIRealtimeTranscriber>(transcriber);
    }

    [Fact]
    public async Task ProcessTranscriptionAsync_FinalUnchangedText_StillFinalizesItem()
    {
        var mockConfig = CreateMockConfigService();
        var mockClip = new Mock<IClipboardService>();
        var mockTranscriberFactory = new Mock<IRealtimeTranscriberFactory>();
        var mockAudioRecorderFactory = new Mock<IAudioRecorderFactory>();

        var controller = new RealtimeTranscriptionController(
            mockConfig.Object,
            mockClip.Object,
            mockTranscriberFactory.Object,
            mockAudioRecorderFactory.Object,
            new Mock<IHistoryService>().Object,
            new Mock<ITextRefinerFactory>().Object
        );

        SetPrivateField(controller, "_streamingState", StreamingState.Streaming);
        SetPrivateField(controller, "_realtimeTranscriptionText", "hello world");
        SetPrivateField(controller, "_lastTypedLength", "hello world".Length);
        SetPrivateField(controller, "_currentRealtimeItemId", "item-1");

        await InvokeProcessTranscriptionAsync(controller, "hello world", true, "item-1");

        Assert.Equal("", GetPrivateField<string>(controller, "_realtimeTranscriptionText"));
        Assert.Equal(0, GetPrivateField<int>(controller, "_lastTypedLength"));
        Assert.Null(GetPrivateField<string?>(controller, "_currentRealtimeItemId"));
        Assert.Equal("hello world", GetPrivateField<string>(controller, "_typedText"));
    }

    [Fact]
    public async Task ProcessTranscriptionAsync_StoppingWithInterimAllowed_ProcessesUpdate()
    {
        var mockConfig = CreateMockConfigService();
        var mockClip = new Mock<IClipboardService>();
        mockClip.Setup(c => c.SetTextAndPasteAsync("hello!")).ReturnsAsync(true);

        var mockTranscriberFactory = new Mock<IRealtimeTranscriberFactory>();
        var mockAudioRecorderFactory = new Mock<IAudioRecorderFactory>();

        var controller = new RealtimeTranscriptionController(
            mockConfig.Object,
            mockClip.Object,
            mockTranscriberFactory.Object,
            mockAudioRecorderFactory.Object,
            new Mock<IHistoryService>().Object,
            new Mock<ITextRefinerFactory>().Object
        );

        SetPrivateField(controller, "_streamingState", StreamingState.Stopping);
        SetPrivateField(controller, "_allowRealtimeInterimWhileStopping", true);

        await InvokeProcessTranscriptionAsync(controller, "hello!", false, "item-1");

        Assert.Equal("hello!", GetPrivateField<string>(controller, "_realtimeTranscriptionText"));
        Assert.Equal("item-1", GetPrivateField<string?>(controller, "_currentRealtimeItemId"));
        Assert.Equal(6, GetPrivateField<int>(controller, "_lastTypedLength"));
    }

    [Fact]
    public async Task ProcessTranscriptionAsync_LegacyFinal_KeepsBaselinePending()
    {
        var mockConfig = CreateMockConfigService();
        var mockClip = new Mock<IClipboardService>();
        mockClip.Setup(c => c.SetTextAndPasteAsync("hello world")).ReturnsAsync(true);

        var mockTranscriberFactory = new Mock<IRealtimeTranscriberFactory>();
        var mockAudioRecorderFactory = new Mock<IAudioRecorderFactory>();

        var controller = new RealtimeTranscriptionController(
            mockConfig.Object,
            mockClip.Object,
            mockTranscriberFactory.Object,
            mockAudioRecorderFactory.Object,
            new Mock<IHistoryService>().Object,
            new Mock<ITextRefinerFactory>().Object
        );

        SetPrivateField(controller, "_streamingState", StreamingState.Streaming);

        await InvokeProcessTranscriptionAsync(controller, "hello world", true, null);

        Assert.Equal(
            "hello world",
            GetPrivateField<string>(controller, "_realtimeTranscriptionText")
        );
        Assert.Equal("hello world".Length, GetPrivateField<int>(controller, "_lastTypedLength"));
        Assert.Equal("", GetPrivateField<string>(controller, "_typedText"));
        Assert.True(GetPrivateField<bool>(controller, "_legacyFinalPending"));
        Assert.Equal("hello world", GetPrivateField<string>(controller, "_pendingLegacyFinalText"));
    }

    [Fact]
    public async Task ProcessLegacyTranscriptionEvent_CumulativeUpdate_DoesNotAppendPreviousText()
    {
        var mockConfig = CreateMockConfigService();
        var mockClip = new Mock<IClipboardService>();
        mockClip.Setup(c => c.SetTextAndPasteAsync("hello world")).ReturnsAsync(true);
        mockClip.Setup(c => c.SetTextAndPasteAsync(" again")).ReturnsAsync(true);

        var mockTranscriberFactory = new Mock<IRealtimeTranscriberFactory>();
        var mockAudioRecorderFactory = new Mock<IAudioRecorderFactory>();

        var controller = new RealtimeTranscriptionController(
            mockConfig.Object,
            mockClip.Object,
            mockTranscriberFactory.Object,
            mockAudioRecorderFactory.Object,
            new Mock<IHistoryService>().Object,
            new Mock<ITextRefinerFactory>().Object
        );

        SetPrivateField(controller, "_streamingState", StreamingState.Streaming);

        await InvokeProcessTranscriptionAsync(controller, "hello world", true, null);
        await InvokeProcessLegacyTranscriptionEvent(controller, "hello world again", false);

        Assert.Equal(
            "hello world again",
            GetPrivateField<string>(controller, "_realtimeTranscriptionText")
        );
        Assert.Equal("", GetPrivateField<string>(controller, "_typedText"));
        Assert.False(GetPrivateField<bool>(controller, "_legacyFinalPending"));
    }

    [Fact]
    public async Task ProcessLegacyTranscriptionEvent_NewSegmentAfterFinal_CommitsPreviousText()
    {
        var mockConfig = CreateMockConfigService();
        var mockClip = new Mock<IClipboardService>();
        mockClip.Setup(c => c.SetTextAndPasteAsync("hello world")).ReturnsAsync(true);
        mockClip.Setup(c => c.SetTextAndPasteAsync("next sentence")).ReturnsAsync(true);

        var mockTranscriberFactory = new Mock<IRealtimeTranscriberFactory>();
        var mockAudioRecorderFactory = new Mock<IAudioRecorderFactory>();

        var controller = new RealtimeTranscriptionController(
            mockConfig.Object,
            mockClip.Object,
            mockTranscriberFactory.Object,
            mockAudioRecorderFactory.Object,
            new Mock<IHistoryService>().Object,
            new Mock<ITextRefinerFactory>().Object
        );

        SetPrivateField(controller, "_streamingState", StreamingState.Streaming);

        await InvokeProcessTranscriptionAsync(controller, "hello world", true, null);
        await InvokeProcessLegacyTranscriptionEvent(controller, "next sentence", false);

        Assert.Equal("hello world", GetPrivateField<string>(controller, "_typedText"));
        Assert.Equal(
            "next sentence",
            GetPrivateField<string>(controller, "_realtimeTranscriptionText")
        );
        Assert.Equal("next sentence".Length, GetPrivateField<int>(controller, "_lastTypedLength"));
        Assert.False(GetPrivateField<bool>(controller, "_legacyFinalPending"));
    }

    [Fact]
    public async Task ProcessLegacyTranscriptionEvent_SimilarCorrection_ReusesPendingBaseline()
    {
        var mockConfig = CreateMockConfigService();
        var mockClip = new Mock<IClipboardService>();
        const string first = "normal people use administrator access for everything.";
        const string corrected = "normal people use administrator rights for everything.";
        mockClip.Setup(c => c.SetTextAndPasteAsync(first)).ReturnsAsync(true);
        mockClip.Setup(c => c.SetTextAndPasteAsync("rights for everything.")).ReturnsAsync(true);

        var mockTranscriberFactory = new Mock<IRealtimeTranscriberFactory>();
        var mockAudioRecorderFactory = new Mock<IAudioRecorderFactory>();

        var controller = new RealtimeTranscriptionController(
            mockConfig.Object,
            mockClip.Object,
            mockTranscriberFactory.Object,
            mockAudioRecorderFactory.Object,
            new Mock<IHistoryService>().Object,
            new Mock<ITextRefinerFactory>().Object
        );

        SetPrivateField(controller, "_streamingState", StreamingState.Streaming);

        await InvokeProcessTranscriptionAsync(controller, first, true, null);
        await InvokeProcessLegacyTranscriptionEvent(controller, corrected, false);

        Assert.Equal(corrected, GetPrivateField<string>(controller, "_realtimeTranscriptionText"));
        Assert.Equal("", GetPrivateField<string>(controller, "_typedText"));
        Assert.False(GetPrivateField<bool>(controller, "_legacyFinalPending"));
    }

    [Fact]
    public void CanProcessOrderedRealtimeUpdate_UnknownPreviousItem_DoesNotBlock()
    {
        var mockConfig = CreateMockConfigService();
        var mockClip = new Mock<IClipboardService>();
        var mockTranscriberFactory = new Mock<IRealtimeTranscriberFactory>();
        var mockAudioRecorderFactory = new Mock<IAudioRecorderFactory>();

        var controller = new RealtimeTranscriptionController(
            mockConfig.Object,
            mockClip.Object,
            mockTranscriberFactory.Object,
            mockAudioRecorderFactory.Object,
            new Mock<IHistoryService>().Object,
            new Mock<ITextRefinerFactory>().Object
        );

        var update = new RealtimeTranscriptionUpdate
        {
            Text = "hello",
            IsFinal = false,
            ItemId = "item-2",
            PreviousItemId = "item-1",
        };

        Assert.True(InvokeCanProcessOrderedRealtimeUpdate(controller, update));
    }

    [Fact]
    public async Task ProcessLegacyTranscriptionEvent_RoutesThroughPump_SerializesApplication()
    {
        var mockConfig = CreateMockConfigService();
        var mockClip = new Mock<IClipboardService>();
        var pasteOrder = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var firstPasteStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseFirstPaste = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        mockClip
            .Setup(c => c.SetTextAndPasteAsync(It.IsAny<string>()))
            .Returns<string>(async text =>
            {
                pasteOrder.Enqueue(text);
                if (pasteOrder.Count == 1)
                {
                    firstPasteStarted.TrySetResult();
                    await releaseFirstPaste.Task;
                }

                return true;
            });

        var mockTranscriberFactory = new Mock<IRealtimeTranscriberFactory>();
        var mockAudioRecorderFactory = new Mock<IAudioRecorderFactory>();

        var controller = new RealtimeTranscriptionController(
            mockConfig.Object,
            mockClip.Object,
            mockTranscriberFactory.Object,
            mockAudioRecorderFactory.Object,
            new Mock<IHistoryService>().Object,
            new Mock<ITextRefinerFactory>().Object
        );

        SetPrivateField(controller, "_streamingState", StreamingState.Streaming);
        SetPrivateField(controller, "_allowRealtimeTextUpdates", true);

        // First legacy final starts processing and blocks on paste.
        InvokeProcessLegacyTranscriptionEventFireAndForget(controller, "hello world", true);
        await firstPasteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Second event while first is still applying — must queue via pump, not race.
        InvokeProcessLegacyTranscriptionEventFireAndForget(controller, "hello world again", true);

        // While first is blocked, second must not have pasted yet (serialized pump).
        Assert.Single(pasteOrder);

        releaseFirstPaste.TrySetResult();
        await WaitForTranscriptionIdleAsync(controller);

        Assert.Equal(new[] { "hello world", " again" }, pasteOrder.ToArray());
        Assert.Null(GetPrivateField<object?>(controller, "_pendingLegacyUpdate"));
    }

    [Fact]
    public void CanProcessOrderedRealtimeUpdate_PendingPreviousItem_StillBlocks()
    {
        var mockConfig = CreateMockConfigService();
        var mockClip = new Mock<IClipboardService>();
        var mockTranscriberFactory = new Mock<IRealtimeTranscriberFactory>();
        var mockAudioRecorderFactory = new Mock<IAudioRecorderFactory>();

        var controller = new RealtimeTranscriptionController(
            mockConfig.Object,
            mockClip.Object,
            mockTranscriberFactory.Object,
            mockAudioRecorderFactory.Object,
            new Mock<IHistoryService>().Object,
            new Mock<ITextRefinerFactory>().Object
        );

        var pendingUpdatesField = typeof(RealtimeTranscriptionController).GetField(
            "_pendingOrderedRealtimeUpdates",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.NotNull(pendingUpdatesField);

        var pendingUpdateType = pendingUpdatesField!.FieldType.GenericTypeArguments[1];
        var pendingUpdate = Activator.CreateInstance(pendingUpdateType);
        Assert.NotNull(pendingUpdate);

        pendingUpdateType
            .GetProperty("Update", BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(
                pendingUpdate,
                new RealtimeTranscriptionUpdate
                {
                    Text = "first",
                    IsFinal = false,
                    ItemId = "item-1",
                }
            );
        pendingUpdateType
            .GetProperty("Sequence", BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(pendingUpdate, 0L);

        var pendingUpdates = pendingUpdatesField.GetValue(controller);
        pendingUpdatesField
            .FieldType.GetMethod("Add")!
            .Invoke(pendingUpdates, new object?[] { "item-1", pendingUpdate });

        var update = new RealtimeTranscriptionUpdate
        {
            Text = "second",
            IsFinal = false,
            ItemId = "item-2",
            PreviousItemId = "item-1",
        };

        Assert.False(InvokeCanProcessOrderedRealtimeUpdate(controller, update));
    }

    [Fact]
    public void DetermineStopWaitTimeoutMs_NoPendingAudioOrText_UsesIdleTimeout()
    {
        var mockConfig = CreateMockConfigService();
        var mockClip = new Mock<IClipboardService>();
        var mockTranscriberFactory = new Mock<IRealtimeTranscriberFactory>();
        var mockAudioRecorderFactory = new Mock<IAudioRecorderFactory>();

        var controller = new RealtimeTranscriptionController(
            mockConfig.Object,
            mockClip.Object,
            mockTranscriberFactory.Object,
            mockAudioRecorderFactory.Object,
            new Mock<IHistoryService>().Object,
            new Mock<ITextRefinerFactory>().Object
        );

        Assert.Equal(600, InvokeDetermineStopWaitTimeoutMs(controller, hasRemainingAudio: false));
    }

    [Fact]
    public void DetermineStopWaitTimeoutMs_PendingRealtimeText_UsesPendingTimeout()
    {
        var mockConfig = CreateMockConfigService();
        var mockClip = new Mock<IClipboardService>();
        var mockTranscriberFactory = new Mock<IRealtimeTranscriberFactory>();
        var mockAudioRecorderFactory = new Mock<IAudioRecorderFactory>();

        var controller = new RealtimeTranscriptionController(
            mockConfig.Object,
            mockClip.Object,
            mockTranscriberFactory.Object,
            mockAudioRecorderFactory.Object,
            new Mock<IHistoryService>().Object,
            new Mock<ITextRefinerFactory>().Object
        );

        SetPrivateField(controller, "_realtimeTranscriptionText", "hello");
        SetPrivateField(controller, "_lastTypedLength", 5);

        Assert.Equal(2500, InvokeDetermineStopWaitTimeoutMs(controller, hasRemainingAudio: false));
    }

    [Fact]
    public void DetermineStopWaitTimeoutMs_RemainingAudio_UsesPendingTimeout()
    {
        var mockConfig = CreateMockConfigService();
        var mockClip = new Mock<IClipboardService>();
        var mockTranscriberFactory = new Mock<IRealtimeTranscriberFactory>();
        var mockAudioRecorderFactory = new Mock<IAudioRecorderFactory>();

        var controller = new RealtimeTranscriptionController(
            mockConfig.Object,
            mockClip.Object,
            mockTranscriberFactory.Object,
            mockAudioRecorderFactory.Object,
            new Mock<IHistoryService>().Object,
            new Mock<ITextRefinerFactory>().Object
        );

        Assert.Equal(2500, InvokeDetermineStopWaitTimeoutMs(controller, hasRemainingAudio: true));
    }

    private static bool InvokeCanProcessOrderedRealtimeUpdate(
        RealtimeTranscriptionController controller,
        RealtimeTranscriptionUpdate update
    )
    {
        var method = typeof(RealtimeTranscriptionController).GetMethod(
            "CanProcessOrderedRealtimeUpdate",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.NotNull(method);
        return Assert.IsType<bool>(method!.Invoke(controller, new object[] { update }));
    }

    private static async Task InvokeProcessTranscriptionAsync(
        RealtimeTranscriptionController controller,
        string text,
        bool isFinal,
        string? itemId
    )
    {
        var method = typeof(RealtimeTranscriptionController).GetMethod(
            "ProcessTranscriptionAsync",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.NotNull(method);

        var task = (Task?)
            method!.Invoke(
                controller,
                new object?[] { text, isFinal, itemId, default(System.Threading.CancellationToken) }
            );

        Assert.NotNull(task);
        await task!;
    }

    private static int InvokeDetermineStopWaitTimeoutMs(
        RealtimeTranscriptionController controller,
        bool hasRemainingAudio
    )
    {
        var method = typeof(RealtimeTranscriptionController).GetMethod(
            "DetermineStopWaitTimeoutMs",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.NotNull(method);
        return Assert.IsType<int>(method!.Invoke(controller, new object[] { hasRemainingAudio }));
    }

    private static async Task InvokeProcessLegacyTranscriptionEvent(
        RealtimeTranscriptionController controller,
        string text,
        bool isFinal
    )
    {
        InvokeProcessLegacyTranscriptionEventFireAndForget(controller, text, isFinal);
        await WaitForTranscriptionIdleAsync(controller);
    }

    private static void InvokeProcessLegacyTranscriptionEventFireAndForget(
        RealtimeTranscriptionController controller,
        string text,
        bool isFinal
    )
    {
        var method = typeof(RealtimeTranscriptionController).GetMethod(
            "ProcessLegacyTranscriptionEvent",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.NotNull(method);
        method!.Invoke(controller, new object[] { text, isFinal });
    }

    private static async Task WaitForTranscriptionIdleAsync(
        RealtimeTranscriptionController controller,
        int timeoutMs = 5000
    )
    {
        var gateField = typeof(RealtimeTranscriptionController).GetField(
            "_transcriptionLock",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.NotNull(gateField);
        var gate = Assert.IsType<System.Threading.SemaphoreSlim>(gateField!.GetValue(controller));

        var pumpField = typeof(RealtimeTranscriptionController).GetField(
            "_queuePumpRunning",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.NotNull(pumpField);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var pumpRunning = (int)pumpField!.GetValue(controller)!;
            if (pumpRunning == 0 && gate.CurrentCount == 1)
            {
                // One more beat in case pump is about to restart from finally.
                await Task.Delay(20);
                pumpRunning = (int)pumpField.GetValue(controller)!;
                if (pumpRunning == 0 && gate.CurrentCount == 1)
                    return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Timed out waiting for realtime transcription pump to idle.");
    }

    [Fact]
    public void HandleRealtimeTranscriptionEvent_WhenUpdatesDisallowed_IgnoresInterim()
    {
        var mockConfig = CreateMockConfigService();
        var mockClip = new Mock<IClipboardService>();
        var mockTranscriberFactory = new Mock<IRealtimeTranscriberFactory>();
        var mockAudioRecorderFactory = new Mock<IAudioRecorderFactory>();

        var controller = new RealtimeTranscriptionController(
            mockConfig.Object,
            mockClip.Object,
            mockTranscriberFactory.Object,
            mockAudioRecorderFactory.Object,
            new Mock<IHistoryService>().Object,
            new Mock<ITextRefinerFactory>().Object
        );

        SetPrivateField(controller, "_streamingState", StreamingState.Streaming);
        SetPrivateField(controller, "_allowRealtimeTextUpdates", false);

        var update = new RealtimeTranscriptionUpdate
        {
            Text = "should be ignored",
            IsFinal = false,
            ItemId = "item-1",
        };

        InvokeHandleRealtimeTranscriptionEvent(controller, update);

        Assert.Equal(0L, GetPrivateField<long>(controller, "_nextOrderedRealtimeSequence"));
    }

    [Fact]
    public async Task CleanupAsync_WithSessionText_PersistsTranscriptionHistory()
    {
        var mockConfig = CreateMockConfigService();
        var mockHistory = new Mock<IHistoryService>();
        var mockRefinerFactory = new Mock<ITextRefinerFactory>();
        var mockClip = new Mock<IClipboardService>();
        var mockTranscriberFactory = new Mock<IRealtimeTranscriberFactory>();
        var mockAudioRecorderFactory = new Mock<IAudioRecorderFactory>();

        var controller = new RealtimeTranscriptionController(
            mockConfig.Object,
            mockClip.Object,
            mockTranscriberFactory.Object,
            mockAudioRecorderFactory.Object,
            mockHistory.Object,
            mockRefinerFactory.Object
        );

        SetPrivateField(controller, "_streamingState", StreamingState.Streaming);
        SetPrivateField(controller, "_typedText", "hello ");
        SetPrivateField(controller, "_realtimeTranscriptionText", "world");
        SetPrivateField(controller, "_streamingStartTime", DateTime.UtcNow.AddSeconds(-5));

        var cleanupTask = (Task)
            typeof(RealtimeTranscriptionController)
                .GetMethod("CleanupAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(controller, null)!;
        await cleanupTask;

        mockHistory.Verify(
            h => h.AppendTranscription("hello world", It.Is<int>(ms => ms >= 0)),
            Times.Once
        );
        Assert.Equal(StreamingState.Idle, controller.State);
    }

    [Fact]
    public async Task CleanupAsync_WithEmptySession_DoesNotPersistHistory()
    {
        var mockConfig = CreateMockConfigService();
        var mockHistory = new Mock<IHistoryService>();
        var controller = new RealtimeTranscriptionController(
            mockConfig.Object,
            new Mock<IClipboardService>().Object,
            new Mock<IRealtimeTranscriberFactory>().Object,
            new Mock<IAudioRecorderFactory>().Object,
            mockHistory.Object,
            new Mock<ITextRefinerFactory>().Object
        );

        SetPrivateField(controller, "_streamingState", StreamingState.Streaming);

        var cleanupTask = (Task)
            typeof(RealtimeTranscriptionController)
                .GetMethod("CleanupAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(controller, null)!;
        await cleanupTask;

        mockHistory.Verify(
            h => h.AppendTranscription(It.IsAny<string>(), It.IsAny<int>()),
            Times.Never
        );
    }

    [Fact]
    public async Task CleanupAsync_DuringPendingUpdates_DoesNotThrow()
    {
        var mockConfig = CreateMockConfigService();
        var mockClip = new Mock<IClipboardService>();
        var mockTranscriberFactory = new Mock<IRealtimeTranscriberFactory>();
        var mockAudioRecorderFactory = new Mock<IAudioRecorderFactory>();

        var controller = new RealtimeTranscriptionController(
            mockConfig.Object,
            mockClip.Object,
            mockTranscriberFactory.Object,
            mockAudioRecorderFactory.Object,
            new Mock<IHistoryService>().Object,
            new Mock<ITextRefinerFactory>().Object
        );

        SetPrivateField(controller, "_streamingState", StreamingState.Streaming);
        SetPrivateField(controller, "_allowRealtimeTextUpdates", true);

        // Enqueue a pending ordered update
        var update = new RealtimeTranscriptionUpdate
        {
            Text = "hello",
            IsFinal = false,
            ItemId = "item-1",
        };
        InvokeHandleRealtimeTranscriptionEvent(controller, update);

        // Verify it was enqueued
        Assert.True(GetPrivateField<long>(controller, "_nextOrderedRealtimeSequence") > 0);

        // Run cleanup via reflection - should not throw
        var cleanupTask = (Task)
            typeof(RealtimeTranscriptionController)
                .GetMethod("CleanupAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(controller, null)!;
        await cleanupTask;

        Assert.Equal(StreamingState.Idle, controller.State);
    }

    private static void InvokeHandleRealtimeTranscriptionEvent(
        RealtimeTranscriptionController controller,
        RealtimeTranscriptionUpdate update
    )
    {
        var method = typeof(RealtimeTranscriptionController).GetMethod(
            "HandleRealtimeTranscriptionEvent",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.NotNull(method);
        method!.Invoke(controller, new object[] { update });
    }

    private static T GetPrivateField<T>(object target, string fieldName)
    {
        var field = target
            .GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (T)field!.GetValue(target)!;
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
