namespace TailSlap;

public sealed class RealtimeTranscriberFactory : IRealtimeTranscriberFactory
{
    public IRealtimeTranscriber Create(string webSocketUrl)
    {
        return new RealtimeTranscriber(webSocketUrl);
    }

    public IRealtimeTranscriber Create(TranscriberConfig config)
    {
        if (string.Equals(config.RealtimeProvider, "openai", StringComparison.OrdinalIgnoreCase))
        {
            return new OpenAIRealtimeTranscriber(config);
        }

        return new RealtimeTranscriber(
            config.WebSocketUrl,
            connectionTimeoutSeconds: config.WebSocketConnectionTimeoutSeconds,
            receiveTimeoutSeconds: config.WebSocketReceiveTimeoutSeconds,
            sendTimeoutSeconds: config.WebSocketSendTimeoutSeconds,
            heartbeatIntervalSeconds: config.WebSocketHeartbeatIntervalSeconds,
            heartbeatTimeoutSeconds: config.WebSocketHeartbeatTimeoutSeconds
        );
    }
}
