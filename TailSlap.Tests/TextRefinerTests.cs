using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Xunit;

public class TextRefinerTests
{
    [Fact]
    public void TextRefiner_CreatesInstanceWithValidConfig()
    {
        // Arrange
        var cfg = new LlmConfig
        {
            Enabled = true,
            BaseUrl = "http://localhost:11434/v1",
            Model = "llama2",
            Temperature = 0.7,
            MaxTokens = 1000,
        };

        var mockFactory = new Mock<IHttpClientFactory>();

        // Act
        var refiner = new TextRefiner(cfg, mockFactory.Object);

        // Assert
        Assert.NotNull(refiner);
    }

    [Fact]
    public void TextRefiner_ThrowsWhenHttpClientFactoryIsNull()
    {
        // Arrange
        var cfg = new LlmConfig
        {
            Enabled = true,
            BaseUrl = "http://localhost:11434/v1",
            Model = "llama2",
            Temperature = 0.7,
        };

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new TextRefiner(cfg, null!));
    }

    [Fact]
    public async Task RefineAsync_DisabledLlm_ThrowsInvalidOperationException()
    {
        // Arrange
        var cfg = new LlmConfig
        {
            Enabled = false,
            BaseUrl = "http://localhost:11434/v1",
            Model = "llama2",
            Temperature = 0.7,
        };

        var mockFactory = new Mock<IHttpClientFactory>();
        var refiner = new TextRefiner(cfg, mockFactory.Object);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            refiner.RefineAsync("text")
        );
        Assert.Contains("disabled", ex.Message.ToLower());
    }

    [Fact]
    public async Task RefineAsync_EmptyText_ThrowsArgumentException()
    {
        // Arrange
        var cfg = new LlmConfig
        {
            Enabled = true,
            BaseUrl = "http://localhost:11434/v1",
            Model = "llama2",
            Temperature = 0.7,
        };

        var mockFactory = new Mock<IHttpClientFactory>();
        var refiner = new TextRefiner(cfg, mockFactory.Object);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => refiner.RefineAsync(""));
        Assert.Contains("empty", ex.Message.ToLower());
    }

    [Fact]
    public async Task RefineAsync_NullText_ThrowsArgumentException()
    {
        // Arrange
        var cfg = new LlmConfig
        {
            Enabled = true,
            BaseUrl = "http://localhost:11434/v1",
            Model = "llama2",
            Temperature = 0.7,
        };

        var mockFactory = new Mock<IHttpClientFactory>();
        var refiner = new TextRefiner(cfg, mockFactory.Object);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => refiner.RefineAsync(null!));
    }

    [Fact]
    public async Task RefineAsync_Whitespace_ThrowsArgumentException()
    {
        // Arrange
        var cfg = new LlmConfig
        {
            Enabled = true,
            BaseUrl = "http://localhost:11434/v1",
            Model = "llama2",
            Temperature = 0.7,
        };

        var mockFactory = new Mock<IHttpClientFactory>();
        var refiner = new TextRefiner(cfg, mockFactory.Object);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => refiner.RefineAsync("   "));
    }
}
