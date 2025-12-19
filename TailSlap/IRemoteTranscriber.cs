using System.Threading;
using System.Threading.Tasks;

public interface IRemoteTranscriber
{
    Task<string> TestConnectionAsync(CancellationToken ct = default);
    Task<string> TranscribeAudioAsync(string audioFilePath, CancellationToken ct = default);
}
