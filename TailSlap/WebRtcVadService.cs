using System;
using WebRtcVadSharp;

public enum VadSensitivity
{
    Low = 0, // OperatingMode.HighQuality - Least aggressive, fewer false positives
    Medium = 1, // OperatingMode.LowBitrate - Balanced
    High = 2, // OperatingMode.Aggressive - More sensitive
    VeryHigh = 3, // OperatingMode.VeryAggressive - Most sensitive, may have false positives
}

public sealed class WebRtcVadService : IDisposable
{
    private WebRtcVad? _vad;
    private bool _disposed;
    private readonly object _lock = new();

    // Reused under _lock: a fresh frame per 20ms slice would allocate ~50
    // arrays per second of recording for no benefit.
    private readonly byte[] _frame20Ms = new byte[640]; // 20ms at 16kHz, 16-bit mono
    private readonly byte[] _frame10Ms = new byte[320]; // 10ms at 16kHz, 16-bit mono

    public WebRtcVadService(VadSensitivity sensitivity = VadSensitivity.High)
    {
        _vad = new WebRtcVad
        {
            OperatingMode = sensitivity switch
            {
                VadSensitivity.Low => OperatingMode.HighQuality,
                VadSensitivity.Medium => OperatingMode.LowBitrate,
                VadSensitivity.High => OperatingMode.Aggressive,
                VadSensitivity.VeryHigh => OperatingMode.VeryAggressive,
                _ => OperatingMode.Aggressive,
            },
        };
    }

    public void SetSensitivity(VadSensitivity sensitivity)
    {
        lock (_lock)
        {
            if (_vad != null)
            {
                _vad.OperatingMode = sensitivity switch
                {
                    VadSensitivity.Low => OperatingMode.HighQuality,
                    VadSensitivity.Medium => OperatingMode.LowBitrate,
                    VadSensitivity.High => OperatingMode.Aggressive,
                    VadSensitivity.VeryHigh => OperatingMode.VeryAggressive,
                    _ => OperatingMode.Aggressive,
                };
            }
        }
    }

    public bool HasSpeech(byte[] audioData)
    {
        if (_disposed || _vad == null || audioData.Length == 0)
            return false;

        lock (_lock)
        {
            if (_vad == null)
                return false;

            try
            {
                // WebRTC VAD requires specific frame sizes for 16kHz: 10ms (160 samples/320 bytes),
                // 20ms (320 samples/640 bytes), or 30ms (480 samples/960 bytes)
                // We'll process in 20ms chunks (640 bytes) and return true if ANY chunk has speech
                int frameSize = _frame20Ms.Length;

                int frames = audioData.Length / frameSize;
                if (frames == 0)
                {
                    // Buffer too small - try to process what we have if it's at least 10ms
                    if (audioData.Length >= _frame10Ms.Length)
                    {
                        Array.Copy(audioData, _frame10Ms, _frame10Ms.Length);
                        return _vad.HasSpeech(_frame10Ms, SampleRate.Is16kHz, FrameLength.Is10ms);
                    }
                    return false;
                }

                // Check each 20ms frame - return true if any frame contains speech
                for (int i = 0; i < frames; i++)
                {
                    Array.Copy(audioData, i * frameSize, _frame20Ms, 0, frameSize);

                    if (_vad.HasSpeech(_frame20Ms, SampleRate.Is16kHz, FrameLength.Is20ms))
                    {
                        return true;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"WebRtcVadService.HasSpeech: Error - {ex.Message}");
                return false;
            }
        }
    }

    public (bool hasSpeech, int speechFrames, int totalFrames) AnalyzeDetailed(byte[] audioData)
    {
        if (_disposed || _vad == null || audioData.Length == 0)
            return (false, 0, 0);

        lock (_lock)
        {
            if (_vad == null)
                return (false, 0, 0);

            try
            {
                int frameSize = _frame20Ms.Length;
                int frames = audioData.Length / frameSize;
                if (frames == 0)
                    return (false, 0, 0);

                int speechFrames = 0;
                for (int i = 0; i < frames; i++)
                {
                    Array.Copy(audioData, i * frameSize, _frame20Ms, 0, frameSize);

                    if (_vad.HasSpeech(_frame20Ms, SampleRate.Is16kHz, FrameLength.Is20ms))
                    {
                        speechFrames++;
                    }
                }

                return (speechFrames > 0, speechFrames, frames);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"WebRtcVadService.AnalyzeDetailed: Error - {ex.Message}");
                return (false, 0, 0);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        lock (_lock)
        {
            _vad?.Dispose();
            _vad = null;
        }
        _disposed = true;
    }
}
