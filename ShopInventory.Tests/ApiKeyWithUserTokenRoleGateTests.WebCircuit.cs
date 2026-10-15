using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Models;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;
using WebLoginResponse = ShopInventory.Web.Models.LoginResponse;
using WebUserInfo = ShopInventory.Web.Models.UserInfo;

namespace ShopInventory.Tests;

/// <summary>
/// What the API makes of a request the Web sends from a circuit, depending on who is signed in on it.
/// </summary>
/// <remarks>
/// The request is built by the Web's own pieces — <see cref="CircuitApiKeyHandler"/> on a client
/// carrying the key, <see cref="WebApiCallerCircuitHandler"/> around the activity, and the real
/// <see cref="CustomAuthStateProvider"/> putting the bearer on that client — and the headers it leaves
/// with are judged by the API's real handlers and policies (<see cref="EvaluateAsync(ServiceProvider, System.Reflection.MethodInfo, string?, string?)"/>).
/// Only the portal session is a stand-in: it holds one flag.
/// </remarks>
public sealed partial class ApiKeyWithUserTokenRoleGateTests
{
    /// <summary>
    /// The reported case: the provider cleared the token, a page still sent, and the API answered it
    /// as the key — Admin. Now the key does not go, and an Admin-only action is a 401.
    /// </summary>
    [Fact]
    public async Task A_circuit_whose_token_was_cleared_reaches_an_Admin_gate_unauthenticated()
    {
        await using var services = BuildServices();
        var circuit = new WebCircuit();
        await circuit.Staff.StoreAuthData(SignIn(ApplicationRoles.Cashier));
        await circuit.Staff.ClearAuthData();

        var sent = await circuit.SendAsync();

        Assert.Null(sent.ApiKey);
        Assert.Null(sent.Bearer);
        foreach (var action in new[] { CreateNotification, CreateInvoice, CreateTransferDirect })
        {
            var outcome = await EvaluateAsync(services, action, sent.ApiKey, sent.Bearer);
            Assert.False(outcome.Result.Succeeded, $"{action.DeclaringType!.Name}.{action.Name}");
            Assert.Equal(StatusCodes401, outcome.StatusCode);
        }
    }

    [Fact]
    public async Task A_circuit_nobody_signed_in_on_sends_without_the_key()
    {
        await using var services = BuildServices();
        var circuit = new WebCircuit();

        var sent = await circuit.SendAsync();

        Assert.Null(sent.ApiKey);
        Assert.Equal(StatusCodes401, (await EvaluateAsync(services, CreateNotification, sent.ApiKey, sent.Bearer)).StatusCode);
    }

    /// <summary>
    /// A timer, a hub callback or a Task.Run a page starts carries the page's circuit with it.
    /// </summary>
    [Fact]
    public async Task Work_a_signed_out_circuit_starts_sends_without_the_key_too()
    {
        var circuit = new WebCircuit();

        var sent = await circuit.SendAsync(send => Task.Run(send));

        Assert.Null(sent.ApiKey);
    }

    /// <summary>
    /// A signed-in member of staff is unchanged: the key goes with their token and the API judges them.
    /// </summary>
    [Fact]
    public async Task A_signed_in_cashier_still_sends_the_key_beside_their_token_and_is_judged_as_a_cashier()
    {
        await using var services = BuildServices();
        var circuit = new WebCircuit();
        var token = UserToken(ApplicationRoles.Cashier);
        await circuit.Staff.StoreAuthData(SignIn(ApplicationRoles.Cashier, token));

        var sent = await circuit.SendAsync();

        Assert.Equal(WebApiKey, sent.ApiKey);
        Assert.Equal(token, sent.Bearer);
        Assert.True((await EvaluateAsync(services, CreateInvoice, sent.ApiKey, sent.Bearer)).Result.Succeeded);
        Assert.True((await EvaluateAsync(services, CreateNotification, sent.ApiKey, sent.Bearer)).Result.Forbidden);
    }

    /// <summary>
    /// The customer portal never sends its customer's token to the API. Its calls are the key's, and
    /// stay so once the circuit has proved the customer's session.
    /// </summary>
    [Fact]
    public async Task A_proved_portal_customer_keeps_the_key_only_calls_the_portal_makes()
    {
        await using var services = BuildServices();
        var circuit = new WebCircuit();
        circuit.Portal.HasVerifiedCustomer = true;

        var sent = await circuit.SendAsync();

        Assert.Equal(WebApiKey, sent.ApiKey);
        Assert.Null(sent.Bearer);
        Assert.True((await EvaluateAsync(services, CreateInvoice, sent.ApiKey, sent.Bearer)).Result.Succeeded);

        circuit.Portal.HasVerifiedCustomer = false;
        Assert.Null((await circuit.SendAsync()).ApiKey);
    }

