namespace TailSlap;

public sealed class RealtimeTranscriberFactory : IRealtimeTranscriberFactory
{
    public RealtimeTranscriber Create(string webSocketUrl)
    {
        return new RealtimeTranscriber(webSocketUrl);
    }
}
