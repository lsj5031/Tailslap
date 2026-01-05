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

    public bool IsRecording => _isRecording;

    public event Action<ArraySegment<byte>>? OnAudioChunk;
    public event Action? OnSilenceDetected;

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
    }

    public AudioRecorder(int preferredDeviceIndex)
        : this()
    {
        _preferredDeviceIndex = preferredDeviceIndex;
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
                        Logger.Log(
                            $"AudioRecorder: Silence detected ({consecutiveSilenceMs}ms >= {silenceThresholdMs}ms), stopping"
                        );
                        stats.SilenceDetected = true;
                        stopwatch.Stop();
                        stats.DurationMs = (int)stopwatch.ElapsedMilliseconds;
                        stats.BytesRecorded = (int)_recordedData.Length;
                        return await FinishRecordingAsync(outputPath, stats);
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
                waveInStop(_hWaveIn);
            }

            // Wait for buffers to return. We know we have BUFFER_COUNT buffers.
            // In a robust implementation, we might wait on an event, but a short delay loop is often sufficient for simple WinMM wrappers.
            // We'll give it up to 500ms to return all buffers.
            for (int i = 0; i < 10; i++)
            {
                if (IsAllBuffersReturned())
                    break;
                await Task.Delay(50);
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
            // If the buffer is still in the queue (WHDR_INQUEUE = 0x00000010), it hasn't returned
            if ((_waveHeaders[i].dwFlags & 0x00000010) != 0)
                return false;
        }
        return true;
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
                        if (enableVAD && !isFinalDrain && data.Length >= 2)
                        {
                            float rms = CalculateRMS(data);
                            int bufferDurationMs = data.Length / BYTES_PER_MS;
                            if (rms < _vadSilenceThreshold)
                            {
                                // Only count silence if we've detected speech first
                                if (hasDetectedSpeech)
                                {
                                    consecutiveSilenceMs += bufferDurationMs;
                                    Logger.Log(
                                        $"VAD[Record]: Silence accumulating: {consecutiveSilenceMs}ms"
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
                                    Logger.Log($"VAD[Record]: Speech detected, rms={rms:F0}");
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
                            _waveHeaders[i].dwBytesRecorded = 0;
                            _waveHeaders[i].dwFlags = 0; // Reset flags
                            int prepResult = waveInPrepareHeader(
                                _hWaveIn,
                                ref _waveHeaders[i],
                                Marshal.SizeOf(typeof(WAVEHDR))
                            );
                            if (prepResult != 0)
                            {
                                Logger.Log(
                                    $"AudioRecorder: waveInPrepareHeader FAILED for buffer {i}, error={prepResult}"
                                );
                                continue;
                            }
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

    private void Stop()
    {
        Logger.Log("AudioRecorder.Stop: Starting cleanup");
        _isRecording = false;

        if (_hWaveIn != null && !_hWaveIn.IsInvalid)
        {
            Logger.Log("AudioRecorder.Stop: Calling waveInReset");
            int resetResult = waveInReset(_hWaveIn); // Stop and reset position
            if (resetResult != 0)
                Logger.Log($"AudioRecorder.Stop: waveInReset returned error {resetResult}");

            // Unprepare headers BEFORE closing the device (requires valid handle)
            for (int i = 0; i < BUFFER_COUNT; i++)
            {
                if (_bufferHandles[i].IsAllocated)
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
        _buffersWithSpeech = 0;
        _buffersWithSilence = 0;
        _lastBufferTime = DateTime.MinValue;
        _lastSpeechTime = DateTime.MinValue;
        _externalSpeechDetected = false;
        _lastExternalSpeechTime = DateTime.MinValue;

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

            int deviceId = _preferredDeviceIndex >= 0 ? _preferredDeviceIndex : WAVE_MAPPER;
            Logger.Log($"AudioRecorder.StartStreamingAsync: Opening device {deviceId}");
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
                Logger.Log(
                    $"AudioRecorder.StartStreamingAsync: waveInOpen FAILED with error {result}"
                );
                _hWaveIn?.SetHandleAsInvalid();
                throw new InvalidOperationException(
                    $"Failed to open waveIn device (deviceId={deviceId}, numDevices={numDevices}): error {result}"
                );
            }

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

            Logger.Log("AudioRecorder.StartStreamingAsync: Starting recording");
            result = waveInStart(_hWaveIn);
            if (result != 0)
            {
                Logger.Log(
                    $"AudioRecorder.StartStreamingAsync: waveInStart FAILED with error {result}"
                );
                throw new InvalidOperationException($"Failed to start recording: error {result}");
            }
            Logger.Log("AudioRecorder.StartStreamingAsync: Recording started successfully");

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
        var now = DateTime.Now;

        // Wall-clock silence detection: if speech was detected (locally OR via server transcription)
        // and no recent speech, use actual elapsed time instead of buffer-based counting
        bool anySpeechDetected = hasDetectedSpeech || _externalSpeechDetected;
        DateTime lastSpeech =
            _lastSpeechTime > _lastExternalSpeechTime ? _lastSpeechTime : _lastExternalSpeechTime;

        if (enableVAD && anySpeechDetected && lastSpeech != DateTime.MinValue)
        {
            int wallClockSilenceMs = (int)(now - lastSpeech).TotalMilliseconds;
            if (wallClockSilenceMs >= silenceThresholdMs)
            {
                Logger.Log(
                    $"VAD[Stream]: *** WALL-CLOCK THRESHOLD REACHED *** {wallClockSilenceMs}ms >= {silenceThresholdMs}ms (localVAD={hasDetectedSpeech}, serverVAD={_externalSpeechDetected}). Stats: speech={_buffersWithSpeech} silent={_buffersWithSilence} total={_totalBuffersProcessed}"
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

                if (bufferDone && hasData)
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

                    if (enableVAD && data.Length >= 2)
                    {
                        float rms = CalculateRMS(data);
                        int bufferDurationMs = data.Length / BYTES_PER_MS;

                        // DEBUG: Only log every 20th buffer to reduce noise (1 second of audio)
                        if (_totalBuffersProcessed % 20 == 0)
                        {
                            string speechState = hasDetectedSpeech ? "ACTIVE" : "WAITING";
                            int threshold = hasDetectedSpeech
                                ? _vadSustainThreshold
                                : _vadActivationThreshold;
                            Logger.Log(
                                $"VAD[DEBUG]: buf#{_totalBuffersProcessed} RMS={rms:F0} thresh={threshold} state={speechState} silence={consecutiveSilenceMs}ms"
                            );
                        }

                        // Hysteresis Logic:
                        // 1. To START detecting speech, RMS must exceed ACTIVATION threshold (ignoring background noise)
                        // 2. To CONTINUE detecting speech, RMS must exceed SUSTAIN threshold (catching softer speech endings)
                        // 3. Otherwise, accumulate silence

                        bool isSpeech = false;
                        if (!hasDetectedSpeech)
                        {
                            if (rms > _vadActivationThreshold)
                            {
                                isSpeech = true;
                                _buffersWithSpeech++;
                                Logger.Log(
                                    $"VAD[Stream]: *** Speech ACTIVATED *** (RMS={rms:F0} > {_vadActivationThreshold})"
                                );
                            }
                        }
                        else
                        {
                            if (rms > _vadSustainThreshold)
                            {
                                isSpeech = true;
                                _buffersWithSpeech++;
                                // Log when silence was being accumulated
                                if (consecutiveSilenceMs > 0)
                                {
                                    Logger.Log(
                                        $"VAD[Stream]: Speech SUSTAINED (RMS={rms:F0} > {_vadSustainThreshold}), silence reset from {consecutiveSilenceMs}ms"
                                    );
                                }
                            }
                            else
                            {
                                _buffersWithSilence++;
                            }
                        }

                        if (isSpeech)
                        {
                            hasDetectedSpeech = true;
                            consecutiveSilenceMs = 0;
                            _lastSpeechTime = now;
                        }
                        else
                        {
                            // Only accumulate silence if we have actually started speaking at least once
                            if (hasDetectedSpeech)
                            {
                                consecutiveSilenceMs += bufferDurationMs;

                                // Log every 500ms of silence accumulation
                                if (consecutiveSilenceMs % 500 < bufferDurationMs)
                                {
                                    Logger.Log(
                                        $"VAD[Stream]: Silence milestone: {consecutiveSilenceMs}ms / {silenceThresholdMs}ms (speech={_buffersWithSpeech} silent={_buffersWithSilence} total={_totalBuffersProcessed})"
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

                    if (_isRecording && _hWaveIn != null && !_hWaveIn.IsInvalid)
                    {
                        // Reset for reuse - keep WHDR_PREPARED flag, just clear WHDR_DONE
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
        if (buffersProcessedThisCall == 0 && _totalBuffersProcessed > 0)
        {
            var gap = (now - _lastBufferTime).TotalMilliseconds;
            if (gap > 100) // Log if gap > 100ms
            {
                Logger.Log($"VAD[WARN]: No buffers ready! Gap since last buffer: {gap:F0}ms");
            }
        }

        if (buffersProcessedThisCall > 0)
        {
            _lastBufferTime = now;
        }

        return false;
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
        _disposed = true;
    }
}
