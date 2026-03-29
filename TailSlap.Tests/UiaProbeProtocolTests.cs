using Xunit;

public class UiaProbeProtocolTests
{
    [Fact]
    public void TryParseArgs_ParsesModeAndHandle()
    {
        bool ok = UiaProbeProtocol.TryParseArgs(
            new[] { UiaProbeRequest.CommandName, "deep", "12345" },
            out var request,
            out var error
        );

        Assert.True(ok);
        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal(UiaProbeMode.Deep, request!.Mode);
        Assert.Equal(12345, request.ForegroundWindowHandle);
    }

    [Fact]
    public void TryParseArgs_RejectsUnknownMode()
    {
        bool ok = UiaProbeProtocol.TryParseArgs(
            new[] { UiaProbeRequest.CommandName, "nope" },
            out var request,
            out var error
        );

        Assert.False(ok);
        Assert.Null(request);
        Assert.Contains("Unknown UIA probe mode", error);
    }

    [Fact]
    public void ResponseSerialization_RoundTripsSuccessText()
    {
        string json = UiaProbeProtocol.Serialize(UiaProbeResponse.Success("hello\nworld"));

        bool ok = UiaProbeProtocol.TryDeserialize(json, out var response, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.NotNull(response);
        Assert.Equal("success", response!.Status);
        Assert.Equal("hello\nworld", response.Text);
    }
}
