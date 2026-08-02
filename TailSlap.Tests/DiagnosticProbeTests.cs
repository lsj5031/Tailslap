using System.Net.Http;
using TailSlap;
using Xunit;

namespace TailSlap.Tests;

public sealed class DiagnosticProbeTests
{
    [Fact]
    public void CreateGetRequest_AddsBearerHeaderWithoutExposingKeyInOtherFields()
    {
        using var request = DiagnosticProbe.CreateGetRequest(
            "http://localhost:18000/v1/models",
            " test-key "
        );

        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
    }

    [Fact]
    public void ClassifyHttpStatus_200IsSuccess()
    {
        var result = DiagnosticProbe.ClassifyHttpStatus(
            200,
            postOnlyEndpoint: false,
            apiKeyConfigured: false
        );

        Assert.Equal(DiagnosticSeverity.Success, result.Severity);
        Assert.Equal("Reachable", result.Status);
    }

    [Fact]
    public void ClassifyHttpStatus_405IsSuccessForPostOnlyEndpoint()
    {
        var result = DiagnosticProbe.ClassifyHttpStatus(
            405,
            postOnlyEndpoint: true,
            apiKeyConfigured: false
        );

        Assert.Equal(DiagnosticSeverity.Success, result.Severity);
        Assert.Equal("Reachable (POST required)", result.Status);
    }

    [Fact]
    public void ClassifyHttpStatus_AuthenticationFailureDependsOnKeyConfiguration()
    {
        var withoutKey = DiagnosticProbe.ClassifyHttpStatus(401, false, false);
        var withKey = DiagnosticProbe.ClassifyHttpStatus(401, false, true);

        Assert.Equal(DiagnosticSeverity.Warning, withoutKey.Severity);
        Assert.Equal("Authentication required", withoutKey.Status);
        Assert.Equal(DiagnosticSeverity.Error, withKey.Severity);
        Assert.Equal("Authentication rejected", withKey.Status);
    }

    [Fact]
    public void ClassifyHttpStatus_OtherHttpResponsesRemainWarnings()
    {
        var result = DiagnosticProbe.ClassifyHttpStatus(404, false, false);

        Assert.Equal(DiagnosticSeverity.Warning, result.Severity);
        Assert.Equal("Server responded (404)", result.Status);
    }
}
