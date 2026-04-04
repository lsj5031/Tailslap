using Moq;
using Xunit;

public class ConfigServiceTests
{
    [Fact]
    public void IsValidUrl_ValidHttpUrl_ReturnsTrue()
    {
        Assert.True(ConfigService.IsValidUrl("http://localhost:11434/v1"));
    }

    [Fact]
    public void IsValidUrl_ValidHttpsUrl_ReturnsTrue()
    {
        Assert.True(ConfigService.IsValidUrl("https://api.openai.com/v1"));
    }

    [Fact]
    public void IsValidUrl_InvalidUrl_ReturnsFalse()
    {
        Assert.False(ConfigService.IsValidUrl("not-a-url"));
    }

    [Fact]
    public void IsValidTemperature_InRange_ReturnsTrue()
    {
        Assert.True(ConfigService.IsValidTemperature(0.5));
    }

    [Fact]
    public void IsValidTemperature_OutOfRange_ReturnsFalse()
    {
        Assert.False(ConfigService.IsValidTemperature(2.5));
    }

    [Fact]
    public void IsValidModelName_NonEmpty_ReturnsTrue()
    {
        Assert.True(ConfigService.IsValidModelName("gpt-4o"));
    }

    [Fact]
    public void IsValidModelName_Empty_ReturnsFalse()
    {
        Assert.False(ConfigService.IsValidModelName(""));
    }

    [Theory]
    [InlineData(99, false)]
    [InlineData(100, true)]
    [InlineData(2500, true)]
    [InlineData(5000, true)]
    [InlineData(5001, false)]
    public void IsValidSilenceThreshold_Tests(int thresholdMs, bool expected)
    {
        Assert.Equal(expected, ConfigService.IsValidSilenceThreshold(thresholdMs));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(16384, true)]
    [InlineData(32768, true)]
    [InlineData(32769, false)]
    public void IsValidMaxTokens_Tests(int maxTokens, bool expected)
    {
        Assert.Equal(expected, ConfigService.IsValidMaxTokens(maxTokens));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(150, true)]
    [InlineData(300, true)]
    [InlineData(301, false)]
    public void IsValidTimeout_Tests(int timeoutSeconds, bool expected)
    {
        Assert.Equal(expected, ConfigService.IsValidTimeout(timeoutSeconds));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(60, true)]
    [InlineData(120, true)]
    [InlineData(121, false)]
    public void IsValidWebSocketTimeout_Tests(int timeoutSeconds, bool expected)
    {
        Assert.Equal(expected, ConfigService.IsValidWebSocketTimeout(timeoutSeconds));
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(30, true)]
    [InlineData(60, true)]
    [InlineData(61, false)]
    public void IsValidWebSocketHeartbeatInterval_Tests(int intervalSeconds, bool expected)
    {
        Assert.Equal(expected, ConfigService.IsValidWebSocketHeartbeatInterval(intervalSeconds));
    }

    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(60, true)]
    [InlineData(120, true)]
    [InlineData(121, false)]
    public void IsValidWebSocketHeartbeatTimeout_Tests(int timeoutSeconds, bool expected)
    {
        Assert.Equal(expected, ConfigService.IsValidWebSocketHeartbeatTimeout(timeoutSeconds));
    }
}
