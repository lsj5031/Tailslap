namespace TailSlap;

public interface IRealtimeTranscriberFactory
{
    RealtimeTranscriber Create(string webSocketUrl);
}
