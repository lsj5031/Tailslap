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

    [Fact]
    public void ProcessServerTextMessage_FreeTextError_RaisesGenericActionableMessage()
    {
        using var transcriber = new RealtimeTranscriber("ws://localhost:18000/stream");
        string? raisedError = null;
        transcriber.OnError += error => raisedError = error;

        var method = typeof(RealtimeTranscriber).GetMethod(
            "ProcessServerTextMessage",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
        );
        Assert.NotNull(method);
        method!.Invoke(transcriber, new object[] { """{"error":"secret server diagnostic"}""" });

        Assert.Equal(
            "The transcription server reported an error. Check the endpoint and model settings, then try again.",
            raisedError
        );
        Assert.DoesNotContain("secret server diagnostic", raisedError);
    }
}
