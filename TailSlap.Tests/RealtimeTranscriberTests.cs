using Xunit;

public sealed class RealtimeTranscriberTests
{
    [Fact]
    public async Task SendAudioChunkAsync_BeforeConnect_DoesNotThrow()
    {
        using var transcriber = new RealtimeTranscriber("ws://localhost:18000/stream");

        await transcriber.SendAudioChunkAsync(new byte[] { 0, 0 });
    }

    [Fact]
    public async Task StopAsync_WhenNotConnected_DoesNotThrow()
    {
        using var transcriber = new RealtimeTranscriber("ws://localhost:18000/stream");

        await transcriber.StopAsync();
    }
}
