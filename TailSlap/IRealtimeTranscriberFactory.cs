namespace TailSlap;

public interface IRealtimeTranscriberFactory
{
    RealtimeTranscriber Create(string webSocketUrl);
    RealtimeTranscriber Create(TranscriberConfig config);
}
