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

    private const int BUFFER_COUNT = 2;
    private const int BUFFER_SIZE = 32768; // 32KB per buffer
    private const int VAD_BUFFER_MS = 50; // Analyze RMS every 50ms for VAD
    private SafeWaveInHandle? _hWaveIn;
    private byte[][] _buffers;
    private GCHandle[] _bufferHandles;
    private WAVEHDR[] _waveHeaders;
    private MemoryStream _recordedData;
    private bool _isRecording;
    private bool _disposed;
    private int _preferredDeviceIndex = -1;

    public bool IsRecording => _isRecording;

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
            $"AudioRecorder.RecordAsync: outputPath={outputPath}, maxDurationMs={maxDurationMs}, enableVAD={enableVAD}"
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

            // Track silence for VAD
            int consecutiveSilentBuffers = 0;
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
                        ref consecutiveSilentBuffers,
                        stopwatch,
                        stats,
                        isFinalDrain: false
                    );

                    if (enableVAD && consecutiveSilentBuffers * VAD_BUFFER_MS >= silenceThresholdMs)
                    {
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
                ref consecutiveSilentBuffers,
                stopwatch,
                stats,
                isFinalDrain: true
            );

            stats.BytesRecorded = (int)_recordedData.Length;
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
        ref int consecutiveSilentBuffers,
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
                if (
                    (_waveHeaders[i].dwFlags & 0x00000001) != 0
                    || (_waveHeaders[i].dwBytesRecorded > 0 && isFinalDrain)
                )
                {
                    if (_waveHeaders[i].dwBytesRecorded > 0)
                    {
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
                            // Threshold of ~500 RMS for silence detection (adjust as needed)
                            if (rms < 500)
                            {
                                consecutiveSilentBuffers++;
                                // We don't stop here, we just track. The main loop checks the threshold.
                            }
                            else
                            {
                                consecutiveSilentBuffers = 0;
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

    public void Dispose()
    {
        if (_disposed)
            return;
        Stop();
        _recordedData?.Dispose();
        _disposed = true;
    }
}
