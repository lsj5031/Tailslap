using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public sealed class RecordingStats
{
    public int DurationMs { get; set; }
    public int BytesRecorded { get; set; }
    public bool SilenceDetected { get; set; }
}

public sealed class AudioRecorder : IDisposable
{
    // WinMM Constants
    private const int WAVE_MAPPER = -1;
    private const int WAVE_FORMAT_PCM = 1;
    private const uint CALLBACK_NULL = 0;
    private const int MM_WIM_OPEN = 0x3BE;
    private const int MM_WIM_CLOSE = 0x3BF;
    private const int MM_WIM_DATA = 0x3C0;
    private const uint WHDR_DONE = 0x00000001; // Buffer is done
    private const uint WHDR_PREPARED = 0x00000002;
    private const uint WHDR_INQUEUE = 0x00000010;

    // WebRTC VAD for smarter speech detection
    private WebRtcVadService? _webRtcVad;
    private bool _useWebRtcVad = true; // Enable by default

    // WinMM P/Invoke
    [DllImport("winmm.dll")]
    private static extern int waveInGetNumDevs();

    [DllImport("winmm.dll")]
    private static extern int waveInOpen(
        out SafeWaveInHandle phwi,
        int uDeviceID,
        ref WAVEFORMATEX pwfx,
        IntPtr dwCallback,
        IntPtr dwInstance,
        uint fdwOpen
    );

    [DllImport("winmm.dll")]
    private static extern int waveInStart(SafeWaveInHandle hwi);

    [DllImport("winmm.dll")]
    private static extern int waveInStop(SafeWaveInHandle hwi);

    [DllImport("winmm.dll")]
    private static extern int waveInClose(IntPtr hwi); // Keep internal for SafeHandle

    [DllImport("winmm.dll")]
    private static extern int waveInAddBuffer(SafeWaveInHandle hwi, ref WAVEHDR pwh, int cbwh);

    [DllImport("winmm.dll")]
    private static extern int waveInPrepareHeader(SafeWaveInHandle hwi, ref WAVEHDR pwh, int cbwh);

    [DllImport("winmm.dll")]
    private static extern int waveInUnprepareHeader(
        SafeWaveInHandle hwi,
        ref WAVEHDR pwh,
        int cbwh
    );

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    public static int GetDeviceCount() => waveInGetNumDevs();

    private const int BUFFER_COUNT = 8;
    private const int BUFFER_SIZE = 6400; // 200ms buffers for smoother VAD averaging
    private const int VAD_BUFFER_MS = 100; // Check every 100ms
    private const int BYTES_PER_MS = 32; // 16kHz * 2 bytes * 1 channel / 1000ms = 32 bytes/ms
    private SafeWaveInHandle? _hWaveIn;
    private byte[][] _buffers;
    private GCHandle[] _bufferHandles;
    private WAVEHDR[] _waveHeaders;
    private MemoryStream _recordedData;
    private bool _isRecording;
    private bool _disposed;
    private int _preferredDeviceIndex = -1;
    private int _stopInvoked;
    private bool _waveInResetIssued;
    private DateTime _lastStreamingRecoveryAttempt = DateTime.MinValue;

    public bool IsRecording => _isRecording;

    public event Action<ArraySegment<byte>>? OnAudioChunk;
    public event Action? OnSilenceDetected;

    /// <summary>
    /// Fires with the current RMS audio level (0-32768 range for 16-bit audio)
    /// whenever a buffer is processed during recording or streaming.
    /// </summary>
    public event Action<float>? OnRmsLevel;

    // External speech detection (e.g., from server transcription)
    private volatile bool _externalSpeechDetected = false;
    private DateTime _lastExternalSpeechTime = DateTime.MinValue;

    /// <summary>
    /// Notify the recorder that speech was detected externally (e.g., server sent transcription).
    /// This resets the silence timer for auto-stop purposes.
    /// </summary>
    public void NotifySpeechDetected()
    {
        _externalSpeechDetected = true;
        _lastExternalSpeechTime = DateTime.Now;
    }

    public AudioRecorder()
    {
        _buffers = new byte[BUFFER_COUNT][];
        _bufferHandles = new GCHandle[BUFFER_COUNT];
        _waveHeaders = new WAVEHDR[BUFFER_COUNT];
        _recordedData = new MemoryStream();
        InitializeWebRtcVad();
    }

    public AudioRecorder(int preferredDeviceIndex)
        : this()
    {
        _preferredDeviceIndex = preferredDeviceIndex;
    }

    private void InitializeWebRtcVad()
    {
        try
        {
            _webRtcVad = new WebRtcVadService(VadSensitivity.High);
            Logger.Log("AudioRecorder: WebRTC VAD initialized (sensitivity=High)");
        }
        catch (Exception ex)
        {
            Logger.Log(
                $"AudioRecorder: WebRTC VAD initialization failed, falling back to RMS - {ex.Message}"
            );
            _webRtcVad = null;
            _useWebRtcVad = false;
        }
    }

    public void SetWebRtcVadSensitivity(VadSensitivity sensitivity)
    {
        if (_webRtcVad != null)
        {
            _webRtcVad.SetSensitivity(sensitivity);
            Logger.Log($"AudioRecorder: WebRTC VAD sensitivity set to {sensitivity}");
        }
    }

    public void SetUseWebRtcVad(bool enabled)
    {
        _useWebRtcVad = enabled && _webRtcVad != null;
        Logger.Log($"AudioRecorder: WebRTC VAD {(_useWebRtcVad ? "enabled" : "disabled")}");
    }

    public static List<string> GetAvailableMicrophones()
    {
        var devices = new List<string>();
        int numDevices = waveInGetNumDevs();
        for (int i = 0; i < numDevices; i++)
        {
            devices.Add($"Device {i}");
        }
        return devices;
    }

