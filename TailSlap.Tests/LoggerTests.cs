using System;
using System.Linq;
using TailSlap;
using Xunit;

// LoggerTests read the shared static Logger's on-disk log file. Other test
// classes run in parallel and write to the same Logger, which made these tests
// flaky (entries not yet durable when read). This collection runs in isolation
// so the log is quiescent while these tests read it.
[CollectionDefinition("LoggerTests", DisableParallelization = true)]
public class LoggerTestsCollection { }

[Collection("LoggerTests")]
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
        // Windows DateTime.UtcNow has ~15ms resolution; back-to-back writes can
        // share a timestamp and the timestamp sort is unstable for ties. Pause
        // so the two entries are guaranteed distinct timestamps.
        Logger.LogWarning("older warning entry");
        System.Threading.Thread.Sleep(25);
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
