using System;
using System.Net.Http;

public sealed class TextRefinerFactory : ITextRefinerFactory
{
    private readonly IHttpClientFactory _httpClientFactory;

    public TextRefinerFactory(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    }

    public ITextRefiner Create(LlmConfig cfg)
    {
        return new TextRefiner(cfg, _httpClientFactory);
    }
}
