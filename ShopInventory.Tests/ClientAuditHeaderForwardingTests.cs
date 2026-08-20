using Blazored.LocalStorage;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins that a browser's address reaches the API on the auth calls, not just on sales orders.
/// </summary>
/// <remarks>
/// The Web app calls the API server-to-server, so a request without <c>X-Forwarded-For</c> arrives
/// as loopback. In a nine-hour production log nine of thirty-two auth events read
/// <c>from IP: ::1</c>, and both of the day's failed logins were among them — the two lines an
/// operator would most want an address on. Twenty-four places in the API key rate limiting, lockout
/// and audit on the connection address.
/// <para>
/// Only the sales-order path forwarded it. These tests cover the auth calls, which is where the gap
/// actually hurt, and the loopback rule that keeps a meaningless address from being forwarded as if
/// it meant something.
/// </para>
/// </remarks>
public sealed class ClientAuditHeaderForwardingTests
{
    private const string BrowserIp = "197.221.253.6";

    [Fact]
    public async Task Login_forwards_the_browser_address()
    {
        var (service, recorder) = CreateAuthService(BrowserIp, userAgent: "Mozilla/5.0 (Windows NT 10.0)");

        await service.LoginAsync("crispen.mambeya", "wrong-password");

        var request = Assert.Single(recorder.Requests);
        Assert.EndsWith("api/auth/login", request.RequestUri!.ToString());
        Assert.Equal(BrowserIp, Assert.Single(request.Headers.GetValues(ClientAuditHeaders.ForwardedFor)));
        Assert.Contains("Mozilla/5.0", request.Headers.GetValues("User-Agent"));
    }

    [Fact]
    public async Task Registration_forwards_the_browser_address()
    {
        var (service, recorder) = CreateAuthService(BrowserIp);

        await service.RegisterUserAsync(new ShopInventory.Web.Models.RegisterUserRequest
        {
            Username = "new.user",
            Email = "new.user@example.com",
            Password = "not-a-real-password",
            Role = "SalesRep"
        });

        var request = Assert.Single(recorder.Requests);
        Assert.Equal(BrowserIp, Assert.Single(request.Headers.GetValues(ClientAuditHeaders.ForwardedFor)));
    }

    /// <summary>
    /// A loopback address says nothing about who the caller is, and forwarding it would only make
    /// the API's own resolution trust a value that carries no information.
    /// </summary>
    [Theory]
    [InlineData("::1")]
    [InlineData("127.0.0.1")]
    public async Task A_loopback_address_is_not_forwarded(string loopback)
    {
        var (service, recorder) = CreateAuthService(loopback);

        await service.LoginAsync("someone", "not-a-real-password");

        var request = Assert.Single(recorder.Requests);
        Assert.False(request.Headers.Contains(ClientAuditHeaders.ForwardedFor));
    }

    [Fact]
    public async Task A_circuit_that_never_captured_an_address_still_sends_the_request()
    {
        var (service, recorder) = CreateAuthService(clientIp: null);

        await service.LoginAsync("someone", "not-a-real-password");

        var request = Assert.Single(recorder.Requests);
        Assert.False(request.Headers.Contains(ClientAuditHeaders.ForwardedFor));
    }

    private static (ShopInventory.Web.Services.AuthService Service, RecordingHandler Recorder) CreateAuthService(
        string? clientIp,
        string? userAgent = null)
    {
        var auditContext = new WebClientAuditContext();
        auditContext.Capture(clientIp, userAgent);

        var recorder = new RecordingHandler();
        var httpClient = new HttpClient(recorder) { BaseAddress = new Uri("http://localhost:5106/") };

        var authStateProvider = new CustomAuthStateProvider(
            StubProxy.Unused<ILocalStorageService>(),
            httpClient,
            NullLogger<CustomAuthStateProvider>.Instance,
            auditContext);

        var service = new ShopInventory.Web.Services.AuthService(
            httpClient,
            authStateProvider,
            NullLogger<ShopInventory.Web.Services.AuthService>.Instance,
            auditContext);

        return (service, recorder);
    }

    /// <summary>Captures the outbound request and answers 401, which every caller here tolerates.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"message\":\"Invalid credentials\"}", System.Text.Encoding.UTF8, "application/json"),
                RequestMessage = request
            });
        }
    }
}
