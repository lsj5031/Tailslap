namespace TailSlap;

public sealed class RealtimeTranscriberFactory : IRealtimeTranscriberFactory
{
    public RealtimeTranscriber Create(string webSocketUrl)
    {
        return new RealtimeTranscriber(webSocketUrl);
    }

    public RealtimeTranscriber Create(TranscriberConfig config)
    {
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
