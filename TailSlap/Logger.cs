using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

public static class Logger
{
    private static string LogPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TailSlap",
            "app.log"
        );

    private const int MaxQueueSize = 10000; // Prevent unbounded memory growth
    private static readonly ConcurrentQueue<string> LogQueue = new();
    private static readonly SemaphoreSlim WriterSignal = new(0);
    private static readonly Task WriterTask;
    private static volatile bool _shuttingDown = false;
    private static volatile int _droppedCount = 0;

    static Logger()
    {
        WriterTask = BackgroundWriterLoop();
    }

    public static void Log(string message)
    {
        try
        {
            // Backpressure: if queue is too large, drop oldest messages
            while (LogQueue.Count >= MaxQueueSize)
            {
                if (LogQueue.TryDequeue(out _))
                {
                    Interlocked.Increment(ref _droppedCount);
                }
                else
                {
                    break; // Queue became empty, stop trying
                }
            }

            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}";
            LogQueue.Enqueue(line);
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
                    // Wait for signal with timeout to periodically flush
                    await WriterSignal.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch { }

                // Process queued items
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);

                    if (LogQueue.Count > 0)
                    {
                        using var stream = new FileStream(
                            LogPath,
                            FileMode.Append,
                            FileAccess.Write,
                            FileShare.ReadWrite
                        );
                        using var writer = new StreamWriter(stream);

                        // Log if messages were dropped due to backpressure
                        int dropped = Interlocked.Exchange(ref _droppedCount, 0);
                        if (dropped > 0)
                        {
                            writer.WriteLine(
                                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - [WARNING] {dropped} log messages dropped due to queue overflow"
                            );
                        }

                        int itemsWritten = 0;
                        while (LogQueue.TryDequeue(out var line) && itemsWritten < 100)
                        {
                            writer.WriteLine(line);
                            itemsWritten++;
                        }
                        writer.Flush();
                    }
                }
                catch
                {
                    // Ignore I/O failures; loop continues
                }
            }

            // Final flush on shutdown
            while (LogQueue.TryDequeue(out var line))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                    File.AppendAllText(LogPath, line + "\n");
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// Optionally call this to wait until all queued logs are written (useful on shutdown).
    /// </summary>
    public static void Flush()
    {
        try
        {
            // Wait a bit for the writer to process remaining items
            int attempts = 0;
            while (LogQueue.Count > 0 && attempts < 10)
            {
                Thread.Sleep(50);
                attempts++;
            }
        }
        catch { }
    }

    /// <summary>
    /// Call this on application shutdown to gracefully stop the writer thread.
    /// </summary>
    public static void Shutdown()
    {
        try
        {
            _shuttingDown = true;
            WriterSignal.Release();
            // Give the writer thread a moment to finish
            WriterTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch { }
    }
}

public sealed class LoggerServiceAdapter : ILoggerService
{
    public void Log(string message) => Logger.Log(message);
    public void Flush() => Logger.Flush();
    public void Shutdown() => Logger.Shutdown();
}
