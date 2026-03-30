using Xunit;

public class DpapiTests
{
    [Fact]
    public void ProtectAndUnprotect_RoundTrip_ReturnsOriginalString()
    {
        // Arrange
        var original = "Hello, World! 123 @#$";

        // Act
        var protectedString = Dpapi.Protect(original);
        var unprotectedString = Dpapi.Unprotect(protectedString);

        // Assert
        Assert.Equal(original, unprotectedString);
    }

    [Fact]
    public void ProtectAndUnprotect_EmptyString_ReturnsEmptyString()
    {
        // Arrange
        var original = "";

        // Act
        var protectedString = Dpapi.Protect(original);
        var unprotectedString = Dpapi.Unprotect(protectedString);

        // Assert
        Assert.Equal(original, unprotectedString);
    }

    [Fact]
    public void Unprotect_InvalidBase64_ReturnsEmptyString()
    {
        // Arrange
        var invalidBase64 = "This is not base64!";

        // Act
        var result = Dpapi.Unprotect(invalidBase64);

        // Assert
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Unprotect_ValidBase64InvalidData_ReturnsEmptyString()
    {
        // Arrange
        // "InvalidData" in base64 is "SW52YWxpZERhdGE="
        var validBase64InvalidData = "SW52YWxpZERhdGE=";

        // Act
        var result = Dpapi.Unprotect(validBase64InvalidData);

        // Assert
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Protect_NullInput_ReturnsEmptyString()
    {
        // Arrange
        string? nullInput = null;

        // Act
        // According to the code: Encoding.UTF8.GetBytes(plaintext) will throw if plaintext is null.
        // It's wrapped in a try-catch that logs and returns string.Empty.
        var result = Dpapi.Protect(nullInput!);

        // Assert
        Assert.Equal(string.Empty, result);
    }
}
