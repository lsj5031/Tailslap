using System;
using System.Collections.Generic;
using System.Reflection;
using TailSlap;
using Xunit;

public class OpenAIRealtimeTranscriberTests
{
    [Fact]
    public void IsConnected_InitiallyFalse()
    {
        var config = new TranscriberConfig
        {
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o-transcribe",
            RealtimeProvider = "openai",
        };
        using var transcriber = new OpenAIRealtimeTranscriber(config);
        Assert.False(transcriber.IsConnected);
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var config = new TranscriberConfig
        {
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o-transcribe",
            RealtimeProvider = "openai",
        };
        var transcriber = new OpenAIRealtimeTranscriber(config);
        transcriber.Dispose();
        transcriber.Dispose(); // Should not throw
    }

    [Fact]
    public async Task ConnectAsync_ThrowsWhenDisposed()
    {
        var config = new TranscriberConfig
        {
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o-transcribe",
            RealtimeProvider = "openai",
        };
        var transcriber = new OpenAIRealtimeTranscriber(config);
        transcriber.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => transcriber.ConnectAsync());
    }

    [Fact]
    public void OnTranscription_EventCanBeSubscribed()
    {
        var config = new TranscriberConfig
        {
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o-transcribe",
            RealtimeProvider = "openai",
        };
        using var transcriber = new OpenAIRealtimeTranscriber(config);

        RealtimeTranscriptionUpdate? receivedUpdate = null;
        transcriber.OnTranscription += update => receivedUpdate = update;

        var eventField = typeof(OpenAIRealtimeTranscriber).GetEvent("OnTranscription");
        Assert.NotNull(eventField);
        Assert.Null(receivedUpdate);
    }

    [Fact]
    public void Config_OpenAIProvider_SetsRealtimeProvider()
    {
        var config = new TranscriberConfig
        {
            RealtimeProvider = "openai",
            BaseUrl = "https://api.openai.com/v1",
        };
        Assert.Equal("openai", config.RealtimeProvider);
    }

    [Fact]
    public void Config_DefaultProvider_IsCustom()
    {
        var config = new TranscriberConfig();
        Assert.Equal("custom", config.RealtimeProvider);
    }

    [Fact]
    public void Config_Clone_PreservesRealtimeProvider()
    {
        var config = new TranscriberConfig
        {
            RealtimeProvider = "openai",
            BaseUrl = "https://api.openai.com/v1",
        };
        var clone = config.Clone();
        Assert.Equal("openai", clone.RealtimeProvider);
    }

    [Fact]
    public void Config_WebSocketUrl_OpenAIProvider_ReturnsRealtimeEndpoint()
    {
        var config = new TranscriberConfig
        {
            RealtimeProvider = "openai",
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o-transcribe",
        };
        var wsUrl = config.WebSocketUrl;
        Assert.Contains("wss://", wsUrl);
        Assert.Contains("/v1/realtime", wsUrl);
        Assert.Contains("intent=transcription", wsUrl);
    }

    [Fact]
    public void Config_WebSocketUrl_CustomProvider_ReturnsStreamEndpoint()
    {
        var config = new TranscriberConfig
        {
            RealtimeProvider = "custom",
            BaseUrl = "http://localhost:18000/v1",
        };
        var wsUrl = config.WebSocketUrl;
        Assert.Contains("ws://", wsUrl);
        Assert.Contains("/v1/audio/transcriptions/stream", wsUrl);
    }

    [Fact]
    public void Config_WebSocketUrl_OpenAI_WithLocalhost_UsesLocalRealtimeEndpoint()
    {
        var config = new TranscriberConfig
        {
            RealtimeProvider = "openai",
            BaseUrl = "http://localhost:18000/v1",
            Model = "gpt-4o-transcribe",
        };
        var wsUrl = config.WebSocketUrl;
        Assert.Equal("ws://localhost:18000/v1/realtime?intent=transcription", wsUrl);
    }

    [Fact]
    public void ProcessServerEvent_DeltaUpdate_UsesCommittedOrderingMetadata()
    {
        var transcriber = new OpenAIRealtimeTranscriber(
            new TranscriberConfig
            {
                RealtimeProvider = "openai",
                BaseUrl = "http://localhost:18000/v1",
                Model = "gpt-4o-transcribe",
            }
        );

        var updates = new List<RealtimeTranscriptionUpdate>();
        transcriber.OnTranscription += update => updates.Add(update);

        InvokeServerEvent(
            transcriber,
            """
            {"type":"input_audio_buffer.committed","item_id":"item-2","previous_item_id":"item-1"}
            """
        );
        InvokeServerEvent(
            transcriber,
            """
            {"type":"conversation.item.input_audio_transcription.delta","item_id":"item-2","delta":"hello"}
            """
        );

        var update = Assert.Single(updates);
        Assert.Equal("hello", update.Text);
        Assert.False(update.IsFinal);
        Assert.Equal("item-2", update.ItemId);
        Assert.Equal("item-1", update.PreviousItemId);
    }

    [Fact]
    public void ProcessServerEvent_DeltaUpdates_AreAccumulatedPerItem()
    {
        var transcriber = new OpenAIRealtimeTranscriber(
            new TranscriberConfig
            {
                RealtimeProvider = "openai",
                BaseUrl = "http://localhost:18000/v1",
                Model = "gpt-4o-transcribe",
            }
        );

        var updates = new List<RealtimeTranscriptionUpdate>();
        transcriber.OnTranscription += update => updates.Add(update);

        InvokeServerEvent(
            transcriber,
            """
            {"type":"conversation.item.input_audio_transcription.delta","item_id":"item-1","delta":"hello "}
            """
        );
        InvokeServerEvent(
            transcriber,
            """
            {"type":"conversation.item.input_audio_transcription.delta","item_id":"item-1","delta":"world"}
            """
        );

        Assert.Equal(2, updates.Count);
        Assert.Equal("hello ", updates[0].Text);
        Assert.Equal("hello world", updates[1].Text);
    }

    [Fact]
    public void ProcessServerEvent_TranscriptTextEvents_AreSupported()
    {
        var transcriber = new OpenAIRealtimeTranscriber(
            new TranscriberConfig
            {
                RealtimeProvider = "openai",
                BaseUrl = "http://localhost:18000/v1",
                Model = "gpt-4o-transcribe",
            }
        );

        var updates = new List<RealtimeTranscriptionUpdate>();
        transcriber.OnTranscription += update => updates.Add(update);

        InvokeServerEvent(
            transcriber,
            """
            {"type":"transcript.text.delta","item_id":"item-1","delta":"hello "}
            """
        );
        InvokeServerEvent(
            transcriber,
            """
            {"type":"transcript.text.done","item_id":"item-1","text":"hello world"}
            """
        );

        Assert.Equal(2, updates.Count);
        Assert.Equal("hello ", updates[0].Text);
        Assert.False(updates[0].IsFinal);
        Assert.Equal("hello world", updates[1].Text);
        Assert.True(updates[1].IsFinal);
    }

    private static void InvokeServerEvent(OpenAIRealtimeTranscriber transcriber, string json)
    {
        var method = typeof(OpenAIRealtimeTranscriber).GetMethod(
            "ProcessServerEvent",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.NotNull(method);
        method!.Invoke(transcriber, new object[] { json });
    }
}
