using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Moq;
using Xunit;

namespace TailSlap.Tests;

public sealed class RemoteTranscriberTests
{
    [Fact]
    public async Task TranscribeAudioAsync_TopLevelText_ReturnsText()
    {
        using var fixture = new RemoteTranscriberFixture(
            _ => JsonResponse("""{"text":"hello world"}""")
        );

        var result = await fixture.Transcriber.TranscribeAudioAsync(fixture.AudioPath);

        Assert.Equal("hello world", result);
    }

    [Fact]
    public async Task TranscribeAudioAsync_OpenAiChoiceMessage_ReturnsContent()
    {
        using var fixture = new RemoteTranscriberFixture(
            _ => JsonResponse("""{"choices":[{"message":{"content":"choice text"}}]}""")
        );

        var result = await fixture.Transcriber.TranscribeAudioAsync(fixture.AudioPath);

        Assert.Equal("choice text", result);
    }

    [Fact]
    public async Task TranscribeAudioAsync_SendsExpectedMultipartRequest()
    {
        CapturedRequest? captured = null;
        using var fixture = new RemoteTranscriberFixture(
            request =>
            {
                captured = request;
                return JsonResponse("""{"text":"ok"}""");
            },
            apiKey: "secret-token"
        );

        await fixture.Transcriber.TranscribeAudioAsync(fixture.AudioPath);

        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Equal(
            "http://unit.test/v1/audio/transcriptions",
            captured.RequestUri?.AbsoluteUri
        );
        Assert.Equal("Bearer secret-token", captured.Authorization);
        Assert.Contains("name=file", captured.Content);
        Assert.Contains($"filename={Path.GetFileName(fixture.AudioPath)}", captured.Content);
        Assert.Contains("name=model", captured.Content);
        Assert.Contains("test-model", captured.Content);
    }

    [Fact]
    public async Task TranscribeAudioAsync_InvalidJson_ThrowsParseErrorWithFingerprint()
    {
        using var fixture = new RemoteTranscriberFixture(_ => JsonResponse("not-json"));

        var error = await Assert.ThrowsAsync<TranscriberException>(() =>
            fixture.Transcriber.TranscribeAudioAsync(fixture.AudioPath)
        );

        Assert.Equal(TranscriberErrorType.ParseError, error.ErrorType);
        Assert.Contains("sha256=", error.ResponseText);
        Assert.DoesNotContain("not-json", error.ResponseText);
    }

    [Fact]
    public async Task TranscribeAudioAsync_HttpFailure_ThrowsHttpErrorWithoutRawBody()
    {
        using var fixture = new RemoteTranscriberFixture(
            _ =>
                new HttpResponseMessage(HttpStatusCode.BadGateway)
                {
                    Content = new StringContent("private upstream details"),
                }
        );

        var error = await Assert.ThrowsAsync<TranscriberException>(() =>
            fixture.Transcriber.TranscribeAudioAsync(fixture.AudioPath)
        );

        Assert.Equal(TranscriberErrorType.HttpError, error.ErrorType);
        Assert.Equal(502, error.StatusCode);
        Assert.Contains("sha256=", error.ResponseText);
        Assert.DoesNotContain("private upstream details", error.ResponseText);
    }

    [Fact]
    public async Task TranscribeStreamingAsync_SseChunks_YieldsUntilDone()
    {
        using var fixture = new RemoteTranscriberFixture(
            _ => SseResponse("data: hello \n\ndata: world\n\ndata: [DONE]\n\ndata: ignored\n\n")
        );

        var chunks = await CollectAsync(
            fixture.Transcriber.TranscribeStreamingAsync(fixture.AudioPath)
        );

        Assert.Equal(new[] { "hello ", "world" }, chunks);
    }

    [Fact]
    public async Task TranscribeStreamingAsync_DataWithoutSpace_YieldsText()
    {
        using var fixture = new RemoteTranscriberFixture(
            _ => SseResponse("data:first\ndata: second\ndata:[DONE]\n")
        );

        var chunks = await CollectAsync(
            fixture.Transcriber.TranscribeStreamingAsync(fixture.AudioPath)
        );

        Assert.Equal(new[] { "first", "second" }, chunks);
    }

    [Fact]
    public async Task TranscribeStreamingAsync_ErrorEvent_ThrowsFingerprintError()
    {
        using var fixture = new RemoteTranscriberFixture(
            _ => SseResponse("data: [Error: private backend detail]\n\n")
        );

        var error = await Assert.ThrowsAsync<TranscriberException>(async () =>
            await CollectAsync(fixture.Transcriber.TranscribeStreamingAsync(fixture.AudioPath))
        );

        Assert.Equal(TranscriberErrorType.HttpError, error.ErrorType);
        Assert.Contains("sha256=", error.ResponseText);
        Assert.DoesNotContain("private backend detail", error.ResponseText);
    }

    [Fact]
    public async Task TranscribeStreamingAsync_NonStreamingJson_YieldsFullText()
    {
        using var fixture = new RemoteTranscriberFixture(
            _ => JsonResponse("""{"transcription":"fallback text"}""")
        );

        var chunks = await CollectAsync(
            fixture.Transcriber.TranscribeStreamingAsync(fixture.AudioPath)
        );

        Assert.Equal(new[] { "fallback text" }, chunks);
    }

    [Fact]
    public async Task TranscribeAudioAsync_MissingFile_ThrowsBeforeHttpRequest()
    {
        var requests = 0;
        using var fixture = new RemoteTranscriberFixture(
            _ =>
            {
                requests++;
                return JsonResponse("""{"text":"unused"}""");
            }
        );
        var missingPath = fixture.AudioPath + ".missing";

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            fixture.Transcriber.TranscribeAudioAsync(missingPath)
        );

        Assert.Equal(0, requests);
    }

    private static async Task<List<string>> CollectAsync(IAsyncEnumerable<string> source)
    {
        var results = new List<string>();
        await foreach (var item in source)
        {
            results.Add(item);
        }

        return results;
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage SseResponse(string content) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "text/event-stream"),
        };

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri? RequestUri,
        string? Authorization,
        string Content
    );

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<CapturedRequest, HttpResponseMessage> _responseFactory;

        public StubHandler(Func<CapturedRequest, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var content =
                request.Content == null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken);
            var captured = new CapturedRequest(
                request.Method,
                request.RequestUri,
                request.Headers.Authorization?.ToString(),
                content
            );
            return _responseFactory(captured);
        }
    }

    private sealed class RemoteTranscriberFixture : IDisposable
    {
        private readonly StubHandler _handler;

        public string AudioPath { get; }
        public RemoteTranscriber Transcriber { get; }

        public RemoteTranscriberFixture(
            Func<CapturedRequest, HttpResponseMessage> responseFactory,
            string? apiKey = null
        )
        {
            AudioPath = Path.GetTempFileName();
            File.WriteAllBytes(AudioPath, new byte[] { 0, 1, 2, 3 });
            _handler = new StubHandler(responseFactory);
            var factory = new Mock<IHttpClientFactory>();
            factory
                .Setup(f => f.CreateClient(It.IsAny<string>()))
                .Returns(() => new HttpClient(_handler, disposeHandler: false));
            var config = new TranscriberConfig
            {
                Enabled = true,
                BaseUrl = "http://unit.test/v1",
                Model = "test-model",
                TimeoutSeconds = 5,
            };
            if (apiKey != null)
            {
                config.ApiKey = apiKey;
            }

            Transcriber = new RemoteTranscriber(config, factory.Object);
        }

        public void Dispose()
        {
            _handler.Dispose();
            File.Delete(AudioPath);
        }
    }
}
