using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using ErrorOr;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShopInventory.Configuration;
using ShopInventory.Controllers;
using ShopInventory.Features.RateLimit;

namespace ShopInventory.Tests;

/// <summary>
/// The auth rate limit, proved by sending sign-ins through the real middleware to the real
/// <see cref="AuthController"/>, with the policies and the controller convention Program.cs uses.
/// </summary>
/// <remarks>
/// The limit was configured for as long as it existed and never applied: Program.cs mapped the
/// controllers with <c>RequireRateLimiting("api")</c>, whose metadata is added after the action's
/// <c>[EnableRateLimiting("auth")]</c> and so wins. Only a request through the pipeline shows that.
/// </remarks>
public sealed class AuthRateLimitPipelineTests
{
    private const int AuthLimit = 10;

    [Fact]
    public async Task The_eleventh_sign_in_in_a_minute_from_one_address_is_refused()
    {
        await using var app = await StartAsync();
        using var http = Client(app);

        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < AuthLimit + 1; attempt++)
            statuses.Add((await SignInAsync(http, apiKey: "web-portal-key")).StatusCode);

        // The first ten reach the controller, which refuses the made-up credentials.
        Assert.All(statuses.Take(AuthLimit), status => Assert.NotEqual(HttpStatusCode.TooManyRequests, status));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[AuthLimit]);
    }

    [Fact]
    public async Task A_different_api_key_from_the_same_address_shares_the_allowance()
    {
        // Every sign-in the Web proxies carries the Web's key. The key must not be what decides the
        // bucket, or one key's bucket is every Web user's, and a second key a way round the limit.
        await using var app = await StartAsync();
        using var http = Client(app);

        for (var attempt = 0; attempt < AuthLimit; attempt++)
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await SignInAsync(http, apiKey: "key-one")).StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await SignInAsync(http, apiKey: "key-two")).StatusCode);
    }

    [Fact]
    public void The_auth_partition_is_the_callers_address()
    {
        var settings = new RateLimitSettings();

        string KeyFor(string ip, string apiKey)
        {
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
            context.Request.Headers["X-API-Key"] = apiKey;
            return ApiRateLimiting.GetAuthPartitionKey(context, settings);
        }

        Assert.Equal(KeyFor("10.10.11.27", "web"), KeyFor("10.10.11.27", "other"));
        Assert.NotEqual(KeyFor("10.10.11.27", "web"), KeyFor("10.10.11.28", "web"));
    }

    private static Task<HttpResponseMessage> SignInAsync(HttpClient http, string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { username = "nobody", password = "wrong" })
        };
        request.Headers.Add("X-API-Key", apiKey);
        return http.SendAsync(request);
    }

    private static HttpClient Client(WebApplication app) => new()
    {
        BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First())
    };

    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        builder.Services.AddSingleton<IRateLimitConfigStore>(new FixedRateLimitConfigStore(new RateLimitSettings
        {
            PermitLimit = 100,
            WindowSeconds = 60,
            AuthEndpointPermitLimit = AuthLimit,
            AuthEndpointWindowSeconds = 60
        }));
        builder.Services.AddSingleton(RefusingMediator());
        builder.Services.AddProblemDetails();
        builder.Services.AddAuthorization();
        builder.Services.AddControllers().ConfigureApplicationPartManager(parts =>
        {
            parts.ApplicationParts.Add(new AssemblyPart(typeof(AuthController).Assembly));
            parts.FeatureProviders.Add(new OnlyController<AuthController>());
        });
        builder.Services.AddApiRateLimiting(new SecuritySettings());

        var app = builder.Build();
        app.UseRouting();
        app.UseRateLimiter();
        app.UseAuthorization();
        app.MapControllers().RequireDefaultRateLimiting(ApiRateLimiting.ApiPolicy);
        await app.StartAsync();
        return app;
    }

    /// <summary>Answers every command with a refusal, as a wrong password would be answered.</summary>
    private static IMediator RefusingMediator() => StubProxy.For<IMediator>((method, _) =>
    {
        var errorOrType = method.ReturnType.GetGenericArguments()[0];
        var refusal = errorOrType
            .GetMethod("op_Implicit", BindingFlags.Public | BindingFlags.Static, [typeof(Error)])!
            .Invoke(null, [Error.Unauthorized("Auth.InvalidCredentials", "Invalid username or password")]);

        return typeof(Task).GetMethod(nameof(Task.FromResult))!
            .MakeGenericMethod(errorOrType)
            .Invoke(null, [refusal]);
    });

    private sealed class FixedRateLimitConfigStore(RateLimitSettings settings) : IRateLimitConfigStore
    {
        public RateLimitSettings Current => settings;

        public Task UpdateAsync(RateLimitSettings newSettings, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class OnlyController<TController> : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            foreach (var controller in feature.Controllers.Where(type => type.AsType() != typeof(TController)).ToList())
                feature.Controllers.Remove(controller);
        }
    }
}
