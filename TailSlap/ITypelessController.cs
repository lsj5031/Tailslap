namespace TailSlap;

/// <summary>
/// Controller for push-to-talk transcription mode.
/// State machine: Idle → Recording → Processing → Idle
/// </summary>
public interface ITypelessController
{
    /// <summary>Whether the controller is currently recording audio.</summary>
    bool IsRecording { get; }

    /// <summary>Whether the controller is currently processing (transcribing) audio.</summary>
    bool IsProcessing { get; }

    /// <summary>
    /// Called when the push-to-talk hotkey is pressed.
    /// If Idle and transcriber is enabled, starts recording.
    /// If already Recording, ignores (auto-repeat suppression).
    /// If Processing, shows notification and ignores.
    /// </summary>
    Task HandleKeyDownAsync();

    /// <summary>
    /// Called when the push-to-talk hotkey is released.
    /// If Recording, stops recording and begins transcription.
    /// Discards recordings shorter than 500ms.
    /// </summary>
    Task HandleKeyUpAsync();

    /// <summary>Fired when recording starts (key-down accepted).</summary>
    event Action? OnStarted;

    /// <summary>Fired when recording ends and transcription begins.</summary>
    event Action? OnProcessingStarted;

    /// <summary>Fired when the full cycle completes (back to Idle).</summary>
    event Action? OnCompleted;

    /// <summary>Fired with the current RMS audio level during recording.</summary>
    event Action<float>? OnRmsLevel;
}
