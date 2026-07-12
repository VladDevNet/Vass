using System.Linq;
using System.Net.Http.Headers;
using Xunit;

namespace VoiceAssistant.API.IntegrationTests;

// PROJECT-AUDIT-2026-07-10 SEC-06: the CORS policy is restricted to
// loopback origins (localhost/127.0.0.1/::1, any port) instead of the
// audit-flagged AllowAnyOrigin(). An arbitrary external website (simulated
// here as https://evil.example.com) gets no Access-Control-* headers at
// all -- exactly the audit's own finding closed. A request claiming a
// loopback origin (simulating mobile/'s own "web" preview target, run
// locally per .claude/launch.json's "mobile-web" config on
// http://localhost:19006) DOES get a real, approved CORS response --
// preserving this project's established way of visually verifying mobile
// UI work in a browser against the live API without a physical device
// (see docs/react-native/BACKLOG.md's PR #10 verification). A remote
// attacker's page can never satisfy this: the Origin header is set by the
// browser from the REQUESTING page's own origin and can't be spoofed by
// that page's own JavaScript to claim "http://localhost:19006".
public class CorsTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client;

    public CorsTests(TestWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Response_WithExternalOrigin_HasNoAccessControlAllowOriginHeader()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Add("Origin", "https://evil.example.com");

        var response = await _client.SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Response_WithLoopbackOrigin_HasAccessControlAllowOriginHeader()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Add("Origin", "http://localhost:19006");

        var response = await _client.SendAsync(request);

        Assert.True(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal("http://localhost:19006", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    // A rejected-origin preflight doesn't come back as a CORS-specific
    // error -- CORS middleware simply declines to add approval headers and
    // lets the request fall through to normal routing, which itself 405s
    // since no controller action accepts OPTIONS on this route. The
    // ABSENCE of Access-Control-* headers is what actually stops a real
    // browser from proceeding; the 405 is incidental to how ASP.NET Core's
    // CORS middleware is implemented, not a deliberate rejection status --
    // asserted on directly below rather than relying on the status code.
    [Fact]
    public async Task PreflightRequest_WithExternalOrigin_HasNoAccessControlHeaders()
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/auth/me");
        request.Headers.Add("Origin", "https://evil.example.com");
        request.Headers.Add("Access-Control-Request-Method", "GET");

        var response = await _client.SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Methods"));
    }

    [Fact]
    public async Task PreflightRequest_WithLoopbackOrigin_IsApprovedByCors()
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/auth/me");
        request.Headers.Add("Origin", "http://localhost:19006");
        request.Headers.Add("Access-Control-Request-Method", "GET");

        var response = await _client.SendAsync(request);

        // A genuinely CORS-approved preflight is short-circuited by CORS
        // middleware BEFORE routing ever sees it -- unlike the
        // rejected-origin case above, this never falls through to (and so
        // can't 405 from) the "no OPTIONS handler" routing fallback.
        Assert.True(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Contains("GET", response.Headers.GetValues("Access-Control-Allow-Methods").Single());
    }
}
