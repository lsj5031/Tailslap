namespace TailSlap;

public interface IAudioRecorderFactory
{
    AudioRecorder Create(int preferredMicrophoneIndex = -1);
}
