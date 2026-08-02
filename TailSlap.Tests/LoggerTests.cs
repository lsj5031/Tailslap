using System;
using System.Linq;
using TailSlap;
using Xunit;

public class LoggerTests
{
    [Fact]
    public void ReadRecentIssues_FiltersToErrorAndWarning_ExcludesInfo()
    {
        Logger.Log("info entry that must be excluded");
        Logger.LogWarning("a warning entry");
        Logger.Error("an error entry");
        Logger.Flush();

        var issues = Logger.ReadRecentIssues(maxEntries: 1000);

        Assert.Contains(issues, i => i.Msg.Contains("a warning entry"));
        Assert.Contains(issues, i => i.Msg.Contains("an error entry"));
        Assert.DoesNotContain(issues, i => i.Msg.Contains("info entry that must be excluded"));
    }

    [Fact]
    public void ReadRecentIssues_NewestFirst()
    {
        Logger.LogWarning("older warning entry");
        Logger.Error("newer error entry");
        Logger.Flush();

        var issues = Logger.ReadRecentIssues(maxEntries: 1000);

        int indexWarn = issues.FindIndex(i => i.Msg.Contains("older warning entry"));
        int indexError = issues.FindIndex(i => i.Msg.Contains("newer error entry"));
        Assert.True(indexWarn >= 0, "warning entry should be found");
        Assert.True(indexError >= 0, "error entry should be found");
        Assert.True(
            indexError < indexWarn,
            "newer entry must appear before older entry (newest first)"
        );
    }

    [Fact]
    public void ReadRecentIssues_NeverThrows()
    {
        // Safe to call even if no log exists yet / files are unreadable.
        var issues = Logger.ReadRecentIssues(maxEntries: 10);
        Assert.NotNull(issues);
        Assert.All(issues, i => Assert.False(string.IsNullOrEmpty(i.Level)));
    }
}
