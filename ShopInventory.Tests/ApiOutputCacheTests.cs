using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ShopInventory.Tests;

/// <summary>
/// Why the API has no output cache, and a guard against one coming back unnoticed.
/// </summary>
/// <remarks>
/// It had one, on the product, item-group, van-catalogue, warehouse and report endpoints, and it never
/// served a response: every endpoint requires sign-in, and ASP.NET's output cache does not serve a request
/// that is authenticated or carries an Authorization header. Making it work was not worth doing. The Web
/// calls the master-data endpoints only to fill its own cache or for a manual Data Sync, which must read
/// SAP fresh; the reports and warehouses are already cached in-process. And a cached response is served
/// before MVC's filters run, so a [RequirePermission] added to a cached action later would be skipped.
/// </remarks>
public sealed class ApiOutputCacheTests
{
    [Fact]
    public void No_api_endpoint_asks_for_an_output_cache_it_would_never_get()
    {
        var cached = typeof(ShopInventory.Controllers.ProductController).Assembly.GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type) && !type.IsAbstract)
            .SelectMany(type => type
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => method.IsDefined(typeof(OutputCacheAttribute), true)
                                 || type.IsDefined(typeof(OutputCacheAttribute), true))
                .Select(method => $"{type.Name}.{method.Name}"))
            .ToList();

        Assert.Empty(cached);
    }

    /// <summary>
    /// The rule the removal rests on, with a policy built exactly as Program.cs built the old ones.
    /// </summary>
    [Fact]
    public async Task The_output_cache_never_serves_a_signed_in_caller()
    {
        var runs = new Dictionary<string, int> { ["signed-in"] = 0, ["anonymous"] = 0 };
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, SignedInWhenAskedTo>("Test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddOutputCache(options =>
        {
            options.AddBasePolicy(policy => policy.NoCache());
            options.AddPolicy("master-data", policy => policy.Cache().Expire(TimeSpan.FromMinutes(30)).SetVaryByQuery("*"));
        });

        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseOutputCache();
        app.MapGet("/signed-in", () => { runs["signed-in"]++; return "ok"; }).RequireAuthorization().CacheOutput("master-data");
        app.MapGet("/anonymous", () => { runs["anonymous"]++; return "ok"; }).CacheOutput("master-data");
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        using var http = new HttpClient { BaseAddress = new Uri(address) };

        for (var call = 0; call < 2; call++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/signed-in");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "token");
            (await http.SendAsync(request)).EnsureSuccessStatusCode();
            (await http.GetAsync("/anonymous")).EnsureSuccessStatusCode();
        }

        await app.StopAsync();

        Assert.Equal(2, runs["signed-in"]); // run both times: never served from the cache
        Assert.Equal(1, runs["anonymous"]); // the control: the same policy does cache an anonymous caller
    }

    /// <summary>Signs the request in as a user whenever it carries an Authorization header.</summary>
    private sealed class SignedInWhenAskedTo(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey("Authorization"))
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "rep")], "Test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }
}