    /// <summary>
    /// Quartz jobs and hosted services run outside every circuit, and the portal's sign-in lookup runs
    /// as the Web app inside one: both keep the key's roles.
    /// </summary>
    [Fact]
    public async Task Work_outside_a_circuit_and_the_web_apps_own_lookups_keep_the_key()
    {
        await using var services = BuildServices();
        var circuit = new WebCircuit();

        var background = await circuit.SendOutsideTheCircuitAsync();
        var lookup = await circuit.SendAsync(async send =>
        {
            using var asTheWebApp = WebApiCaller.AsTheWebApp();
            await send();
        });
        var afterTheLookup = await circuit.SendAsync(async send =>
        {
            using (WebApiCaller.AsTheWebApp())
            {
            }

            await send();
        });

        Assert.Equal(WebApiKey, background.ApiKey);
        Assert.Equal(WebApiKey, lookup.ApiKey);
        Assert.Null(afterTheLookup.ApiKey);
        Assert.True((await EvaluateAsync(services, CreateNotification, background.ApiKey, background.Bearer)).Result.Succeeded);
    }

    /// <summary>
    /// The 401 a signed-out circuit now gets is genuine. Treating it as an expired token would try a
    /// refresh nobody holds and sign the empty session out again on every poll of the page.
    /// </summary>
    [Fact]
    public async Task A_401_to_a_send_that_carried_no_token_is_not_renewed()
    {
        var api = new RefusingApi();
        using var client = new HttpClient(api) { BaseAddress = new Uri("http://api.test/") };
        var browser = new SignedOutBrowser(client);
        var signOuts = 0;
        browser.AuthStateProvider.AuthenticationStateChanged += _ => signOuts++;

        using var response = await ApiTokenAuthentication.SendAsync(
            client, browser.AuthStateProvider, browser.LocalStorage, () => client.GetAsync("api/sync/sap-availability"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(["GET /api/sync/sap-availability"], api.Calls);
        Assert.Equal(0, signOuts);
    }

    private sealed class RefusingApi : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }
    }

    private const int StatusCodes401 = (int)HttpStatusCode.Unauthorized;

    private static WebLoginResponse SignIn(string role, string? token = null) => new()
    {
        AccessToken = token ?? UserToken(role),
        RefreshToken = "refresh",
        ExpiresAt = DateTime.UtcNow.AddHours(1),
        User = new WebUserInfo { Username = $"{role.ToLowerInvariant()}-user", Role = role }
    };

    private sealed record SentHeaders(string? ApiKey, string? Bearer);

    /// <summary>One Blazor circuit: its API client, its auth state provider and its portal session.</summary>
    private sealed class WebCircuit
    {
        private readonly CapturingApi _api = new();
        private readonly HttpClient _client;
        private readonly WebApiCaller _caller;

        public WebCircuit()
        {
            _client = new HttpClient(new CircuitApiKeyHandler { InnerHandler = _api })
            {
                BaseAddress = new Uri("http://api.test/")
            };
            _client.DefaultRequestHeaders.Add(CircuitApiKeyHandler.HeaderName, WebApiKey);

            Staff = new CustomAuthStateProvider(
                ExpiringSession.BrowserStorage([]),
                _client,
                NullLogger<CustomAuthStateProvider>.Instance,
                new WebClientAuditContext());

            var circuitServices = new ServiceCollection()
                .AddSingleton(Staff)
                .AddSingleton<ICustomerPortalSessionService>(Portal)
                .BuildServiceProvider();
            _caller = new WebApiCaller(circuitServices);
        }

        public CustomAuthStateProvider Staff { get; }

        public PortalSession Portal { get; } = new();

        /// <summary>One send from inside an inbound activity of this circuit, as a page makes it.</summary>
        public Task<SentHeaders> SendAsync() => SendAsync(send => send());

        /// <summary>
        /// Runs <paramref name="activity"/> as an inbound activity of this circuit; it calls the send
        /// it is given however a page would (directly, from Task.Run, inside a scope).
        /// </summary>
        public async Task<SentHeaders> SendAsync(Func<Func<Task>, Task> activity)
        {
            var inbound = new WebApiCallerCircuitHandler(_caller)
                .CreateInboundActivityHandler(_ => activity(SendOnceAsync));

            await inbound(null!);
            return _api.Last!;
        }

        public async Task<SentHeaders> SendOutsideTheCircuitAsync()
        {
            await SendOnceAsync();
            return _api.Last!;
        }

        private async Task SendOnceAsync()
        {
            using var response = await _client.GetAsync("api/notification");
        }
    }

    private sealed class CapturingApi : HttpMessageHandler
    {
        public SentHeaders? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = new SentHeaders(
                request.Headers.TryGetValues(CircuitApiKeyHandler.HeaderName, out var keys) ? keys.Single() : null,
                request.Headers.Authorization?.Parameter);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class PortalSession : ICustomerPortalSessionService
    {
        public bool HasVerifiedCustomer { get; set; }

        public Task<CustomerPortalSession?> GetCurrentSessionAsync() => throw new NotSupportedException();

        public bool CanAccessCardCode(CustomerPortalSession? session, string? cardCode) => throw new NotSupportedException();

        public IReadOnlyList<string> ResolveAccessibleCardCodes(CustomerPortalSession? session, string? requestedCardCode) =>
            throw new NotSupportedException();

        public Task LogoutAsync() => throw new NotSupportedException();

        public Task ClearSessionAsync() => throw new NotSupportedException();
    }
}
