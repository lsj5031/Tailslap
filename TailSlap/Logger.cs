using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

public sealed class LogEntry
{
    public string Ts { get; set; } = "";
    public string Level { get; set; } = "info";
    public string Source { get; set; } = "";
    public string Msg { get; set; } = "";
    public string? Err { get; set; }
}

public static class Logger
{
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TailSlap",
        "logs"
    );

    private static string LogPath => Path.Combine(LogDirectory, "app.jsonl");

    private const int MaxQueueSize = 10000;
    private const long MaxFileSizeBytes = 5 * 1024 * 1024; // 5 MB
    private const int MaxRotatedFiles = 5;
    private const int BatchSize = 100;

    private static readonly ConcurrentQueue<string> LogQueue = new();
    private static readonly SemaphoreSlim WriterSignal = new(0);
    private static readonly Task WriterTask;
    private static volatile bool _shuttingDown = false;
    private static volatile int _droppedCount = 0;
    private static int _queueSize = 0;

    public static bool VerboseEnabled { get; set; } = false;

    static Logger()
    {
        WriterTask = BackgroundWriterLoop();
    }

    public static void Log(string message, [CallerMemberName] string source = "")
    {
        Enqueue("info", message, null, source);
    }

    public static void LogWarning(string message, [CallerMemberName] string source = "")
    {
        Enqueue("warn", message, null, source);
    }

    public static void Error(
        string message,
        Exception? ex = null,
        [CallerMemberName] string source = ""
    )
    {
        Enqueue("error", message, ex != null ? $"{ex.GetType().Name}: {ex.Message}" : null, source);
    }

    public static void Debug(string message, [CallerMemberName] string source = "")
    {
        if (VerboseEnabled)
            Enqueue("debug", message, null, source);
    }

    public static void LogVerbose(string message, [CallerMemberName] string source = "")
    {
        if (VerboseEnabled)
            Enqueue("debug", message, null, source);
    }

    private static void Enqueue(string level, string message, string? err, string source)
    {
        try
        {
            while (Interlocked.CompareExchange(ref _queueSize, 0, 0) >= MaxQueueSize)
            {
                if (LogQueue.TryDequeue(out _))
                {
                    Interlocked.Decrement(ref _queueSize);
                    Interlocked.Increment(ref _droppedCount);
                }
                else
                {
                    break;
                }
            }

            var entry = new LogEntry
            {
                Ts = DateTime.UtcNow.ToString("o"),
                Level = level,
                Source = source,
                Msg = message,
                Err = err,
            };
            var json = JsonSerializer.Serialize(entry, TailSlapJsonContext.Default.LogEntry);
            LogQueue.Enqueue(json);
            Interlocked.Increment(ref _queueSize);
            WriterSignal.Release();
        }
        catch { }
    }

    private static async Task BackgroundWriterLoop()
    {
        try
        {
            while (!_shuttingDown)
            {
                try
                {
                    await WriterSignal.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch { }

                try
                {
                    Directory.CreateDirectory(LogDirectory);

                    if (Interlocked.CompareExchange(ref _queueSize, 0, 0) > 0)
                    {
                        RotateIfNeeded();

                        using var stream = new FileStream(
                            LogPath,
                            FileMode.Append,
                            FileAccess.Write,
                            FileShare.ReadWrite
                        );
                        using var writer = new StreamWriter(stream);

                        int dropped = Interlocked.Exchange(ref _droppedCount, 0);
                        if (dropped > 0)
                        {
                            var warnEntry = new LogEntry
                            {
                                Ts = DateTime.UtcNow.ToString("o"),
                                Level = "warn",
                                Source = "Logger",
                                Msg = $"{dropped} log messages dropped due to queue overflow",
                            };
                            writer.WriteLine(
                                JsonSerializer.Serialize(
                                    warnEntry,
                                    TailSlapJsonContext.Default.LogEntry
                                )
                            );
                        }

                        int itemsWritten = 0;
                        while (LogQueue.TryDequeue(out var line) && itemsWritten < BatchSize)
                        {
                            Interlocked.Decrement(ref _queueSize);
                            writer.WriteLine(line);
                            itemsWritten++;
                        }
                        writer.Flush();
                    }
                }
                catch { }
            }

            // Final flush on shutdown
            while (LogQueue.TryDequeue(out var line))
            {
                Interlocked.Decrement(ref _queueSize);
                try
                {
                    Directory.CreateDirectory(LogDirectory);
                    File.AppendAllText(LogPath, line + "\n");
                }
                catch { }
            }
        }
        catch { }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            if (!File.Exists(LogPath))
                return;

            var info = new FileInfo(LogPath);
            if (info.Length < MaxFileSizeBytes)
                return;

            // Delete oldest rotated file
            var oldest = Path.Combine(LogDirectory, $"app.{MaxRotatedFiles - 1}.jsonl");
            if (File.Exists(oldest))
                File.Delete(oldest);

            // Shift rotated files up: app.(n-1).jsonl -> app.n.jsonl
            for (int i = MaxRotatedFiles - 2; i >= 1; i--)
            {
                var src = Path.Combine(LogDirectory, $"app.{i}.jsonl");
                var dst = Path.Combine(LogDirectory, $"app.{i + 1}.jsonl");
                if (File.Exists(src))
                    File.Move(src, dst);
            }

            // Move current -> app.1.jsonl
            File.Move(LogPath, Path.Combine(LogDirectory, "app.1.jsonl"));
        }
        catch { }
    }

    public static void Flush()
    {
        try
        {
            int attempts = 0;
            while (Interlocked.CompareExchange(ref _queueSize, 0, 0) > 0 && attempts < 10)
            {
                Thread.Sleep(50);
                attempts++;
            }
        }
        catch { }
    }

    public static void Shutdown()
    {
        try
        {
            _shuttingDown = true;
            WriterSignal.Release();
            WriterTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch { }
    }
}

public sealed class LoggerServiceAdapter : ILoggerService
{
    public void Log(string message, string source = "") => Logger.Log(message, source);

    public void LogWarning(string message, string source = "") =>
        Logger.LogWarning(message, source);

    public void Error(string message, Exception? ex = null, string source = "") =>
        Logger.Error(message, ex, source);

    public void Debug(string message, string source = "") => Logger.Debug(message, source);

    public void LogVerbose(string message, string source = "") =>
        Logger.LogVerbose(message, source);

    public void Flush() => Logger.Flush();

    public void Shutdown() => Logger.Shutdown();
}
