using System;
using System.Threading.Tasks;

namespace TailSlap;

public interface IRealtimeTranscriptionController
{
    StreamingState State { get; }
    bool IsStreaming { get; }

    Task TriggerStreamingAsync();
    Task StartAsync();
    Task StopAsync();

    event Action? OnStarted;
    event Action? OnStopped;
    event Action<string, bool>? OnTranscription;

    /// <summary>Fired with the current RMS audio level during streaming.</summary>
    event Action<float>? OnRmsLevel;
}
