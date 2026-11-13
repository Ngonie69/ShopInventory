using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ShopInventory.Services.CallTimings;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins how the Web says which part of it a request came from, and that the API's call timings then
/// count a customer portal page apart from the Web's background work.
/// </summary>
/// <remarks>
/// Both reach the API with the Web's key and no user token. The request is sent by the Web's own
/// <see cref="WebSurfaceHandler"/>, inside an inbound activity published by the real
/// <see cref="WebApiCallerCircuitHandler"/>, and the headers it leaves with are classified by the API's
/// real <see cref="InboundCallerClassifier"/>. Only the circuit's address is a stand-in.
/// </remarks>
public sealed class WebSurfaceHeaderTests
{
    [Theory]
    [InlineData("https://shop.test/customer-portal", WebApiClientIdentity.CustomerPortalSurface)]
    [InlineData("https://shop.test/customer-portal/invoices?page=2", WebApiClientIdentity.CustomerPortalSurface)]
    [InlineData("https://shop.test/Customer-Portal/login", WebApiClientIdentity.CustomerPortalSurface)]
    [InlineData("https://shop.test/customer-portal-management", WebApiClientIdentity.StaffSurface)]
    [InlineData("https://shop.test/invoices", WebApiClientIdentity.StaffSurface)]
    [InlineData("https://shop.test/", WebApiClientIdentity.StaffSurface)]
    public async Task A_request_from_a_page_names_the_part_of_the_web_it_is_on(string address, string surface)
    {
        var sent = await SendFromCircuitAsync(address);

        Assert.Equal(surface, sent);
    }

    [Fact]
    public async Task Background_work_sends_no_surface_even_when_a_client_carried_one()
    {
        var api = new CapturingApi();
        using var client = new HttpClient(new WebSurfaceHandler { InnerHandler = api }) { BaseAddress = new Uri("http://api.test/") };
        client.DefaultRequestHeaders.Add(WebApiClientIdentity.SurfaceHeaderName, WebApiClientIdentity.CustomerPortalSurface);

        // Run in a context of its own, so no circuit published elsewhere on this thread can leak in.
        await Task.Run(async () =>
        {
            using var response = await client.GetAsync("api/stock/warehouses");
        });

        Assert.Null(api.Surface);
    }

    [Fact]
    public async Task A_lookup_the_web_makes_for_the_portal_names_the_portal_and_the_name_ends_with_it()
    {
        var api = new CapturingApi();
        using var client = new HttpClient(new WebSurfaceHandler { InnerHandler = api }) { BaseAddress = new Uri("http://api.test/") };
        var surfaces = new List<string?>();

        await Task.Run(async () =>
        {
            using (WebApiCaller.AsTheWebApp(WebApiClientIdentity.CustomerPortalSurface))
            {
                using var inside = await client.GetAsync("api/businesspartner/x");
                surfaces.Add(api.Surface);
            }

            using var after = await client.GetAsync("api/businesspartner/x");
            surfaces.Add(api.Surface);

            using (WebApiCaller.AsTheWebApp())
            {
                using var unnamed = await client.GetAsync("api/businesspartner/x");
                surfaces.Add(api.Surface);
            }
        });

        Assert.Equal([WebApiClientIdentity.CustomerPortalSurface, null, null], surfaces);
    }

