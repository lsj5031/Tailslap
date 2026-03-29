using System;
using System.Threading;
using System.Threading.Tasks;

namespace TailSlap;

public interface IRealtimeTranscriber : IDisposable
{
    bool IsConnected { get; }

    event Action<RealtimeTranscriptionUpdate>? OnTranscription;
    event Action<string>? OnError;
    event Action? OnConnected;
    event Action? OnDisconnected;
    event Action? OnConnectionLost;

    Task ConnectAsync(CancellationToken ct = default);
    Task SendAudioChunkAsync(byte[] pcm16Data, CancellationToken ct = default);
    Task SendAudioChunkAsync(ArraySegment<byte> pcm16Data, CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);
}
