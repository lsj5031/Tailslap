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
            mockAudioRecorderFactory.Object
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
            mockAudioRecorderFactory.Object
        );

        SetPrivateField(controller, "_streamingState", StreamingState.Stopping);
        SetPrivateField(controller, "_allowRealtimeInterimWhileStopping", true);

        await InvokeProcessTranscriptionAsync(controller, "hello!", false, "item-1");

        Assert.Equal("hello!", GetPrivateField<string>(controller, "_realtimeTranscriptionText"));
        Assert.Equal("item-1", GetPrivateField<string?>(controller, "_currentRealtimeItemId"));
        Assert.Equal(6, GetPrivateField<int>(controller, "_lastTypedLength"));
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
