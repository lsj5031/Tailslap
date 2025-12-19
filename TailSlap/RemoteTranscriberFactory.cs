using System;
using System.Net.Http;

public sealed class RemoteTranscriberFactory : IRemoteTranscriberFactory
{
    private readonly IHttpClientFactory _httpClientFactory;

    public RemoteTranscriberFactory(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory =
            httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    }

    public IRemoteTranscriber Create(TranscriberConfig config)
    {
        return new RemoteTranscriber(config, _httpClientFactory);
    }
}
