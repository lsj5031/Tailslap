namespace TailSlap;

public interface IRealtimeTranscriberFactory
{
    IRealtimeTranscriber Create(string webSocketUrl);
    IRealtimeTranscriber Create(TranscriberConfig config);
}
