using System;
using TailSlap;
using Xunit;

public class HistoryQueryTests
{
    [Fact]
    public void Matches_EmptyQuery_MatchesAll()
    {
        Assert.True(HistoryQuery.Matches("", "hello", "world"));
        Assert.True(HistoryQuery.Matches("   ", "hello"));
        Assert.True(HistoryQuery.Matches(null, "hello"));
    }

    [Fact]
    public void Matches_IsCaseInsensitive()
    {
        Assert.True(HistoryQuery.Matches("HeLLo", "say hello there"));
        Assert.False(HistoryQuery.Matches("xyz", "say hello there"));
    }

    [Fact]
    public void Matches_SearchesAnyField()
    {
        Assert.True(HistoryQuery.Matches("gpt", "orig", "refined", "gpt-4"));
        Assert.True(HistoryQuery.Matches("orig", "original text", "refined", "model"));
    }

    [Fact]
    public void FormatRefinementExport_IncludesWarningAndEntries()
    {
        var text = HistoryQuery.FormatRefinementExport(
            new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            new[]
            {
                (
                    new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    "llama",
                    "raw text",
                    "polished text"
                ),
            }
        );

        Assert.Contains("plaintext", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("raw text", text);
        Assert.Contains("polished text", text);
        Assert.Contains("model=llama", text);
    }

    [Fact]
    public void FormatTranscriptionExport_IncludesDuration()
    {
        var text = HistoryQuery.FormatTranscriptionExport(
            DateTime.UtcNow,
            new[] { (DateTime.UtcNow, "spoken words", 1500) }
        );

        Assert.Contains("spoken words", text);
        Assert.Contains("durationMs=1500", text);
    }
}
