using System.Net;
using System.Text;
using System.Text.Json;
using Blazored.LocalStorage;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// One Blazor circuit's worth of API plumbing, signed in on an access token the API no longer
/// accepts: the shared client, the browser's localStorage and the auth state provider over both.
/// </summary>
/// <remarks>
/// For the token renewal tests of the Web's API services. A page that stays open and keeps calling
/// outlives the hour its access token is good for, and a service that sends on the client bare is
/// then answered 401 on every call while the user is still signed in. Each of those tests builds
/// its service over one of these and reads <see cref="Calls"/> to see what reached the API, and on
/// which token.
/// </remarks>
internal sealed class ExpiringSession
{
    public const string StaleToken = "stale";
    public const string FreshToken = "fresh";

    /// <summary>The refresh the API is asked for when the stale token is still attached.</summary>
    public const string RefreshOnStaleToken = $"POST /api/auth/refresh {StaleToken}";

    /// <summary>The refresh the API is asked for before anything has been attached.</summary>
    public const string RefreshBeforeSending = "POST /api/auth/refresh (none)";

    private readonly TokenCheckingApi _api;

    private ExpiringSession(DateTime tokenExpiresAt, Func<HttpRequestMessage, string> answer)
    {
        _api = new TokenCheckingApi(answer);
        HttpClient = new HttpClient(_api) { BaseAddress = new Uri("http://localhost:5106/") };
        LocalStorage = BrowserStorage(new Dictionary<string, object?>
        {
            ["authToken"] = StaleToken,
            ["refreshToken"] = "refresh-1",
            ["tokenExpiresAt"] = tokenExpiresAt
        });
        AuthStateProvider = new CustomAuthStateProvider(
            LocalStorage,
            HttpClient,
            NullLogger<CustomAuthStateProvider>.Instance,
            new WebClientAuditContext());
    }

    public HttpClient HttpClient { get; }

    public ILocalStorageService LocalStorage { get; }

    public CustomAuthStateProvider AuthStateProvider { get; }

    /// <summary>Every request that reached the API: "METHOD /path token".</summary>
    public List<string> Calls => _api.Calls;

    /// <summary>
    /// The stored token still looks valid, but the API refuses it: the session is only found to
    /// have lapsed by the 401.
    /// </summary>
    /// <param name="answer">The JSON body the API gives a request that carries the fresh token.</param>
    public static ExpiringSession RejectedByTheApi(Func<HttpRequestMessage, string>? answer = null) =>
        new(DateTime.UtcNow.AddHours(1), answer ?? (_ => "{}"));

    /// <summary>The stored token is past its expiry, which is known before anything is sent.</summary>
    /// <param name="answer">The JSON body the API gives a request that carries the fresh token.</param>
    public static ExpiringSession PastItsExpiry(Func<HttpRequestMessage, string>? answer = null) =>
        new(DateTime.UtcNow.AddMinutes(-5), answer ?? (_ => "{}"));

    /// <summary>The browser's localStorage, as far as the auth state provider uses it.</summary>
    internal static ILocalStorageService BrowserStorage(Dictionary<string, object?> items) =>
        StubProxy.For<ILocalStorageService>((method, args) =>
        {
            var key = (string)args![0]!;

            switch (method.Name)
            {
                case nameof(ILocalStorageService.GetItemAsync):
                    // Answers ValueTask<T?>, built by reflection because T is only known per call.
                    return items.TryGetValue(key, out var value) && value is not null
                        ? Activator.CreateInstance(method.ReturnType, value)
                        : Activator.CreateInstance(method.ReturnType);

                case nameof(ILocalStorageService.SetItemAsync):
                    items[key] = args[1];
                    return default(ValueTask);

                case nameof(ILocalStorageService.RemoveItemAsync):
                    items.Remove(key);
                    return default(ValueTask);

                default:
                    throw new InvalidOperationException($"ILocalStorageService.{method.Name} was not expected to be called.");
            }
        });

    /// <summary>An API that refuses every token but the one its refresh endpoint hands out.</summary>
    private sealed class TokenCheckingApi(Func<HttpRequestMessage, string> answer) : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var token = request.Headers.Authorization?.Parameter;
            Calls.Add($"{request.Method} {path} {token ?? "(none)"}");

            if (path == "/api/auth/refresh")
            {
                return Task.FromResult(Json(JsonSerializer.Serialize(new
                {
                    accessToken = FreshToken,
                    refreshToken = "refresh-2",
                    expiresAt = DateTime.UtcNow.AddHours(1),
                    user = new { username = "admin", role = "Admin" }
                })));
            }

            return Task.FromResult(token == FreshToken
                ? Json(answer(request))
                : new HttpResponseMessage(HttpStatusCode.Unauthorized) { RequestMessage = request });
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
