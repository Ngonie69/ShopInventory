using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Blazored.LocalStorage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins that every call a circuit makes to the API goes out on that circuit's own access token,
/// renewed when it has run out, whether or not the class that sent it knows anything about tokens.
/// </summary>
/// <remarks>
/// Built through the real registration, <see cref="CircuitApiClient.AddCircuitApiClient"/>, over a
/// client factory pipeline shared between circuits as it is in the app. Each test's circuit is a DI
/// scope with its own browser storage.
/// </remarks>
public sealed class CircuitApiTokenHandlerTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Each_circuit_sends_on_its_own_renewed_token_through_the_shared_pipeline()
    {
        await using var portal = new Portal();
        await using var alice = portal.OpenCircuit("alice", expiresIn: TimeSpan.FromMinutes(-5));
        await using var bob = portal.OpenCircuit("bob", expiresIn: TimeSpan.FromMinutes(-5));

        using var first = await alice.Client.GetAsync("api/things").WaitAsync(Patience);
        using var second = await bob.Client.GetAsync("api/things").WaitAsync(Patience);
        using var third = await alice.Client.GetAsync("api/things").WaitAsync(Patience);

        Assert.Equal(
            [
                "POST /api/auth/refresh (none) web-key {\"refreshToken\":\"refresh-alice\"}",
                "GET /api/things fresh-alice web-key",
                "POST /api/auth/refresh (none) web-key {\"refreshToken\":\"refresh-bob\"}",
                "GET /api/things fresh-bob web-key",
                "GET /api/things fresh-alice web-key"
            ],
            portal.Api.Calls);
        Assert.Equal(new Uri("http://localhost:5106/"), alice.Client.BaseAddress);
    }

    /// <summary>
    /// The maintenance settings service sends on the client bare, and its routes take the bearer
    /// alone: an hour in, this was "Your session has expired" on a page whose user was signed in.
    /// </summary>
    [Fact]
    public async Task A_service_that_sends_bare_is_renewed_before_its_token_runs_out()
    {
        await using var portal = new Portal(answer: _ => """{"isActive":true}""");
        await using var circuit = portal.OpenCircuit("carol", expiresIn: TimeSpan.FromMinutes(-5));
        var service = new MaintenanceService(circuit.Client, NullLogger<MaintenanceService>.Instance);

        var settings = await service.GetSettingsAsync().WaitAsync(Patience);

        Assert.NotNull(settings);
        Assert.Equal(
            [
                "POST /api/auth/refresh (none) web-key {\"refreshToken\":\"refresh-carol\"}",
                "GET /api/maintenance/mobile fresh-carol web-key"
            ],
            portal.Api.Calls);
    }

    [Fact]
    public async Task A_token_the_API_rejects_is_renewed_and_the_request_sent_again_with_its_body()
    {
        await using var portal = new Portal();
        await using var circuit = portal.OpenCircuit("dan", expiresIn: TimeSpan.FromHours(1));

        using var response = await circuit.Client
            .PutAsJsonAsync("api/maintenance/mobile", new { isActive = true })
            .WaitAsync(Patience);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            [
                "PUT /api/maintenance/mobile stale-dan web-key {\"isActive\":true}",
                "POST /api/auth/refresh stale-dan web-key {\"refreshToken\":\"refresh-dan\"}",
                "PUT /api/maintenance/mobile fresh-dan web-key {\"isActive\":true}"
            ],
            portal.Api.Calls);
    }

    /// <summary>
    /// A stream has been read by the first attempt. Sending the request again would post an empty
    /// or broken upload, so the refusal goes back to the caller, who can build it again.
    /// </summary>
    [Fact]
    public async Task A_refused_upload_is_not_sent_a_second_time()
    {
        await using var portal = new Portal();
        await using var circuit = portal.OpenCircuit("eve", expiresIn: TimeSpan.FromHours(1));
        using var upload = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes("scanned pod")));

        using var response = await circuit.Client.PostAsync("api/invoice/1/pod", upload).WaitAsync(Patience);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(["POST /api/invoice/1/pod stale-eve web-key scanned pod"], portal.Api.Calls);
    }

    /// <summary>
    /// Navigation renews an expired token itself, holding the provider's refresh lock while the
    /// refresh goes out on this same client. The handler asking for a token for that request would
    /// wait on the lock for ever, and every page would hang on the router.
    /// </summary>
    [Fact]
    public async Task The_refresh_the_provider_sends_does_not_wait_on_itself()
    {
        await using var portal = new Portal();
        await using var circuit = portal.OpenCircuit("fay", expiresIn: TimeSpan.FromMinutes(-5));

        var state = await circuit.AuthStateProvider.GetAuthenticationStateAsync().WaitAsync(Patience);

        Assert.True(state.User.Identity?.IsAuthenticated);
        Assert.Equal(
            ["POST /api/auth/refresh (none) web-key {\"refreshToken\":\"refresh-fay\"}"],
            portal.Api.Calls);
    }

    /// <summary>
    /// The assistant calls its AI provider on this same client. The access token is the API's and
    /// must not follow a request to another host.
    /// </summary>
    [Fact]
    public async Task A_request_to_another_host_is_left_as_the_caller_built_it()
    {
        await using var portal = new Portal();
        await using var circuit = portal.OpenCircuit("gus", expiresIn: TimeSpan.FromMinutes(-5));

        using var elsewhere = await circuit.Client.GetAsync("https://ai.example/v1/models").WaitAsync(Patience);

        Assert.Equal(["GET https://ai.example/v1/models (none) web-key"], portal.Api.Calls);
    }

    [Fact]
    public async Task Credentials_of_another_kind_are_not_replaced()
    {
        await using var portal = new Portal();
        await using var circuit = portal.OpenCircuit("hal", expiresIn: TimeSpan.FromMinutes(-5));
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/things");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", "dXNlcjpwYXNz");

        using var response = await circuit.Client.SendAsync(request).WaitAsync(Patience);

        Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
        Assert.Equal(["GET /api/things dXNlcjpwYXNz web-key"], portal.Api.Calls);
    }

    [Fact]
    public async Task With_nobody_signed_in_the_request_goes_out_on_the_key_alone()
    {
        await using var portal = new Portal();
        await using var circuit = portal.OpenCircuit(user: null, expiresIn: TimeSpan.Zero);

        using var response = await circuit.Client.GetAsync("api/maintenance/status").WaitAsync(Patience);

        Assert.Equal(["GET /api/maintenance/status (none) web-key"], portal.Api.Calls);
    }

    /// <summary>
    /// Services wrapped in <see cref="ApiTokenAuthentication.SendAsync"/> renew after a 401 too.
    /// When the handler has already renewed and the API still refuses, the wrapper must stop: a
    /// polling page whose calls are genuinely refused would otherwise spend three refreshes a poll.
    /// </summary>
    [Fact]
    public async Task A_wrapped_service_does_not_renew_a_second_time_for_the_same_refusal()
    {
        await using var portal = new Portal { RefusesEveryToken = true };
        await using var circuit = portal.OpenCircuit("ivy", expiresIn: TimeSpan.FromHours(1));
        var service = new SapAvailabilityService(
            circuit.Client,
            NullLogger<SapAvailabilityService>.Instance,
            circuit.LocalStorage,
            circuit.AuthStateProvider);

        var availability = await service.GetAsync().WaitAsync(Patience);

        Assert.Null(availability);
        Assert.Equal(
            [
                "GET /api/sync/sap-availability stale-ivy web-key",
                "POST /api/auth/refresh stale-ivy web-key {\"refreshToken\":\"refresh-ivy\"}",
                "GET /api/sync/sap-availability fresh-ivy web-key"
            ],
            portal.Api.Calls);
    }

    [Fact]
    public async Task A_session_that_cannot_be_renewed_is_handed_its_401_and_signed_out()
    {
        await using var portal = new Portal { RefusesToRefresh = true };
        await using var circuit = portal.OpenCircuit("jon", expiresIn: TimeSpan.FromHours(1));

        using var response = await circuit.Client.GetAsync("api/things").WaitAsync(Patience);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(
            [
                "GET /api/things stale-jon web-key",
                "POST /api/auth/refresh stale-jon web-key {\"refreshToken\":\"refresh-jon\"}"
            ],
            portal.Api.Calls);
        Assert.False(circuit.Browser.ContainsKey("authToken"));
    }

    /// <summary>The Web's service container, as far as the circuit's API client is concerned.</summary>
    private sealed class Portal : IAsyncDisposable
    {
        private readonly ServiceProvider _services;

        public Portal(Func<HttpRequestMessage, string>? answer = null)
        {
            Api = new FakeApi(this, answer ?? (_ => "{}"));

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddHttpClient("ShopInventoryApi", client =>
                {
                    client.BaseAddress = new Uri("http://localhost:5106/");
                    client.DefaultRequestHeaders.Add("X-API-Key", "web-key");
                })
                .ConfigurePrimaryHttpMessageHandler(() => Api)
                .AddHttpMessageHandler(() => new SapBackgroundPriorityHandler())
                .AddHttpMessageHandler(() => new PageReadCancellationHandler());
            services.AddCircuitApiClient("ShopInventoryApi");
            services.AddScoped<BrowserItems>();
            services.AddScoped(circuit => ExpiringSession.BrowserStorage(circuit.GetRequiredService<BrowserItems>()));
            services.AddScoped<WebClientAuditContext>();
            services.AddScoped<CustomAuthStateProvider>();

            _services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        }

        public FakeApi Api { get; }

        /// <summary>The API answers 401 whatever token it is shown.</summary>
        public bool RefusesEveryToken { get; init; }

        /// <summary>The API rejects the refresh token.</summary>
        public bool RefusesToRefresh { get; init; }

        /// <summary>A circuit whose browser holds <paramref name="user"/>'s session, or none.</summary>
        public Circuit OpenCircuit(string? user, TimeSpan expiresIn)
        {
            var scope = _services.CreateAsyncScope();
            var browser = scope.ServiceProvider.GetRequiredService<BrowserItems>();
            if (user is not null)
            {
                browser["authToken"] = $"stale-{user}";
                browser["refreshToken"] = $"refresh-{user}";
                browser["tokenExpiresAt"] = DateTime.UtcNow.Add(expiresIn);
            }

            return new Circuit(scope, browser);
        }

        public ValueTask DisposeAsync() => _services.DisposeAsync();
    }

    private sealed class BrowserItems : Dictionary<string, object?>;

    private sealed class Circuit(AsyncServiceScope scope, BrowserItems browser) : IAsyncDisposable
    {
        public BrowserItems Browser => browser;

        public HttpClient Client => scope.ServiceProvider.GetRequiredService<HttpClient>();

        public ILocalStorageService LocalStorage => scope.ServiceProvider.GetRequiredService<ILocalStorageService>();

        public CustomAuthStateProvider AuthStateProvider => scope.ServiceProvider.GetRequiredService<CustomAuthStateProvider>();

        public ValueTask DisposeAsync() => scope.DisposeAsync();
    }

    /// <summary>
    /// Accepts only the token its refresh endpoint hands out, "fresh-{user}", and writes down each
    /// request as "METHOD path-or-url token key body".
    /// </summary>
    private sealed class FakeApi(Portal portal, Func<HttpRequestMessage, string> answer) : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var where = uri.Host == "localhost" ? uri.AbsolutePath : uri.ToString();
            var token = request.Headers.Authorization?.Parameter ?? "(none)";
            var key = request.Headers.TryGetValues("X-API-Key", out var keys) ? string.Join(",", keys) : "(no key)";
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Calls)
            {
                Calls.Add($"{request.Method} {where} {token} {key} {body}".TrimEnd());
            }

            if (uri.AbsolutePath == "/api/auth/refresh")
            {
                if (portal.RefusesToRefresh)
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized) { RequestMessage = request };
                }

                var user = JsonDocument.Parse(body).RootElement.GetProperty("refreshToken").GetString()!["refresh-".Length..];
                return Json(JsonSerializer.Serialize(new
                {
                    accessToken = $"fresh-{user}",
                    refreshToken = $"refresh-{user}",
                    expiresAt = DateTime.UtcNow.AddHours(1),
                    user = new { username = user, role = "Admin" }
                }));
            }

            var signedIn = token.StartsWith("fresh-", StringComparison.Ordinal) && !portal.RefusesEveryToken;
            var needsNobody = uri.Host != "localhost" || uri.AbsolutePath == "/api/maintenance/status";
            return signedIn || needsNobody
                ? Json(answer(request))
                : new HttpResponseMessage(HttpStatusCode.Unauthorized) { RequestMessage = request };
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
