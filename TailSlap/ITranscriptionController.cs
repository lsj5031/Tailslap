using System;
using System.Threading.Tasks;

namespace TailSlap;

public interface ITranscriptionController
{
    bool IsTranscribing { get; }
    bool IsRecording { get; }

    Task<bool> TriggerTranscribeAsync();
    void StopRecording();

    event Action? OnStarted;
    event Action? OnCompleted;

    /// <summary>Fired with the current RMS audio level during recording.</summary>
    event Action<float>? OnRmsLevel;
}
