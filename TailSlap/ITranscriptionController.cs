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
}
