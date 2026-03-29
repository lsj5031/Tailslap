using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Xunit;

public class HistoryServiceTests
{
    [Fact]
    public void HistoryService_CreatesInstance()
    {
        // Arrange & Act
        var service = new HistoryService();

        // Assert
        Assert.NotNull(service);
    }

    [Fact]
    public void ReadAll_ReturnsValidList()
    {
        // Arrange
        var service = new HistoryService();

        // Act
        var history = service.ReadAll();

        // Assert
        Assert.NotNull(history);
        Assert.IsType<List<(DateTime, string, string, string)>>(history);
    }

    [Fact]
    public void ReadAllTranscriptions_ReturnsValidList()
    {
        // Arrange
        var service = new HistoryService();

        // Act
        var history = service.ReadAllTranscriptions();

        // Assert
        Assert.NotNull(history);
        Assert.IsType<List<(DateTime, string, int)>>(history);
    }

    [Fact]
    public void ClearAll_DoesNotThrow()
    {
        // Arrange
        var service = new HistoryService();

        // Act & Assert - should not throw
        service.ClearAll();
    }

    [Fact]
    public void Append_ValidInputs_DoesNotThrow()
    {
        // Arrange
        var service = new HistoryService();

        // Act & Assert - should not throw
        service.Append("original text", "refined text", "gpt-4o");
    }

    [Fact]
    public void Append_EmptyInputs_DoesNotThrow()
    {
        // Arrange
        var service = new HistoryService();

        // Act & Assert - should not throw
        service.Append("", "", "gpt-4o");
    }

    [Fact]
    public void AppendTranscription_ValidInputs_DoesNotThrow()
    {
        // Arrange
        var service = new HistoryService();

        // Act & Assert - should not throw
        service.AppendTranscription("transcribed text", 5000);
    }

    [Fact]
    public void AppendTranscription_EmptyText_DoesNotThrow()
    {
        // Arrange
        var service = new HistoryService();

        // Act & Assert - should not throw
        service.AppendTranscription("", 0);
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
}