    public async Task<RecordingStats> RecordAsync(
        string outputPath,
        int maxDurationMs = 0,
        CancellationToken ct = default,
        bool enableVAD = false,
        int silenceThresholdMs = 1000
    )
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AudioRecorder));
        _stopInvoked = 0;
        _waveInResetIssued = false;
        Logger.Log(
            $"AudioRecorder.RecordAsync: outputPath={outputPath}, maxDurationMs={maxDurationMs}, enableVAD={enableVAD}, silenceThresholdMs={silenceThresholdMs}"
        );
        int numDevices = waveInGetNumDevs();
        Logger.Log($"AudioRecorder: Found {numDevices} audio input devices");
        if (numDevices == 0)
            throw new InvalidOperationException("No audio input device found.");

        var stats = new RecordingStats { SilenceDetected = false };
        bool useMaxDuration = maxDurationMs > 0;

        try
        {
            // Audio format: 16-bit mono at 16kHz
            var wfx = new WAVEFORMATEX
            {
                wFormatTag = WAVE_FORMAT_PCM,
                nChannels = 1,
                nSamplesPerSec = 16000,
                nAvgBytesPerSec = 32000,
                nBlockAlign = 2,
                wBitsPerSample = 16,
                cbSize = 0,
            };

            // Open recording device (use preferred device if specified)
            int deviceId = _preferredDeviceIndex >= 0 ? _preferredDeviceIndex : WAVE_MAPPER;
            Logger.Log($"AudioRecorder: Opening device {deviceId}");
            int result = waveInOpen(
                out _hWaveIn,
                deviceId,
                ref wfx,
                IntPtr.Zero,
                IntPtr.Zero,
                CALLBACK_NULL
            );
            if (result != 0)
            {
                Logger.Log($"AudioRecorder: waveInOpen FAILED with error {result}");
                _hWaveIn?.SetHandleAsInvalid();
                throw new InvalidOperationException(
                    $"Failed to open waveIn device (deviceId={deviceId}, numDevices={numDevices}): error {result}"
                );
            }
            Logger.Log(
                $"AudioRecorder: waveInOpen succeeded, handle=0x{_hWaveIn.DangerousGetHandle():X}"
            );

            // Allocate and prepare buffers
            for (int i = 0; i < BUFFER_COUNT; i++)
            {
                _buffers[i] = new byte[BUFFER_SIZE];
                _bufferHandles[i] = GCHandle.Alloc(_buffers[i], GCHandleType.Pinned);

                _waveHeaders[i] = new WAVEHDR
                {
                    lpData = _bufferHandles[i].AddrOfPinnedObject(),
                    dwBufferLength = (uint)BUFFER_SIZE,
                    dwBytesRecorded = 0,
                    dwUser = IntPtr.Zero,
                    dwFlags = 0,
                    dwLoops = 0,
                };

                result = waveInPrepareHeader(
                    _hWaveIn,
                    ref _waveHeaders[i],
                    Marshal.SizeOf(typeof(WAVEHDR))
                );
                if (result != 0)
                    throw new InvalidOperationException(
                        $"Failed to prepare wave header {i}: error {result}"
                    );

                result = waveInAddBuffer(
                    _hWaveIn,
                    ref _waveHeaders[i],
                    Marshal.SizeOf(typeof(WAVEHDR))
                );
                if (result != 0)
                    throw new InvalidOperationException(
                        $"Failed to add buffer {i}: error {result}"
                    );
            }

            _isRecording = true;
            _recordedData.SetLength(0);

            // Start recording
            Logger.Log("AudioRecorder: Starting recording");
            result = waveInStart(_hWaveIn);
            if (result != 0)
            {
                Logger.Log($"AudioRecorder: waveInStart FAILED with error {result}");
                throw new InvalidOperationException($"Failed to start recording: error {result}");
            }
            Logger.Log("AudioRecorder: Recording started successfully");

            // Track silence for VAD (in milliseconds)
            int consecutiveSilenceMs = 0;
            bool hasDetectedSpeech = false;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            bool stopRequestedBySilence = false;

            try
            {
                // Record until cancellation or max duration (if specified)
                while (
                    _isRecording
                    && (!useMaxDuration || stopwatch.ElapsedMilliseconds < maxDurationMs)
                )
                {
                    ct.ThrowIfCancellationRequested();

                    ProcessBuffers(
                        enableVAD,
                        ref consecutiveSilenceMs,
                        ref hasDetectedSpeech,
                        stopwatch,
                        stats,
                        isFinalDrain: false
                    );

                    if (enableVAD && consecutiveSilenceMs >= silenceThresholdMs)
                    {
                        // Check if server detected speech recently (within last 2 seconds)
                        // This prevents cutting off if local VAD is too strict but server hears something
                        if (
                            _externalSpeechDetected
                            && (DateTime.Now - _lastExternalSpeechTime).TotalMilliseconds < 2000
                        )
                        {
                            // Logger.Log($"AudioRecorder: Local silence ({consecutiveSilenceMs}ms) but external speech detected recently. Extending recording.");
                            consecutiveSilenceMs = 0; // Reset silence counter
                            hasDetectedSpeech = true; // FORCE local state to acknowledge speech happened
                        }
                        else
                        {
                            Logger.Log(
                                $"AudioRecorder: Silence detected ({consecutiveSilenceMs}ms >= {silenceThresholdMs}ms), stopping"
                            );
                            stats.SilenceDetected = true;
                            stopwatch.Stop();
                            stopRequestedBySilence = true;
                            break;
                        }
                    }

                    // If external speech was detected, ensure we consider speech as "started" locally
                    // This prevents the infinite recording bug where local VAD misses the speech start
                    // and thus never starts counting silence.
                    if (_externalSpeechDetected)
                    {
                        hasDetectedSpeech = true;
                    }

                    await Task.Delay(VAD_BUFFER_MS, ct);
                }
            }
            catch (OperationCanceledException)
            {
                Logger.Log("AudioRecorder: Recording cancelled by user");
                stopwatch.Stop();
            }

            // Set duration from stopwatch
            stats.DurationMs = (int)stopwatch.ElapsedMilliseconds;
            Logger.Log($"AudioRecorder: Recording loop ended, duration={stats.DurationMs}ms");

            // Stop recording
            if (_hWaveIn != null && !_hWaveIn.IsInvalid)
            {
                Logger.Log("AudioRecorder: Calling waveInStop");
                int stopResult = waveInStop(_hWaveIn);
                if (stopResult != 0)
                {
                    Logger.Log($"AudioRecorder: waveInStop returned error {stopResult}");
                }
            }

            bool buffersReturned = await WaitForAllBuffersReturnedAsync(
                    timeoutMs: stopRequestedBySilence ? 300 : 200,
                    logOnTimeout: false
                )
                .ConfigureAwait(false);

            if (!buffersReturned)
            {
                Logger.Log(
                    "AudioRecorder: Buffers still queued after waveInStop; calling waveInReset for final drain"
                );
                ResetWaveIn("AudioRecorder");
                await WaitForAllBuffersReturnedAsync(timeoutMs: 300, logOnTimeout: false)
                    .ConfigureAwait(false);
            }

            // Collect any final data from buffers
            ProcessBuffers(
                enableVAD,
                ref consecutiveSilenceMs,
                ref hasDetectedSpeech,
                stopwatch,
                stats,
                isFinalDrain: true
            );

            stats.BytesRecorded = (int)_recordedData.Length;

            // Log sanity check - expected bytes at 16kHz 16-bit mono
            int expectedBytesPerSecond = 16000 * 2 * 1; // 32000 bytes/sec
            int expectedBytes = expectedBytesPerSecond * stats.DurationMs / 1000;
            int actualBytes = stats.BytesRecorded;
            float ratio = actualBytes > 0 ? actualBytes / (float)expectedBytes : 0;
            Logger.Log(
                $"AudioRecorder: Sanity check - expected ~{expectedBytes} bytes for {stats.DurationMs}ms, got {actualBytes} bytes, ratio={ratio:F2}x"
            );

            await FinishRecordingAsync(outputPath, stats);

            return stats;
        }
        catch (Exception ex)
        {
            Logger.Log(
                $"AudioRecorder: EXCEPTION during recording: {ex.GetType().Name}: {ex.Message}"
            );
            throw;
        }
        finally
        {
            Logger.Log("AudioRecorder: Entering finally block, calling Stop()");
            Stop();
        }
    }

    private bool IsAllBuffersReturned()
    {
        for (int i = 0; i < BUFFER_COUNT; i++)
        {
            if ((_waveHeaders[i].dwFlags & WHDR_INQUEUE) != 0)
                return false;
        }
        return true;
    }

    private async Task<bool> WaitForAllBuffersReturnedAsync(int timeoutMs, bool logOnTimeout = true)
    {
        const int pollIntervalMs = 20;
        int waitedMs = 0;

        while (!IsAllBuffersReturned() && waitedMs < timeoutMs)
        {
            await Task.Delay(pollIntervalMs).ConfigureAwait(false);
            waitedMs += pollIntervalMs;
        }

        bool allReturned = IsAllBuffersReturned();
        if (!allReturned && logOnTimeout)
        {
            Logger.Log(
                $"AudioRecorder: Timed out waiting for buffers to return after {timeoutMs}ms"
            );
        }

        return allReturned;
    }

    private void ProcessBuffers(
        bool enableVAD,
        ref int consecutiveSilenceMs,
        ref bool hasDetectedSpeech,
        System.Diagnostics.Stopwatch stopwatch,
        RecordingStats stats,
        bool isFinalDrain = false
    )
    {
        for (int i = 0; i < BUFFER_COUNT; i++)
        {
            try
            {
                // Check if buffer is done (WHDR_DONE = 0x00000001) or if we are force draining and it has bytes
                bool bufferDone = (_waveHeaders[i].dwFlags & 0x00000001) != 0;
                bool hasData = _waveHeaders[i].dwBytesRecorded > 0;

                if (bufferDone || (hasData && isFinalDrain))
                {
                    if (hasData)
                    {
                        // Removed high-frequency buffer ready logging to reduce noise
                        // Logger.Log(
                        //    $"VAD[Record]: Buffer {i} ready, bytes={_waveHeaders[i].dwBytesRecorded}, enableVAD={enableVAD}"
                        // );
                        byte[] data = new byte[_waveHeaders[i].dwBytesRecorded];
                        Marshal.Copy(
                            _waveHeaders[i].lpData,
                            data,
                            0,
                            (int)_waveHeaders[i].dwBytesRecorded
                        );
                        _recordedData.Write(data, 0, data.Length);

                        // Check VAD if enabled (only during active recording, not final drain to avoid cutting off end)
                        // Fire RMS level event for UI visualization
                        if (data.Length >= 2)
                        {
                            float rmsForUi = CalculateRMS(data);
                            try
                            {
                                OnRmsLevel?.Invoke(rmsForUi);
                            }
                            catch { }
                        }

                        if (enableVAD && !isFinalDrain && data.Length >= 2)
                        {
                            bool isSpeech = DetectSpeech(data, out float rms);
                            int bufferDurationMs = data.Length / BYTES_PER_MS;

                            if (!isSpeech)
                            {
                                // Only count silence if we've detected speech first
                                if (hasDetectedSpeech)
                                {
                                    consecutiveSilenceMs += bufferDurationMs;
                                    Logger.Log(
                                        $"VAD[Record]: Silence accumulating: {consecutiveSilenceMs}ms (WebRTC={_useWebRtcVad})"
                                    );
                                }
                                else
                                {
                                    Logger.Log($"VAD[Record]: Silence but no speech yet, ignoring");
                                }
                            }
                            else
                            {
                                if (!hasDetectedSpeech)
                                {
                                    Logger.Log(
                                        $"VAD[Record]: Speech detected (WebRTC={_useWebRtcVad}, rms={rms:F0})"
                                    );
                                }
                                hasDetectedSpeech = true;
                                consecutiveSilenceMs = 0;
                            }
                        }

                        // Re-add buffer for more recording if not final drain
                        if (
                            !isFinalDrain
                            && _isRecording
                            && _hWaveIn != null
                            && !_hWaveIn.IsInvalid
                        )
                        {
                            // Reset for reuse - keep WHDR_PREPARED flag (0x02), only clear WHDR_DONE (0x01)
                            // Buffers are already prepared from initial setup, no need to re-prepare
                            _waveHeaders[i].dwBytesRecorded = 0;
                            _waveHeaders[i].dwFlags &= ~0x01u; // Clear DONE flag, keep PREPARED
                            int addResult = waveInAddBuffer(
                                _hWaveIn,
                                ref _waveHeaders[i],
                                Marshal.SizeOf(typeof(WAVEHDR))
                            );
                            if (addResult != 0)
                            {
                                Logger.Log(
                                    $"AudioRecorder: waveInAddBuffer FAILED for buffer {i}, error={addResult}"
                                );
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Log buffer processing errors but don't crash recording
                System.Diagnostics.Debug.WriteLine($"Error processing buffer {i}: {ex.Message}");
            }
        }
    }

    private async Task<RecordingStats> FinishRecordingAsync(string outputPath, RecordingStats stats)
    {
        // Save to WAV file
        _recordedData.Seek(0, SeekOrigin.Begin);
        var audioData = _recordedData.ToArray();
        await Task.Run(() => SaveAsWav(outputPath, audioData, 16000, 1, 16));
        return stats;
    }

    private float CalculateRMS(byte[] audioBuffer)
    {
        float sum = 0;
        for (int i = 0; i < audioBuffer.Length; i += 2)
        {
            if (i + 1 < audioBuffer.Length)
            {
                short sample = BitConverter.ToInt16(audioBuffer, i);
                sum += sample * sample;
            }
        }
        int sampleCount = audioBuffer.Length / 2;
        return sampleCount > 0 ? (float)Math.Sqrt(sum / sampleCount) : 0;
    }

    private bool DetectSpeech(byte[] audioData, out float rms)
    {
        rms = CalculateRMS(audioData);

        // Use WebRTC VAD if available and enabled
        if (_useWebRtcVad && _webRtcVad != null)
        {
            return _webRtcVad.HasSpeech(audioData);
        }

        // Fallback to RMS-based detection
        return rms > _vadSilenceThreshold;
    }

    private bool DetectSpeechWithHysteresis(byte[] audioData, bool hasDetectedSpeech, out float rms)
    {
        rms = CalculateRMS(audioData);

        // Use WebRTC VAD if available and enabled
        if (_useWebRtcVad && _webRtcVad != null)
        {
            bool webRtcSpeech = _webRtcVad.HasSpeech(audioData);

            // Hybrid approach: use WebRTC for activation, but also consider RMS for sustain
            // This helps catch edge cases where WebRTC might miss soft speech endings
            if (!hasDetectedSpeech)
            {
                // For activation, trust WebRTC VAD
                return webRtcSpeech;
            }
            else
            {
                // For sustain, use either WebRTC OR high RMS (more lenient to avoid cutting off)
                return webRtcSpeech || rms > _vadSustainThreshold;
            }
        }

        // Fallback to RMS-based hysteresis detection
        if (!hasDetectedSpeech)
        {
            return rms > _vadActivationThreshold;
        }
        else
        {
            return rms > _vadSustainThreshold;
        }
    }

    private void Stop()
    {
        if (Interlocked.Exchange(ref _stopInvoked, 1) == 1)
        {
            return;
        }

        Logger.Log("AudioRecorder.Stop: Starting cleanup");
        _isRecording = false;

        if (_hWaveIn != null && !_hWaveIn.IsInvalid)
        {
            if (!_waveInResetIssued)
            {
                Logger.Log("AudioRecorder.Stop: Calling waveInReset");
                ResetWaveIn("AudioRecorder.Stop");
            }

            // Let WinMM return ownership of any in-flight buffers before unpreparing them.
            var waitStart = Environment.TickCount64;
            while (!IsAllBuffersReturned() && Environment.TickCount64 - waitStart < 1000)
            {
                Thread.Sleep(20);
            }

            // Unprepare headers BEFORE closing the device (requires valid handle)
            for (int i = 0; i < BUFFER_COUNT; i++)
            {
                if (_bufferHandles[i].IsAllocated && (_waveHeaders[i].dwFlags & WHDR_PREPARED) != 0)
                {
                    try
                    {
                        int unprepResult = waveInUnprepareHeader(
                            _hWaveIn,
                            ref _waveHeaders[i],
                            Marshal.SizeOf(typeof(WAVEHDR))
                        );
                        if (unprepResult != 0)
                            Logger.Log(
                                $"AudioRecorder.Stop: waveInUnprepareHeader[{i}] returned error {unprepResult}"
                            );
                    }
                    catch (Exception ex)
                    {
                        Logger.Log(
                            $"AudioRecorder.Stop: Exception unpreparing header {i}: {ex.Message}"
                        );
                    }
                }
            }

            Logger.Log("AudioRecorder.Stop: Closing handle");
            _hWaveIn.Dispose();
            _hWaveIn = null;
        }

        // Free buffer handles
        for (int i = 0; i < BUFFER_COUNT; i++)
        {
            if (_bufferHandles[i].IsAllocated)
            {
                try
                {
                    _bufferHandles[i].Free();
                }
                catch (Exception ex)
                {
                    Logger.Log(
                        $"AudioRecorder.Stop: Exception freeing buffer handle {i}: {ex.Message}"
                    );
                }
            }
        }
        Logger.Log("AudioRecorder.Stop: Cleanup complete");
    }

    [DllImport("winmm.dll")]
    private static extern int waveInReset(SafeWaveInHandle hwi);

    private int ResetWaveIn(string caller)
    {
        if (_hWaveIn == null || _hWaveIn.IsInvalid)
        {
            return 0;
        }

        int resetResult = waveInReset(_hWaveIn);
        _waveInResetIssued = true;

        if (resetResult != 0)
        {
            Logger.Log($"{caller}: waveInReset returned error {resetResult}");
        }

        return resetResult;
    }

    private static void SaveAsWav(
        string filePath,
        byte[] audioData,
        int sampleRate,
        int channels,
        int bitsPerSample
    )
    {
        try
        {
            if (audioData == null || audioData.Length == 0)
            {
                throw new InvalidOperationException("No audio data to save");
            }

            using var file = File.Create(filePath);
            using var writer = new BinaryWriter(file, Encoding.ASCII, leaveOpen: false);

            int byteRate = sampleRate * channels * (bitsPerSample / 8);
            short blockAlign = (short)(channels * (bitsPerSample / 8));

            // RIFF header
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + audioData.Length); // File size - 8
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));

            // fmt sub-chunk
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16); // Subchunk1Size (PCM)
            writer.Write((short)1); // AudioFormat (1 = PCM)
            writer.Write((short)channels);
            writer.Write(sampleRate);
            writer.Write(byteRate);
            writer.Write(blockAlign);
            writer.Write((short)bitsPerSample);

            // data sub-chunk
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(audioData.Length);
            writer.Write(audioData);

            writer.Flush();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to save WAV file: {ex.Message}", ex);
        }
    }

    private static WAVEFORMATEX CreatePcm16WaveFormat()
    {
        return new WAVEFORMATEX
        {
            wFormatTag = WAVE_FORMAT_PCM,
            nChannels = 1,
            nSamplesPerSec = 16000,
            nAvgBytesPerSec = 32000,
            nBlockAlign = 2,
            wBitsPerSample = 16,
            cbSize = 0,
        };
    }

    private int GetSelectedDeviceId()
    {
        return _preferredDeviceIndex >= 0 ? _preferredDeviceIndex : WAVE_MAPPER;
    }

    private void OpenStreamingCaptureDevice(string caller)
    {
        int numDevices = waveInGetNumDevs();
        if (numDevices == 0)
        {
            throw new InvalidOperationException("No audio input device found.");
        }

        var wfx = CreatePcm16WaveFormat();
        int deviceId = GetSelectedDeviceId();
        Logger.Log($"{caller}: Opening device {deviceId}");

        int result = waveInOpen(
            out _hWaveIn,
            deviceId,
            ref wfx,
            IntPtr.Zero,
            IntPtr.Zero,
            CALLBACK_NULL
        );
        if (result != 0)
        {
            Logger.Log($"{caller}: waveInOpen FAILED with error {result}");
            _hWaveIn?.SetHandleAsInvalid();
            throw new InvalidOperationException(
                $"Failed to open waveIn device (deviceId={deviceId}, numDevices={numDevices}): error {result}"
            );
        }

        for (int i = 0; i < BUFFER_COUNT; i++)
        {
            _buffers[i] = new byte[BUFFER_SIZE];
            if (_bufferHandles[i].IsAllocated)
            {
                _bufferHandles[i].Free();
            }

            _bufferHandles[i] = GCHandle.Alloc(_buffers[i], GCHandleType.Pinned);

            _waveHeaders[i] = new WAVEHDR
            {
                lpData = _bufferHandles[i].AddrOfPinnedObject(),
                dwBufferLength = (uint)BUFFER_SIZE,
                dwBytesRecorded = 0,
                dwUser = IntPtr.Zero,
                dwFlags = 0,
                dwLoops = 0,
            };

            result = waveInPrepareHeader(
                _hWaveIn,
                ref _waveHeaders[i],
                Marshal.SizeOf(typeof(WAVEHDR))
            );
            if (result != 0)
            {
                throw new InvalidOperationException(
                    $"Failed to prepare wave header {i}: error {result}"
                );
            }

            result = waveInAddBuffer(
                _hWaveIn,
                ref _waveHeaders[i],
                Marshal.SizeOf(typeof(WAVEHDR))
            );
            if (result != 0)
            {
                throw new InvalidOperationException($"Failed to add buffer {i}: error {result}");
            }
        }

        _waveInResetIssued = false;
        _isRecording = true;

        Logger.Log($"{caller}: Starting recording");
        result = waveInStart(_hWaveIn);
        if (result != 0)
        {
            Logger.Log($"{caller}: waveInStart FAILED with error {result}");
            throw new InvalidOperationException($"Failed to start recording: error {result}");
        }

        Logger.Log($"{caller}: Recording started successfully");
    }

    private void ReleaseStreamingCaptureDevice(string caller)
    {
        if (_hWaveIn != null && !_hWaveIn.IsInvalid)
        {
            try
            {
                int stopResult = waveInStop(_hWaveIn);
                if (stopResult != 0)
                {
                    Logger.Log($"{caller}: waveInStop returned error {stopResult}");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"{caller}: Exception stopping waveIn device: {ex.Message}");
            }

            try
            {
                if (!_waveInResetIssued)
                {
                    ResetWaveIn(caller);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"{caller}: Exception resetting waveIn device: {ex.Message}");
            }

            for (int i = 0; i < BUFFER_COUNT; i++)
            {
                if (_bufferHandles[i].IsAllocated && (_waveHeaders[i].dwFlags & WHDR_PREPARED) != 0)
                {
                    try
                    {
                        int unprepResult = waveInUnprepareHeader(
                            _hWaveIn,
                            ref _waveHeaders[i],
                            Marshal.SizeOf(typeof(WAVEHDR))
                        );
                        if (unprepResult != 0)
                        {
                            Logger.Log(
                                $"{caller}: waveInUnprepareHeader[{i}] returned error {unprepResult}"
                            );
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"{caller}: Exception unpreparing header {i}: {ex.Message}");
                    }
                }
            }

            Logger.Log($"{caller}: Closing handle");
            _hWaveIn.Dispose();
            _hWaveIn = null;
        }

        for (int i = 0; i < BUFFER_COUNT; i++)
        {
            if (_bufferHandles[i].IsAllocated)
            {
                try
                {
                    _bufferHandles[i].Free();
                }
                catch (Exception ex)
                {
                    Logger.Log($"{caller}: Exception freeing buffer handle {i}: {ex.Message}");
                }
            }

            _buffers[i] = Array.Empty<byte>();
            _waveHeaders[i] = default;
        }
    }

    private void ReopenStreamingCaptureDevice()
    {
        Logger.Log("AudioRecorder.StreamingRecovery: Reopening capture device");
        ReleaseStreamingCaptureDevice("AudioRecorder.StreamingRecovery.Reopen");
        OpenStreamingCaptureDevice("AudioRecorder.StreamingRecovery.Reopen");
        _lastBufferTime = DateTime.Now;
        Logger.Log("AudioRecorder: Streaming recovery reopened capture device successfully");
    }

    public async Task StartStreamingAsync(
        CancellationToken ct = default,
        bool enableVAD = false,
        int silenceThresholdMs = 1000
    )
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AudioRecorder));

        // Reset debug counters for new session
        _totalBuffersProcessed = 0;
        _totalStreamingBufferCompletions = 0;
        _buffersWithSpeech = 0;
        _buffersWithSilence = 0;
        _lastBufferTime = DateTime.MinValue;
        _lastSpeechTime = DateTime.MinValue;
        _externalSpeechDetected = false;
        _lastExternalSpeechTime = DateTime.MinValue;
        _stopInvoked = 0;
        _waveInResetIssued = false;
        _lastStreamingRecoveryAttempt = DateTime.MinValue;

        int numDevices = waveInGetNumDevs();
        Logger.Log(
            $"AudioRecorder.StartStreamingAsync: Found {numDevices} audio input devices, enableVAD={enableVAD}, silenceThresholdMs={silenceThresholdMs}"
        );
        if (numDevices == 0)
            throw new InvalidOperationException("No audio input device found.");

        int consecutiveSilenceMs = 0;
        bool hasDetectedSpeech = false;

        try
        {
            OpenStreamingCaptureDevice("AudioRecorder.StartStreamingAsync");

            while (_isRecording && !ct.IsCancellationRequested)
            {
                bool silenceDetected = ProcessStreamingBuffers(
                    enableVAD,
                    ref consecutiveSilenceMs,
                    ref hasDetectedSpeech,
                    silenceThresholdMs
                );
                if (silenceDetected)
                {
                    Logger.Log(
                        $"AudioRecorder.StartStreamingAsync: Silence detected ({consecutiveSilenceMs}ms >= {silenceThresholdMs}ms), stopping"
                    );

                    // Fire event but DON'T stop immediately here, let the caller decide or let the event handler handle it.
                    // However, our return value is 'true' meaning silence detected.
                    // We should break loop to stop recording locally.

                    OnSilenceDetected?.Invoke();
                    break;
                }
                await Task.Delay(VAD_BUFFER_MS, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            Logger.Log("AudioRecorder.StartStreamingAsync: Recording cancelled");
        }
        catch (Exception ex)
        {
            Logger.Log(
                $"AudioRecorder.StartStreamingAsync: EXCEPTION - {ex.GetType().Name}: {ex.Message}"
            );
            throw;
        }
        finally
        {
            Logger.Log("AudioRecorder.StartStreamingAsync: Cleanup");
            Stop();
        }
    }

    // Default VAD thresholds (can be overridden via SetVadThresholds)
    private int _vadSilenceThreshold = 120; // Used for standard recording
    private int _vadActivationThreshold = 900; // Used for streaming (start) - needs clear speech above ambient
    private int _vadSustainThreshold = 550; // Used for streaming (continue) - lowered to 550 (very close to noise floor)

    public void SetVadThresholds(
        int silenceThreshold,
        int activationThreshold,
        int sustainThreshold
    )
    {
        _vadSilenceThreshold = silenceThreshold > 0 ? silenceThreshold : 120;
        _vadActivationThreshold = activationThreshold > 0 ? activationThreshold : 900;
        _vadSustainThreshold = sustainThreshold > 0 ? sustainThreshold : 550;
        Logger.Log(
            $"AudioRecorder: VAD thresholds set - silence={_vadSilenceThreshold}, activation={_vadActivationThreshold}, sustain={_vadSustainThreshold}"
        );
    }

    // Debug: track buffer processing stats
    private int _totalBuffersProcessed = 0;
    private int _totalStreamingBufferCompletions = 0;
    private int _buffersWithSpeech = 0;
    private int _buffersWithSilence = 0;
    private DateTime _lastBufferTime = DateTime.MinValue;
    private DateTime _lastSpeechTime = DateTime.MinValue;

    private bool ProcessStreamingBuffers(
        bool enableVAD,
        ref int consecutiveSilenceMs,
        ref bool hasDetectedSpeech,
        int silenceThresholdMs
    )
    {
        int buffersProcessedThisCall = 0;
        int buffersCompletedThisCall = 0;
        var now = DateTime.Now;

        // Wall-clock silence detection: if speech was detected (locally OR via server transcription)
        // and no recent speech, use actual elapsed time instead of buffer-based counting
        bool anySpeechDetected = hasDetectedSpeech || _externalSpeechDetected;
        DateTime lastSpeech =
            _lastSpeechTime > _lastExternalSpeechTime ? _lastSpeechTime : _lastExternalSpeechTime;

        if (enableVAD && anySpeechDetected && lastSpeech != DateTime.MinValue)
        {
            int wallClockSilenceMs = (int)(now - lastSpeech).TotalMilliseconds;

            // If we're relying solely on server VAD (local VAD never activated),
            // use a longer timeout to account for server inference latency (~2s per transcription)
            int effectiveThreshold = silenceThresholdMs;
            if (_externalSpeechDetected && !hasDetectedSpeech)
            {
                // Server VAD only: add extra buffer for inference latency
                effectiveThreshold = silenceThresholdMs + 3000; // 3s extra for server latency
            }

            if (wallClockSilenceMs >= effectiveThreshold)
            {
                Logger.Log(
                    $"VAD[Stream]: *** WALL-CLOCK THRESHOLD REACHED *** {wallClockSilenceMs}ms >= {effectiveThreshold}ms (localVAD={hasDetectedSpeech}, serverVAD={_externalSpeechDetected}). Stats: speech={_buffersWithSpeech} silent={_buffersWithSilence} total={_totalBuffersProcessed}"
                );
                return true;
            }
        }

        for (int i = 0; i < BUFFER_COUNT; i++)
        {
            try
            {
                bool bufferDone = (_waveHeaders[i].dwFlags & WHDR_DONE) != 0;
                bool hasData = _waveHeaders[i].dwBytesRecorded > 0;

                if (bufferDone)
                {
                    buffersCompletedThisCall++;
                    _totalStreamingBufferCompletions++;

                    if (hasData)
                    {
                        buffersProcessedThisCall++;
                        _totalBuffersProcessed++;

                        byte[] data = new byte[_waveHeaders[i].dwBytesRecorded];
                        Marshal.Copy(
                            _waveHeaders[i].lpData,
                            data,
                            0,
                            (int)_waveHeaders[i].dwBytesRecorded
                        );

                        OnAudioChunk?.Invoke(new ArraySegment<byte>(data));

                        // Fire RMS level event for UI visualization
                        if (data.Length >= 2)
                        {
                            float streamRms = CalculateRMS(data);
                            try
                            {
                                OnRmsLevel?.Invoke(streamRms);
                            }
                            catch { }
                        }

                        if (enableVAD && data.Length >= 2)
                        {
                            bool isSpeech = DetectSpeechWithHysteresis(
                                data,
                                hasDetectedSpeech,
                                out float rms
                            );
                            int bufferDurationMs = data.Length / BYTES_PER_MS;

                            // DEBUG: Only log every 20th buffer to reduce noise (1 second of audio)
                            if (_totalBuffersProcessed % 20 == 0)
                            {
                                string speechState = hasDetectedSpeech ? "ACTIVE" : "WAITING";
                                string vadType = _useWebRtcVad ? "WebRTC" : "RMS";
                                Logger.Log(
                                    $"VAD[DEBUG]: buf#{_totalBuffersProcessed} {vadType} speech={isSpeech} RMS={rms:F0} state={speechState} silence={consecutiveSilenceMs}ms"
                                );
                            }

                            if (isSpeech)
                            {
                                if (!hasDetectedSpeech)
                                {
                                    _buffersWithSpeech++;
                                    Logger.Log(
                                        $"VAD[Stream]: *** Speech ACTIVATED *** (WebRTC={_useWebRtcVad}, RMS={rms:F0})"
                                    );
                                }
                                else
                                {
                                    _buffersWithSpeech++;
                                    if (consecutiveSilenceMs > 0)
                                    {
                                        Logger.Log(
                                            $"VAD[Stream]: Speech SUSTAINED (WebRTC={_useWebRtcVad}, RMS={rms:F0}), silence reset from {consecutiveSilenceMs}ms"
                                        );
                                    }
                                }
                                hasDetectedSpeech = true;
                                consecutiveSilenceMs = 0;
                                _lastSpeechTime = now;
                            }
                            else
                            {
                                _buffersWithSilence++;
                                // Only accumulate silence if we have actually started speaking at least once
                                if (hasDetectedSpeech)
                                {
                                    consecutiveSilenceMs += bufferDurationMs;

                                    // Log every 500ms of silence accumulation
                                    if (consecutiveSilenceMs % 500 < bufferDurationMs)
                                    {
                                        Logger.Log(
                                            $"VAD[Stream]: Silence milestone: {consecutiveSilenceMs}ms / {silenceThresholdMs}ms (WebRTC={_useWebRtcVad})"
                                        );
                                    }

                                    if (consecutiveSilenceMs >= silenceThresholdMs)
                                    {
                                        Logger.Log(
                                            $"VAD[Stream]: *** THRESHOLD REACHED *** Stopping. Stats: speech={_buffersWithSpeech} silent={_buffersWithSilence} total={_totalBuffersProcessed}"
                                        );
                                        return true;
                                    }
                                }
                            }
                        }
                    }

                    if (_isRecording && _hWaveIn != null && !_hWaveIn.IsInvalid)
                    {
                        // Re-arm even empty completed buffers so the capture queue cannot drain
                        // during startup silence or after a transient device hiccup.
                        _waveHeaders[i].dwBytesRecorded = 0;
                        // Clear only the DONE flag (0x01), keep PREPARED flag (0x02)
                        _waveHeaders[i].dwFlags &= ~0x01u;

                        int addResult = waveInAddBuffer(
                            _hWaveIn,
                            ref _waveHeaders[i],
                            Marshal.SizeOf(typeof(WAVEHDR))
                        );
                        if (addResult != 0)
                        {
                            Logger.Log(
                                $"VAD[ERROR]: waveInAddBuffer failed for buffer {i}, error={addResult}, flags=0x{_waveHeaders[i].dwFlags:X}"
                            );
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"VAD[ERROR]: Exception processing buffer {i}: {ex.Message}");
            }
        }

        // Log if no buffers were ready (potential audio capture issue)
        if (buffersCompletedThisCall == 0 && _totalStreamingBufferCompletions > 0)
        {
            var gap = (now - _lastBufferTime).TotalMilliseconds;
            int expectedBufferCadenceMs = BUFFER_SIZE / BYTES_PER_MS;
            int warningThresholdMs = Math.Max(500, expectedBufferCadenceMs * 2);
            if (gap > warningThresholdMs)
            {
                Logger.Log($"VAD[WARN]: No buffers ready! Gap since last buffer: {gap:F0}ms");

                // Recover quickly in realtime mode. With 200ms capture buffers,
                // waiting multiple seconds before reopening loses too much speech.
                const int recoveryThresholdMs = 900;
                const int recoveryRetryIntervalMs = 1200;
                bool recoveryCooldownElapsed =
                    _lastStreamingRecoveryAttempt == DateTime.MinValue
                    || (now - _lastStreamingRecoveryAttempt).TotalMilliseconds
                        >= recoveryRetryIntervalMs;

                if (gap >= recoveryThresholdMs && recoveryCooldownElapsed)
                {
                    AttemptStreamingBufferRecovery(gap);
                }
            }
        }

        if (buffersCompletedThisCall > 0)
        {
            _lastBufferTime = now;
        }

        return false;
    }

    private void AttemptStreamingBufferRecovery(double gapMs)
    {
        if (!_isRecording || _hWaveIn == null || _hWaveIn.IsInvalid)
        {
            return;
        }

        _lastStreamingRecoveryAttempt = DateTime.Now;
        Logger.Log(
            $"AudioRecorder: Attempting streaming buffer recovery after {gapMs:F0}ms without buffers"
        );

        try
        {
            int stopResult = waveInStop(_hWaveIn);
            if (stopResult != 0)
            {
                Logger.Log(
                    $"AudioRecorder.StreamingRecovery: waveInStop returned error {stopResult}"
                );
            }

            int resetResult = ResetWaveIn("AudioRecorder.StreamingRecovery");
            if (resetResult != 0)
            {
                throw new InvalidOperationException(
                    $"Streaming recovery failed during waveInReset: error {resetResult}"
                );
            }

            var waitStart = Environment.TickCount64;
            while (!IsAllBuffersReturned() && Environment.TickCount64 - waitStart < 250)
            {
                Thread.Sleep(20);
            }

            if (!IsAllBuffersReturned())
            {
                Logger.Log(
                    "AudioRecorder.StreamingRecovery: Timed out waiting for buffers; reopening capture device"
                );
                ReopenStreamingCaptureDevice();
                return;
            }

            for (int i = 0; i < BUFFER_COUNT; i++)
            {
                if ((_waveHeaders[i].dwFlags & WHDR_PREPARED) != 0)
                {
                    int unprepResult = waveInUnprepareHeader(
                        _hWaveIn,
                        ref _waveHeaders[i],
                        Marshal.SizeOf(typeof(WAVEHDR))
                    );
                    if (unprepResult != 0)
                    {
                        throw new InvalidOperationException(
                            $"Streaming recovery failed to unprepare header {i}: error {unprepResult}"
                        );
                    }
                }

                _waveHeaders[i] = new WAVEHDR
                {
                    lpData = _bufferHandles[i].AddrOfPinnedObject(),
                    dwBufferLength = (uint)BUFFER_SIZE,
                    dwBytesRecorded = 0,
                    dwUser = IntPtr.Zero,
                    dwFlags = 0,
                    dwLoops = 0,
                };

                int prepResult = waveInPrepareHeader(
                    _hWaveIn,
                    ref _waveHeaders[i],
                    Marshal.SizeOf(typeof(WAVEHDR))
                );
                if (prepResult != 0)
                {
                    throw new InvalidOperationException(
                        $"Streaming recovery failed to prepare header {i}: error {prepResult}"
                    );
                }

                int addResult = waveInAddBuffer(
                    _hWaveIn,
                    ref _waveHeaders[i],
                    Marshal.SizeOf(typeof(WAVEHDR))
                );
                if (addResult != 0)
                {
                    throw new InvalidOperationException(
                        $"Streaming recovery failed to re-add header {i}: error {addResult}"
                    );
                }
            }

            int startResult = waveInStart(_hWaveIn);
            if (startResult != 0)
            {
                throw new InvalidOperationException(
                    $"Streaming recovery failed to restart capture: error {startResult}"
                );
            }

            _waveInResetIssued = false;
            _lastBufferTime = DateTime.Now;
            Logger.Log("AudioRecorder: Streaming buffer recovery restarted capture successfully");
        }
        catch (Exception ex)
        {
            Logger.Log($"AudioRecorder.StreamingRecovery: Exception - {ex.Message}");
            Logger.Log("AudioRecorder.StreamingRecovery: Falling back to capture device reopen");

            try
            {
                ReopenStreamingCaptureDevice();
            }
            catch (Exception reopenEx)
            {
                Logger.Log($"AudioRecorder.StreamingRecovery: Reopen failed - {reopenEx.Message}");
                throw new InvalidOperationException(
                    "Streaming recovery failed after attempting to reopen the audio device.",
                    reopenEx
                );
            }
        }
    }

    public void StopRecording()
    {
        _isRecording = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Stop();
        _recordedData?.Dispose();
        _webRtcVad?.Dispose();
        _webRtcVad = null;
        _disposed = true;
    }
}
