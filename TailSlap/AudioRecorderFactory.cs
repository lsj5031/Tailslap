namespace TailSlap;

public sealed class AudioRecorderFactory : IAudioRecorderFactory
{
    public AudioRecorder Create(int preferredMicrophoneIndex = -1)
    {
        return new AudioRecorder(preferredMicrophoneIndex);
    }
}
