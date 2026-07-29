using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Xunit;

public class HistoryServiceTests
{
    [Fact]
    public void Append_ValidInputs_RoundTripsEncryptedRefinement()
    {
        var baseDirectory = CreateTempDirectory();

        try
        {
            var service = new HistoryService(baseDirectory);

            service.Append("original text", "refined text", "gpt-4o");

            var entry = Assert.Single(service.ReadAll());
            Assert.Equal("gpt-4o", entry.Model);
            Assert.Equal("original text", entry.Original);
            Assert.Equal("refined text", entry.Refined);
            Assert.True(File.Exists(Path.Combine(baseDirectory, "history.jsonl.encrypted")));
        }
        finally
        {
            DeleteTempDirectory(baseDirectory);
        }
    }

    [Fact]
    public void AppendTranscription_ValidInputs_RoundTripsEncryptedTranscription()
    {
        var baseDirectory = CreateTempDirectory();

        try
        {
            var service = new HistoryService(baseDirectory);

            service.AppendTranscription("transcribed text", 5000);

            var entry = Assert.Single(service.ReadAllTranscriptions());
            Assert.Equal("transcribed text", entry.Text);
            Assert.Equal(5000, entry.RecordingDurationMs);
            Assert.True(
                File.Exists(
                    Path.Combine(baseDirectory, "transcription-history.jsonl.encrypted")
                )
            );
        }
        finally
        {
            DeleteTempDirectory(baseDirectory);
        }
    }

    [Fact]
    public void Append_EmptyInputs_DoesNotCreateHistoryFiles()
    {
        var baseDirectory = CreateTempDirectory();

        try
        {
            var service = new HistoryService(baseDirectory);

            service.Append("", "", "gpt-4o");
            service.AppendTranscription("", 0);

            Assert.False(File.Exists(Path.Combine(baseDirectory, "history.jsonl.encrypted")));
            Assert.False(
                File.Exists(
                    Path.Combine(baseDirectory, "transcription-history.jsonl.encrypted")
                )
            );
        }
        finally
        {
            DeleteTempDirectory(baseDirectory);
        }
    }

    [Fact]
    public void Append_MoreThanMaximumEntries_TrimsToLatestEntries()
    {
        var baseDirectory = CreateTempDirectory();

        try
        {
            var service = new HistoryService(baseDirectory);

            for (var index = 0; index < 61; index++)
            {
                service.Append($"original {index}", $"refined {index}", "gpt-4o");
            }

            var history = service.ReadAll();
            Assert.InRange(history.Count, 1, 50);
            Assert.Equal("original 11", history[0].Original);
            Assert.Equal("refined 11", history[0].Refined);
            Assert.Equal("original 60", history[^1].Original);
            Assert.Equal("refined 60", history[^1].Refined);
        }
        finally
        {
            DeleteTempDirectory(baseDirectory);
        }
    }

    [Fact]
    public void ClearAll_RemovesBothHistoryFiles()
    {
        var baseDirectory = CreateTempDirectory();

        try
        {
            var service = new HistoryService(baseDirectory);
            var refinementPath = Path.Combine(baseDirectory, "history.jsonl.encrypted");
            var transcriptionPath = Path.Combine(
                baseDirectory,
                "transcription-history.jsonl.encrypted"
            );
            service.Append("original text", "refined text", "gpt-4o");
            service.AppendTranscription("transcribed text", 5000);

            service.ClearAll();

            Assert.False(File.Exists(refinementPath));
            Assert.False(File.Exists(transcriptionPath));
        }
        finally
        {
            DeleteTempDirectory(baseDirectory);
        }
    }

    [Fact]
    public async Task AppendTranscription_ConcurrentAppends_PersistsEveryEntry()
    {
        var baseDirectory = CreateTempDirectory();

        try
        {
            var service = new HistoryService(baseDirectory);
            var appends = new Task[20];
            for (var index = 0; index < appends.Length; index++)
            {
                var capturedIndex = index;
                appends[index] = Task.Run(
                    () => service.AppendTranscription($"transcription {capturedIndex}", capturedIndex)
                );
            }

            await Task.WhenAll(appends);

            var history = service.ReadAllTranscriptions();
            Assert.Equal(20, history.Count);
            Assert.Equal(
                Enumerable.Range(0, 20),
                history.Select(entry => entry.RecordingDurationMs).OrderBy(value => value)
            );
        }
        finally
        {
            DeleteTempDirectory(baseDirectory);
        }
    }

    [Fact]
    public void ReadAll_MissingDirectory_ReturnsEmptyHistories()
    {
        var baseDirectory = Path.Combine(
            Path.GetTempPath(),
            "TailSlap.Tests",
            Guid.NewGuid().ToString("N")
        );

        try
        {
            var service = new HistoryService(baseDirectory);

            Assert.Empty(service.ReadAll());
            Assert.Empty(service.ReadAllTranscriptions());
            Assert.False(Directory.Exists(baseDirectory));
        }
        finally
        {
            DeleteTempDirectory(baseDirectory);
        }
    }

    [Fact]
    public void ReadRawJsonEntries_ParsesLegacyIndentedEntries()
    {
        var method = typeof(HistoryService).GetMethod(
            "ReadRawJsonEntries",
            BindingFlags.Static | BindingFlags.NonPublic
        );
        Assert.NotNull(method);

        const string legacyJson = """
            {
              "timestamp":"2026-03-30T00:00:00+13:00",
              "textCiphertext":"abc123",
              "recordingDurationMs":1234
            }
            {
              "timestamp":"2026-03-30T00:01:00+13:00",
              "textCiphertext":"def456",
              "recordingDurationMs":5678
            }
            """;

        using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(legacyJson)));

        var entries = Assert.IsType<List<string>>(method!.Invoke(null, new object[] { reader }));
        Assert.Equal(2, entries.Count);
        Assert.Contains("\"recordingDurationMs\":1234", entries[0]);
        Assert.Contains("\"recordingDurationMs\":5678", entries[1]);
    }

    [Fact]
    public void ReadRawJsonEntries_ParsesSingleLineJsonlEntries()
    {
        var method = typeof(HistoryService).GetMethod(
            "ReadRawJsonEntries",
            BindingFlags.Static | BindingFlags.NonPublic
        );
        Assert.NotNull(method);

        const string jsonl =
            "{\"timestamp\":\"2026-03-30T00:00:00+13:00\",\"textCiphertext\":\"abc123\",\"recordingDurationMs\":1234}\n"
            + "{\"timestamp\":\"2026-03-30T00:01:00+13:00\",\"textCiphertext\":\"def456\",\"recordingDurationMs\":5678}\n";

        using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(jsonl)));

        var entries = Assert.IsType<List<string>>(method!.Invoke(null, new object[] { reader }));
        Assert.Equal(2, entries.Count);
        Assert.DoesNotContain('\n', entries[0]);
        Assert.DoesNotContain('\n', entries[1]);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "TailSlap.Tests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
}
