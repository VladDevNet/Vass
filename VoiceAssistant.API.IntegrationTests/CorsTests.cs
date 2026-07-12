using System.Net.Http.Headers;
using Xunit;

namespace VoiceAssistant.API.IntegrationTests;

// PROJECT-AUDIT-2026-07-10 SEC-06: no CORS policy is configured at all
// anymore (Program.cs no longer calls AddCors/UseCors) -- the audit flagged
// a permissive Access-Control-Allow-Origin: * as too broad, and now that
// ARCH-01 removed the only client that ever needed a browser-facing CORS
// policy (the legacy PWA), the correct posture is "no policy" rather than
// "a narrower policy" (mobile isn't subject to CORS in the first place, so
// there's no legitimate browser origin left to allow).
public class CorsTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client;

    public CorsTests(TestWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Response_WithCrossOriginRequest_HasNoAccessControlAllowOriginHeader()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Add("Origin", "https://evil.example.com");

        var response = await _client.SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task PreflightRequest_ToAuthenticatedEndpoint_HasNoAccessControlAllowOriginHeader()
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/auth/me");
        request.Headers.Add("Origin", "https://evil.example.com");
        request.Headers.Add("Access-Control-Request-Method", "GET");

        var response = await _client.SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Methods"));
    }
}
