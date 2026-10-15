using System.Net;
using System.Reflection;
using System.Text;
using Blazored.LocalStorage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Controllers;
using ShopInventory.Models;
using ShopInventory.Web.Data;
using ShopInventory.Web.Features.CreditNotes.Commands.BulkCancelCreditNotes;
using ShopInventory.Web.Features.CreditNotes.Commands.DuplicateCancelledCreditNotes;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;
using RequirePermissionAttribute = ShopInventory.Authentication.RequirePermissionAttribute;
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

    /// <summary>
    /// A send from a signed-in member of staff's page on a keyed client its service never put their
    /// token on — a client it made from the factory — used to be answered as the key. Now it goes
    /// without the key, so the API sees nobody rather than an Admin.
    /// </summary>
    [Fact]
    public async Task A_signed_in_cashiers_send_without_their_token_goes_without_the_key()
    {
        await using var services = BuildServices();
        var circuit = new WebCircuit();
        await circuit.Staff.StoreAuthData(SignIn(ApplicationRoles.Cashier));

        var sent = await circuit.RunAsync(async () =>
        {
            using var client = circuit.KeyedClient();
            using var response = await client.PostAsync("api/notification", content: null);
        });

        Assert.Equal(new SentRequest("POST /api/notification", ApiKey: null, Bearer: null), Assert.Single(sent));
        Assert.Equal(StatusCodes401, (await EvaluateAsync(services, CreateNotification, apiKey: null, bearerToken: null)).StatusCode);
    }

    /// <summary>
    /// Bulk cancel and duplicate on the credit notes page go on the long-running client. They now
    /// carry the person's token, so the API's permission check reads that person's permissions: the
    /// cashier holds EditInvoices and CreateInvoices, a merchandiser neither.
    /// </summary>
    [Theory]
    [InlineData(ApplicationRoles.Cashier, StatusCodes.Status200OK)]
    [InlineData(ApplicationRoles.Merchandiser, StatusCodes.Status403Forbidden)]
    public async Task Credit_note_bulk_actions_on_the_long_running_client_are_judged_as_the_signed_in_user(string role, int expected)
    {
        await using var services = BuildServices();
        var circuit = new WebCircuit();
        var token = UserToken(role);
        await circuit.Staff.StoreAuthData(SignIn(role, token));
        var cancel = new BulkCancelCreditNotesHandler(
            circuit.ClientFactory, circuit.Staff, circuit.LocalStorage,
            StubProxy.Unused<IAuditService>(), NullLogger<BulkCancelCreditNotesHandler>.Instance);
        var duplicate = new DuplicateCancelledCreditNotesHandler(
            circuit.ClientFactory, circuit.Staff, circuit.LocalStorage,
            StubProxy.Unused<IAuditService>(), NullLogger<DuplicateCancelledCreditNotesHandler>.Instance);

        var sent = await circuit.RunAsync(async () =>
        {
            await cancel.Handle(new BulkCancelCreditNotesCommand([4101], "Raised twice"), CancellationToken.None);
            await duplicate.Handle(new DuplicateCancelledCreditNotesCommand([4101], "Raised twice"), CancellationToken.None);
        });

        Assert.Equal(
            [
                new SentRequest("POST /api/creditnote/bulk-cancel", WebApiKey, token),
                new SentRequest("POST /api/creditnote/duplicate-cancelled", WebApiKey, token)
            ],
            sent);
        foreach (var action in new[] { BulkCancelCreditNotes, DuplicateCancelledCreditNotes })
        {
            Assert.Equal(expected, await EvaluateWithPermissionsAsync(services, action, WebApiKey, token));
        }
    }

    /// <summary>
    /// Data Sync's API syncs go on the long-running client with the person's token: an Admin-only sync
    /// pressed by a cashier is refused as the cashier, where the key alone would have run it.
    /// </summary>
    [Fact]
    public async Task A_data_sync_on_the_long_running_client_is_judged_as_the_signed_in_cashier()
    {
        await using var services = BuildServices();
        var circuit = new WebCircuit();
        var token = UserToken(ApplicationRoles.Cashier);
        await circuit.Staff.StoreAuthData(SignIn(ApplicationRoles.Cashier, token));
        var cache = circuit.MasterDataCache();

        var sent = await circuit.RunAsync(() => cache.SyncItemTaxGroupsFromSapAsync());

        Assert.Equal(new SentRequest("POST /api/sync/item-tax-groups", WebApiKey, token), Assert.Single(sent));
        Assert.True((await EvaluateAsync(services, SyncItemTaxGroups, WebApiKey, token)).Result.Forbidden);
        Assert.True((await EvaluateAsync(services, SyncItemTaxGroups, WebApiKey, bearerToken: null)).Result.Succeeded);
    }

    /// <summary>
    /// A cache sweep a page starts with SapBackgroundPriority.Run runs on the thread pool but carries
    /// the page's circuit, and sends on the circuit's client with the person's token.
    /// </summary>
    [Fact]
    public async Task A_cache_sweep_a_cashiers_page_starts_is_judged_as_the_cashier()
    {
        await using var services = BuildServices();
        var circuit = new WebCircuit();
        var token = UserToken(ApplicationRoles.Cashier);
        await circuit.Staff.StoreAuthData(SignIn(ApplicationRoles.Cashier, token));
        var cache = circuit.MasterDataCache();

        var sent = await circuit.RunAsync(() => SapBackgroundPriority.Run(() => cache.ClearTillTransferRequestItemsAsync()));

        Assert.Equal(new SentRequest("POST /api/sync/transfer-request-items/clear", WebApiKey, token), Assert.Single(sent));
        Assert.True((await EvaluateAsync(services, ClearTransferRequestItems, WebApiKey, token)).Result.Forbidden);
    }

    /// <summary>
    /// The POD pages' typed client carries the person's token, and the API judges the cashier: the
    /// upload-status report admits a cashier, an Admin-only route does not.
    /// </summary>
    [Fact]
    public async Task The_pod_client_is_judged_as_the_signed_in_cashier()
    {
        await using var services = BuildServices();
        var circuit = new WebCircuit();
        var token = UserToken(ApplicationRoles.Cashier);
        await circuit.Staff.StoreAuthData(SignIn(ApplicationRoles.Cashier, token));
        var pods = new PodService(circuit.KeyedClient(), NullLogger<PodService>.Instance, circuit.LocalStorage, circuit.Staff);

        var sent = await circuit.RunAsync(() => pods.GetPodUploadStatusAsync(DateTime.Today.AddDays(-7), DateTime.Today));

        Assert.Equal(new SentRequest("GET /api/invoice/pod-upload-status", WebApiKey, token), Assert.Single(sent));
        Assert.True((await EvaluateAsync(services, GetPodUploadStatus, WebApiKey, token)).Result.Succeeded);
        Assert.True((await EvaluateAsync(services, CreateNotification, WebApiKey, token)).Result.Forbidden);
    }

    /// <summary>
    /// The POD report email reads its report on the long-running client. Pressed on a page it reads as
    /// the person pressing; the schedule runs outside every circuit and reads as the Web app.
    /// </summary>
    [Fact]
    public async Task The_pod_report_email_reads_as_the_signed_in_user_from_a_page_and_as_the_web_app_on_schedule()
    {
        await using var services = BuildServices();
        // The report read fails, which ends the send before anything is exported or mailed.
        var circuit = new WebCircuit(HttpStatusCode.InternalServerError);
        var token = UserToken(ApplicationRoles.Cashier);
        await circuit.Staff.StoreAuthData(SignIn(ApplicationRoles.Cashier, token));
        var email = new PodReportEmailService(
            circuit.ClientFactory,
            circuit.Staff,
            circuit.LocalStorage,
            StubProxy.Unused<IReportExportService>(),
            StubProxy.Unused<IDeliveryRouteDirectory>(),
            StubProxy.Unused<IEmailService>(),
            // The failure is audited, and the audit swallows its own failures.
            StubProxy.Unused<IAuditService>(),
            NullLogger<PodReportEmailService>.Instance);
        var schedule = new PodReportEmailSchedule { Id = 1, Name = "Weekly POD", ToRecipients = "pod@example.test" };

        var fromPage = await circuit.RunAsync(() => email.SendForScheduleAsync(schedule, "cashier-user"));
        var onSchedule = await circuit.RunOutsideTheCircuitAsync(() => email.SendForScheduleAsync(schedule, "System schedule"));

        Assert.Equal(new SentRequest("GET /api/invoice/pod-upload-status", WebApiKey, token), Assert.Single(fromPage));
        Assert.True((await EvaluateAsync(services, CreateNotification, WebApiKey, token)).Result.Forbidden);
        Assert.Equal(new SentRequest("GET /api/invoice/pod-upload-status", WebApiKey, Bearer: null), Assert.Single(onSchedule));
    }

    private static MethodInfo BulkCancelCreditNotes => Action<CreditNoteController>(nameof(CreditNoteController.BulkCancel));

    private static MethodInfo DuplicateCancelledCreditNotes => Action<CreditNoteController>(nameof(CreditNoteController.DuplicateCancelled));

    private static MethodInfo SyncItemTaxGroups => Action<SyncController>(nameof(SyncController.SyncItemTaxGroups));

    private static MethodInfo ClearTransferRequestItems => Action<SyncController>(nameof(SyncController.ClearTransferRequestItems));

    private static MethodInfo GetPodUploadStatus => Action<InvoiceController>(nameof(InvoiceController.GetPodUploadStatus));

    /// <summary>
    /// The authorization middleware's answer, then the action's <see cref="RequirePermissionAttribute"/>
    /// filter, as MVC runs it before the action: the status the request would be refused with, or 200.
    /// </summary>
    private static async Task<int> EvaluateWithPermissionsAsync(ServiceProvider services, MethodInfo action, string? apiKey, string? bearerToken)
    {
        var policy = await EndpointPolicyAsync(services, action);
        await using var request = services.CreateAsyncScope();
        var httpContext = new DefaultHttpContext { RequestServices = request.ServiceProvider };
        if (apiKey is not null)
        {
            httpContext.Request.Headers["X-API-Key"] = apiKey;
        }

        if (bearerToken is not null)
        {
            httpContext.Request.Headers.Authorization = $"Bearer {bearerToken}";
        }

        var evaluator = new PolicyEvaluator(request.ServiceProvider.GetRequiredService<IAuthorizationService>());
        var authentication = await evaluator.AuthenticateAsync(policy, httpContext);
        var result = await evaluator.AuthorizeAsync(policy, authentication, httpContext, resource: null);
        if (!result.Succeeded)
        {
            return result.Challenged ? StatusCodes.Status401Unauthorized : StatusCodes.Status403Forbidden;
        }

        var filterContext = new AuthorizationFilterContext(
            new ActionContext(httpContext, new RouteData(), new ActionDescriptor()), []);
        foreach (var filter in action.GetCustomAttributes<RequirePermissionAttribute>())
        {
            await filter.OnAuthorizationAsync(filterContext);
        }

        return filterContext.Result switch
        {
            null => StatusCodes.Status200OK,
            ForbidResult => StatusCodes.Status403Forbidden,
            UnauthorizedResult => StatusCodes.Status401Unauthorized,
            StatusCodeResult status => status.StatusCode,
            var other => throw new InvalidOperationException($"Unexpected filter result {other.GetType().Name}.")
        };
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

    /// <summary>One request that reached the API: "METHOD /path" and what it carried.</summary>
    private sealed record SentRequest(string Route, string? ApiKey, string? Bearer);

    /// <summary>One Blazor circuit: its API client, its auth state provider and its portal session.</summary>
    private sealed class WebCircuit
    {
        private readonly CapturingApi _api;
        private readonly HttpClient _client;
        private readonly WebApiCaller _caller;

        /// <param name="answer">The status the API answers every request with.</param>
        public WebCircuit(HttpStatusCode answer = HttpStatusCode.OK)
        {
            _api = new CapturingApi(answer);
            _client = KeyedClient();

            LocalStorage = ExpiringSession.BrowserStorage([]);
            Staff = new CustomAuthStateProvider(
                LocalStorage,
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

        public ILocalStorageService LocalStorage { get; }

        public PortalSession Portal { get; } = new();

        /// <summary>
        /// What IHttpClientFactory hands out for the Web's keyed named and typed clients
        /// (ShopInventoryApiLongRunning, IPodService): a new client carrying the key and no token.
        /// </summary>
        public IHttpClientFactory ClientFactory => StubProxy.For<IHttpClientFactory>((_, _) => KeyedClient());

        /// <summary>A new client carrying the key, through the same handler Program.cs puts on every keyed client.</summary>
        public HttpClient KeyedClient()
        {
            var client = new HttpClient(new CircuitApiKeyHandler { InnerHandler = _api }, disposeHandler: false)
            {
                BaseAddress = new Uri("http://api.test/")
            };
            client.DefaultRequestHeaders.Add(CircuitApiKeyHandler.HeaderName, WebApiKey);
            return client;
        }

        /// <summary>The circuit's cache service, on the circuit's shared client and this circuit's session.</summary>
        public MasterDataCacheService MasterDataCache() => new(
            _client,
            ClientFactory,
            NullLogger<MasterDataCacheService>.Instance,
            // Only the sync bookkeeping reads it, and that swallows its own failures.
            StubProxy.Unused<IDbContextFactory<WebAppDbContext>>(),
            LocalStorage,
            Staff,
            StubProxy.Unused<IAppSettingsProvider>());

        /// <summary>
        /// Runs <paramref name="work"/> as an inbound activity of this circuit, as a page's event handler
        /// runs, and returns every request it sent.
        /// </summary>
        public async Task<IReadOnlyList<SentRequest>> RunAsync(Func<Task> work)
        {
            var first = _api.Sent.Count;
            var inbound = new WebApiCallerCircuitHandler(_caller).CreateInboundActivityHandler(_ => work());

            await inbound(null!);
            return _api.Sent.Skip(first).ToList();
        }

        /// <summary>Runs <paramref name="work"/> outside every circuit, as a Quartz job runs.</summary>
        public async Task<IReadOnlyList<SentRequest>> RunOutsideTheCircuitAsync(Func<Task> work)
        {
            var first = _api.Sent.Count;
            await work();
            return _api.Sent.Skip(first).ToList();
        }

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

    private sealed class CapturingApi(HttpStatusCode answer) : HttpMessageHandler
    {
        public SentHeaders? Last { get; private set; }

        public List<SentRequest> Sent { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = new SentHeaders(
                request.Headers.TryGetValues(CircuitApiKeyHandler.HeaderName, out var keys) ? keys.Single() : null,
                request.Headers.Authorization?.Parameter);

            lock (Sent)
            {
                Sent.Add(new SentRequest($"{request.Method} {request.RequestUri!.AbsolutePath}", Last.ApiKey, Last.Bearer));
            }

            return Task.FromResult(new HttpResponseMessage(answer)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
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
