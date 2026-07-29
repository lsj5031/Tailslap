using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
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

    [Fact]
    public async Task RefineAsync_ValidChatResponse_ParsesTrimmedContent()
    {
        var handler = new SequencedHandler(
            JsonResponse("""{"choices":[{"message":{"content":"  polished text  "}}]}""")
        );
        var refiner = CreateRefiner(handler);

        var result = await refiner.RefineAsync("rough text");

        Assert.Equal("polished text", result);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task RefineAsync_ServerError_RetriesThenSucceeds()
    {
        var handler = new SequencedHandler(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("busy"),
            },
            JsonResponse("""{"choices":[{"message":{"content":"recovered text"}}]}""")
        );
        var refiner = CreateRefiner(handler);

        var result = await refiner.RefineAsync("rough text");

        Assert.Equal("recovered text", result);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task RefineAsync_BadRequest_FailsWithoutExposingResponseBody()
    {
        const string privateBody = "private provider diagnostics";
        var handler = new SequencedHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(privateBody),
            },
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(privateBody),
            }
        );
        var refiner = CreateRefiner(handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            refiner.RefineAsync("rough text")
        );

        Assert.Contains("Invalid request", error.Message);
        Assert.DoesNotContain(privateBody, error.Message);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task RefineAsync_SuspiciouslyShortOutput_UsesRecoveryResponse()
    {
        const string input =
            "This is a deliberately long dictated paragraph containing enough substantive detail to require a complete rewritten answer.";
        const string recovered =
            "This deliberately long dictated paragraph contains enough substantive detail to require a complete rewritten answer.";
        var handler = new SequencedHandler(
            JsonResponse("""{"choices":[{"message":{"content":"Too short"}}]}"""),
            JsonResponse(
                """{"choices":[{"message":{"content":"This deliberately long dictated paragraph contains enough substantive detail to require a complete rewritten answer."}}]}"""
            )
        );
        var refiner = CreateRefiner(handler);

        var result = await refiner.RefineAsync(input);

        Assert.Equal(recovered, result);
        Assert.Equal(2, handler.RequestCount);
        Assert.Contains("previous response was too short", handler.RequestBodies[1]);
    }

    private static TextRefiner CreateRefiner(HttpMessageHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(handler, disposeHandler: false));
        return new TextRefiner(
            new LlmConfig
            {
                Enabled = true,
                BaseUrl = "http://unit.test/v1",
                Model = "test-model",
                Temperature = 0.7,
                MaxTokens = 1000,
            },
            factory.Object
        );
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class SequencedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public int RequestCount { get; private set; }
        public List<string> RequestBodies { get; } = new();

        public SequencedHandler(params HttpResponseMessage[] responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            RequestCount++;
            RequestBodies.Add(
                request.Content == null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken)
            );
            return _responses.Dequeue();
        }
    }
}