    [Fact]
    public async Task The_portal_sign_in_lookup_inside_a_staff_circuit_still_counts_as_the_portal()
    {
        // The scope leaves the circuit (so the key goes), and the surface it names is the one sent.
        var api = new CapturingApi();
        using var client = new HttpClient(new WebSurfaceHandler { InnerHandler = api }) { BaseAddress = new Uri("http://api.test/") };
        var circuitServices = new ServiceCollection()
            .AddSingleton<NavigationManager>(new FixedNavigationManager("https://shop.test/invoices"))
            .BuildServiceProvider();
        var inbound = new WebApiCallerCircuitHandler(new WebApiCaller(circuitServices))
            .CreateInboundActivityHandler(async _ =>
            {
                using var asTheWebApp = WebApiCaller.AsTheWebApp(WebApiClientIdentity.CustomerPortalSurface);
                Assert.Null(WebApiCaller.Current);
                using var response = await client.GetAsync("api/businesspartner/x");
            });

        await Task.Run(() => inbound(null!));

        Assert.Equal(WebApiClientIdentity.CustomerPortalSurface, api.Surface);
        Assert.Equal(InboundCallerClassifier.CustomerPortal, ClassifyWebKeyOnly(api.Surface));
    }

    [Fact]
    public void A_circuit_whose_address_cannot_be_read_counts_as_staff()
    {
        var caller = new WebApiCaller(new ServiceCollection()
            .AddSingleton<NavigationManager>(new UninitialisedNavigationManager())
            .BuildServiceProvider());

        Assert.Equal(WebApiClientIdentity.StaffSurface, caller.Surface);
    }

    [Fact]
    public async Task The_api_counts_a_portal_page_apart_from_the_webs_background_work()
    {
        var portal = await SendFromCircuitAsync("https://shop.test/customer-portal/statements");
        var staffPage = await SendFromCircuitAsync("https://shop.test/stock");

        Assert.Equal(InboundCallerClassifier.CustomerPortal, ClassifyWebKeyOnly(portal));
        Assert.Equal(InboundCallerClassifier.WebWithoutUser, ClassifyWebKeyOnly(staffPage));
        Assert.Equal(InboundCallerClassifier.WebBackground, ClassifyWebKeyOnly(null));
    }

    private static async Task<string?> SendFromCircuitAsync(string address)
    {
        var api = new CapturingApi();
        using var client = new HttpClient(new WebSurfaceHandler { InnerHandler = api }) { BaseAddress = new Uri("http://api.test/") };
        var circuitServices = new ServiceCollection()
            .AddSingleton<NavigationManager>(new FixedNavigationManager(address))
            .BuildServiceProvider();
        var inbound = new WebApiCallerCircuitHandler(new WebApiCaller(circuitServices))
            .CreateInboundActivityHandler(async _ =>
            {
                using var response = await client.GetAsync("api/invoice");
            });

        await Task.Run(() => inbound(null!));
        return api.Surface;
    }

    /// <summary>What the API makes of a Web request that carried the key, no token, and <paramref name="surface"/>.</summary>
    private static string ClassifyWebKeyOnly(string? surface)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[WebApiClientIdentity.ClientAppHeaderName] = WebApiClientIdentity.ClientAppValue;
        if (surface is not null)
        {
            context.Request.Headers[WebApiClientIdentity.SurfaceHeaderName] = surface;
        }

        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, "MainIntegration"),
                new Claim(ClaimTypes.AuthenticationMethod, ShopInventory.Authentication.AuthenticationSchemes.ApiKey),
                new Claim("api_key_name", "MainIntegration"),
            ],
            ShopInventory.Authentication.AuthenticationSchemes.ApiKey));

        return InboundCallerClassifier.Classify(context);
    }

    private sealed class CapturingApi : HttpMessageHandler
    {
        public string? Surface { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Surface = request.Headers.TryGetValues(WebApiClientIdentity.SurfaceHeaderName, out var values)
                ? values.Single()
                : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class FixedNavigationManager : NavigationManager
    {
        public FixedNavigationManager(string uri) => Initialize("https://shop.test/", uri);

        protected override void NavigateToCore(string uri, bool forceLoad) => throw new NotSupportedException();
    }

    /// <summary>A circuit not yet given its address: reading <see cref="NavigationManager.Uri"/> throws.</summary>
    private sealed class UninitialisedNavigationManager : NavigationManager
    {
        protected override void NavigateToCore(string uri, bool forceLoad) => throw new NotSupportedException();
    }
}
