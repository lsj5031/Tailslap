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
}
