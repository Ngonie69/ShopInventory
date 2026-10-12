using System.Net;
using System.Text;
using System.Text.Json;
using Blazored.LocalStorage;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins that the WhatsApp console keeps working past the life of the access token it was opened with.
/// </summary>
/// <remarks>
/// The console refreshes itself every fifteen seconds and is left open for hours. Its service used to
/// send on whatever token the circuit last attached, so an hour in every call was a 401: the session
/// list read as empty, the page said "No OpenWA sessions exist yet", and Create + start answered
/// "Your session has expired" to an operator who was still signed in everywhere else.
/// </remarks>
public sealed class WhatsAppAdminServiceTokenRenewalTests
{
    private const string StaleToken = "stale";
    private const string FreshToken = "fresh";

    [Fact]
    public async Task A_token_the_API_rejects_is_renewed_and_the_read_is_sent_again()
    {
        var api = new TokenCheckingApi();
        var service = CreateService(api, tokenExpiresAt: DateTime.UtcNow.AddHours(1));

        var sessions = await service.GetSessionsAsync();

        Assert.Empty(sessions);
        Assert.Equal(
            [
                $"GET /api/whatsapp/sessions {StaleToken}",
                $"POST /api/auth/refresh {StaleToken}",
                $"GET /api/whatsapp/sessions {FreshToken}"
            ],
            api.Calls);
    }

    [Fact]
    public async Task A_token_past_its_expiry_is_renewed_before_the_session_is_created()
    {
        var api = new TokenCheckingApi();
        var service = CreateService(api, tokenExpiresAt: DateTime.UtcNow.AddMinutes(-5));

        var session = await service.CreateSessionAsync("customer-documents");

        Assert.Equal("customer-documents", session.Name);
        Assert.Equal(
            [
                "POST /api/auth/refresh (none)",
                $"POST /api/whatsapp/sessions {FreshToken}"
            ],
            api.Calls);
    }

    private static WhatsAppAdminService CreateService(TokenCheckingApi api, DateTime tokenExpiresAt)
    {
        var httpClient = new HttpClient(api) { BaseAddress = new Uri("http://localhost:5106/") };
        var localStorage = BrowserStorage(new Dictionary<string, object?>
        {
            ["authToken"] = StaleToken,
            ["refreshToken"] = "refresh-1",
            ["tokenExpiresAt"] = tokenExpiresAt
        });

        var authStateProvider = new CustomAuthStateProvider(
            localStorage,
            httpClient,
            NullLogger<CustomAuthStateProvider>.Instance,
            new WebClientAuditContext());

        return new WhatsAppAdminService(
            httpClient,
            NullLogger<WhatsAppAdminService>.Instance,
            localStorage,
            authStateProvider);
    }

    /// <summary>The browser's localStorage, as far as the auth state provider uses it.</summary>
    private static ILocalStorageService BrowserStorage(Dictionary<string, object?> items) =>
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
    private sealed class TokenCheckingApi : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var token = request.Headers.Authorization?.Parameter;
            Calls.Add($"{request.Method} {path} {token ?? "(none)"}");

            if (path == "/api/auth/refresh")
            {
                return Task.FromResult(Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    accessToken = FreshToken,
                    refreshToken = "refresh-2",
                    expiresAt = DateTime.UtcNow.AddHours(1),
                    user = new { username = "admin", role = "Admin" }
                })));
            }

            if (token != FreshToken)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { RequestMessage = request });
            }

            return Task.FromResult(request.Method == HttpMethod.Post
                ? Json(HttpStatusCode.Created, """{"id":"s-1","name":"customer-documents","status":"created"}""")
                : Json(HttpStatusCode.OK, "[]"));
        }

        private static HttpResponseMessage Json(HttpStatusCode statusCode, string body) =>
            new(statusCode) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
