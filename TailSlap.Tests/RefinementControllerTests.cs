using System;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Xunit;

public class RefinementControllerTests
{
    private Mock<IConfigService> CreateMockConfigService(bool llmEnabled = true)
    {
        var mockConfig = new Mock<IConfigService>();
        var config = new AppConfig
        {
            Llm = new LlmConfig
            {
                Enabled = llmEnabled,
                BaseUrl = "http://localhost:11434/v1",
                Model = "llama2",
                Temperature = 0.7,
            },
            AutoPaste = true,
            UseClipboardFallback = true,
        };
        mockConfig.Setup(c => c.CreateValidatedCopy()).Returns(config);
        return mockConfig;
    }

    [Fact]
    public void RefinementController_CreatesInstanceWithValidDependencies()
    {
        // Arrange
        var mockConfig = CreateMockConfigService();
        var mockClip = new Mock<IClipboardService>();
        var mockRefinerFactory = new Mock<ITextRefinerFactory>();
        var mockHistory = new Mock<IHistoryService>();

        // Act
        var controller = new RefinementController(
            mockConfig.Object,
            mockClip.Object,
            mockRefinerFactory.Object,
            mockHistory.Object
        );

        // Assert
        Assert.NotNull(controller);
        Assert.False(controller.IsRefining);
    }

    [Fact]
    public void RefinementController_ThrowsWhenConfigIsNull()
    {
        // Arrange
        var mockClip = new Mock<IClipboardService>();
        var mockRefinerFactory = new Mock<ITextRefinerFactory>();
        var mockHistory = new Mock<IHistoryService>();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new RefinementController(null!, mockClip.Object, mockRefinerFactory.Object, mockHistory.Object)
        );
    }

    [Fact]
    public async Task TriggerRefineAsync_WhenLlmDisabled_ReturnsFalse()
    {
        // Arrange
        var mockConfig = CreateMockConfigService(llmEnabled: false);
        var mockClip = new Mock<IClipboardService>();
        var mockRefinerFactory = new Mock<ITextRefinerFactory>();
        var mockHistory = new Mock<IHistoryService>();

        var controller = new RefinementController(
            mockConfig.Object,
            mockClip.Object,
            mockRefinerFactory.Object,
            mockHistory.Object
        );

        // Act
        var result = await controller.TriggerRefineAsync();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task TriggerRefineAsync_WhenNoTextSelected_ReturnsFalse()
    {
        // Arrange
        var mockConfig = CreateMockConfigService();
        var mockClip = new Mock<IClipboardService>();
        mockClip.Setup(c => c.CaptureSelectionOrClipboardAsync(It.IsAny<bool>()))
            .ReturnsAsync(string.Empty);
        var mockRefinerFactory = new Mock<ITextRefinerFactory>();
        var mockHistory = new Mock<IHistoryService>();

        var controller = new RefinementController(
            mockConfig.Object,
            mockClip.Object,
            mockRefinerFactory.Object,
            mockHistory.Object
        );

        // Act
        var result = await controller.TriggerRefineAsync();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task TriggerRefineAsync_FiresOnStartedAndOnCompletedEvents()
    {
        // Arrange
        var mockConfig = CreateMockConfigService();
        var mockClip = new Mock<IClipboardService>();
        mockClip.Setup(c => c.CaptureSelectionOrClipboardAsync(It.IsAny<bool>()))
            .ReturnsAsync("test text");
        mockClip.Setup(c => c.SetText(It.IsAny<string>())).Returns(true);
        mockClip.Setup(c => c.PasteAsync()).ReturnsAsync(true);

        var mockRefiner = new Mock<ITextRefiner>();
        mockRefiner.Setup(r => r.RefineAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("refined text");

        var mockRefinerFactory = new Mock<ITextRefinerFactory>();
        mockRefinerFactory.Setup(f => f.Create(It.IsAny<LlmConfig>())).Returns(mockRefiner.Object);

        var mockHistory = new Mock<IHistoryService>();

        var controller = new RefinementController(
            mockConfig.Object,
            mockClip.Object,
            mockRefinerFactory.Object,
            mockHistory.Object
        );

        bool onStartedFired = false;
        bool onCompletedFired = false;
        controller.OnStarted += () => onStartedFired = true;
        controller.OnCompleted += () => onCompletedFired = true;

        // Act
        await controller.TriggerRefineAsync();

        // Assert
        Assert.True(onStartedFired);
        Assert.True(onCompletedFired);
    }

    [Fact]
    public async Task TriggerRefineAsync_WhenSuccessful_AppendsToHistory()
    {
        // Arrange
        var mockConfig = CreateMockConfigService();
        var mockClip = new Mock<IClipboardService>();
        mockClip.Setup(c => c.CaptureSelectionOrClipboardAsync(It.IsAny<bool>()))
            .ReturnsAsync("test text");
        mockClip.Setup(c => c.SetText(It.IsAny<string>())).Returns(true);
        mockClip.Setup(c => c.PasteAsync()).ReturnsAsync(true);

        var mockRefiner = new Mock<ITextRefiner>();
        mockRefiner.Setup(r => r.RefineAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("refined text");

        var mockRefinerFactory = new Mock<ITextRefinerFactory>();
        mockRefinerFactory.Setup(f => f.Create(It.IsAny<LlmConfig>())).Returns(mockRefiner.Object);

        var mockHistory = new Mock<IHistoryService>();

        var controller = new RefinementController(
            mockConfig.Object,
            mockClip.Object,
            mockRefinerFactory.Object,
            mockHistory.Object
        );

        // Act
        await controller.TriggerRefineAsync();

        // Assert
        mockHistory.Verify(h => h.Append("test text", "refined text", "llama2"), Times.Once);
    }

    [Fact]
    public void CancelRefine_CancelsCurrentOperation()
    {
        // Arrange
        var mockConfig = CreateMockConfigService();
        var mockClip = new Mock<IClipboardService>();
        var mockRefinerFactory = new Mock<ITextRefinerFactory>();
        var mockHistory = new Mock<IHistoryService>();

        var controller = new RefinementController(
            mockConfig.Object,
            mockClip.Object,
            mockRefinerFactory.Object,
            mockHistory.Object
        );

        // Act - should not throw even if not refining
        controller.CancelRefine();

        // Assert - no exception means success
        Assert.False(controller.IsRefining);
    }
}
