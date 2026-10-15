using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The Web shows a user only once the API has said who the token belongs to. What the browser stores
/// beside the token — "userInfo", its role, "tokenExpiresAt" — is never taken as who is signed in.
/// </summary>
/// <remarks>
/// Anyone at the keyboard can write localStorage, so nothing in it is evidence of who is signed in:
/// the token is confirmed with the API, an expiry that cannot be read is unusable, and the role comes
/// from the API's answer.
/// </remarks>
public sealed class BrowserStorageIdentityTests
{
    /// <summary>A token that is not a JWT and a role written into storage by hand.</summary>
    [Fact]
    public async Task A_forged_token_and_role_in_storage_show_no_one()
    {
        var api = new FakeAuthApi();
        var storage = new Dictionary<string, object?>
        {
            ["authToken"] = "x",
            ["userInfo"] = new { username = "admin", role = "Admin" }
        };
        var provider = Provider(api, storage);

        var state = await provider.GetAuthenticationStateAsync();

        Assert.False(state.User.Identity?.IsAuthenticated);
        Assert.False(state.User.IsInRole("Admin"));
        Assert.False(storage.ContainsKey("authToken"), "the forged session is cleared from the browser");
    }

    /// <summary>
    /// A forged expiry beside the token gets it past the local check, but not past the API: api/auth/me
    /// refuses it and there is no refresh token to renew it with.
    /// </summary>
    [Fact]
    public async Task A_forged_token_with_a_forged_expiry_is_refused_by_the_api()
    {
        var api = new FakeAuthApi();
        var storage = new Dictionary<string, object?>
        {
            ["authToken"] = "x",
            ["tokenExpiresAt"] = DateTime.UtcNow.AddHours(8),
            ["userInfo"] = new { username = "admin", role = "Admin" }
        };
        var provider = Provider(api, storage);

        var state = await provider.GetAuthenticationStateAsync();

        Assert.False(state.User.Identity?.IsAuthenticated);
        Assert.Contains("GET /api/auth/me x", api.Calls);
    }

    /// <summary>A real cashier who edits "userInfo" to say Admin is still shown as a cashier.</summary>
    [Fact]
    public async Task The_role_comes_from_the_api_and_not_from_storage()
    {
        var token = FakeAuthApi.Token(DateTime.UtcNow.AddMinutes(30));
        var api = new FakeAuthApi { Users = { [token] = ("cashier1", "Cashier") } };
        var storage = new Dictionary<string, object?>
        {
            ["authToken"] = token,
            ["refreshToken"] = "refresh-1",
            ["userInfo"] = new { username = "cashier1", role = "Admin" }
        };
        var provider = Provider(api, storage);

        var state = await provider.GetAuthenticationStateAsync();

        Assert.True(state.User.Identity?.IsAuthenticated);
        Assert.True(state.User.IsInRole("Cashier"));
        Assert.False(state.User.IsInRole("Admin"));
        Assert.Equal("cashier1", state.User.FindFirst(ClaimTypes.Name)?.Value);
    }

    /// <summary>The API is asked once per circuit, not once per component asking for the state.</summary>
    [Fact]
    public async Task The_api_is_asked_once_per_circuit()
    {
        var token = FakeAuthApi.Token(DateTime.UtcNow.AddMinutes(30));
        var api = new FakeAuthApi { Users = { [token] = ("admin", "Admin") } };
        var provider = Provider(api, new Dictionary<string, object?> { ["authToken"] = token });

        var first = provider.GetAuthenticationStateAsync();
        var second = provider.GetAuthenticationStateAsync();
        await Task.WhenAll(first, second);
        var third = await provider.GetAuthenticationStateAsync();

        Assert.True(third.User.IsInRole("Admin"));
        Assert.Single(api.Calls, call => call.StartsWith("GET /api/auth/me", StringComparison.Ordinal));
    }

    /// <summary>
    /// A token the API no longer accepts — revoked, or signed by someone else — is renewed once, and the
    /// user comes back from the refresh response.
    /// </summary>
    [Fact]
    public async Task A_token_the_api_refuses_is_renewed_and_the_user_taken_from_the_refresh()
    {
        var refused = FakeAuthApi.Token(DateTime.UtcNow.AddMinutes(30));
        var api = new FakeAuthApi { RefreshUser = ("merch1", "Merchandiser") };
        var provider = Provider(api, new Dictionary<string, object?>
        {
            ["authToken"] = refused,
            ["refreshToken"] = "refresh-1",
            ["userInfo"] = new { username = "merch1", role = "Admin" }
        });

        var state = await provider.GetAuthenticationStateAsync();

        Assert.True(state.User.IsInRole("Merchandiser"));
        Assert.False(state.User.IsInRole("Admin"));
        Assert.Contains(api.Calls, call => call.StartsWith("POST /api/auth/refresh", StringComparison.Ordinal));
    }

    /// <summary>An expiry the token does not carry, and nothing stored, is unusable rather than endless.</summary>
    [Fact]
    public async Task An_unreadable_expiry_is_not_sent_as_a_good_token()
    {
        var api = new FakeAuthApi();
        var provider = Provider(api, new Dictionary<string, object?> { ["authToken"] = "opaque" });

        var token = await provider.GetAccessTokenAsync();

        Assert.Null(token);
        Assert.DoesNotContain(api.Calls, call => call.EndsWith(" opaque", StringComparison.Ordinal));
    }

    /// <summary>Signing out on the Web revokes the refresh token on the API, not only in the browser.</summary>
    [Fact]
    public async Task Logout_revokes_the_refresh_token_on_the_api()
    {
        var token = FakeAuthApi.Token(DateTime.UtcNow.AddMinutes(30));
        var api = new FakeAuthApi { Users = { [token] = ("admin", "Admin") } };
        var storage = new Dictionary<string, object?> { ["authToken"] = token, ["refreshToken"] = "refresh-1" };
        var httpClient = api.Client();
        var provider = Provider(api, storage, httpClient);
        var auth = new ShopInventory.Web.Services.AuthService(
            httpClient, provider, NullLogger<ShopInventory.Web.Services.AuthService>.Instance, new WebClientAuditContext());

        await auth.LogoutAsync();

        Assert.Contains($"POST /api/auth/logout {token} refresh-1", api.Calls);
        Assert.False(storage.ContainsKey("refreshToken"));
    }

    private static CustomAuthStateProvider Provider(
        FakeAuthApi api,
        Dictionary<string, object?> storage,
        HttpClient? httpClient = null) =>
        new(
            ExpiringSession.BrowserStorage(storage),
            httpClient ?? api.Client(),
            NullLogger<CustomAuthStateProvider>.Instance,
            new WebClientAuditContext());

    /// <summary>
    /// The three auth routes the provider calls: me answers for the tokens in <see cref="Users"/> and
    /// refuses every other, refresh renews "refresh-1" only, logout records the token it revokes.
    /// </summary>
    private sealed class FakeAuthApi : HttpMessageHandler
    {
        public Dictionary<string, (string Username, string Role)> Users { get; } = [];

        public (string Username, string Role)? RefreshUser { get; init; }

        public List<string> Calls { get; } = [];

        public HttpClient Client() => new(this) { BaseAddress = new Uri("http://localhost:5106/") };

        /// <summary>An unsigned JWT with an exp claim: all the Web reads off a token itself.</summary>
        public static string Token(DateTime expires) =>
            new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
                claims: [new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())],
                expires: expires));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var token = request.Headers.Authorization?.Parameter ?? "(none)";
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

            switch (path)
            {
                case "/api/auth/me":
                    Calls.Add($"GET {path} {token}");
                    return Users.TryGetValue(token, out var user)
                        ? Json(new { username = user.Username, role = user.Role })
                        : new HttpResponseMessage(HttpStatusCode.Unauthorized);

                case "/api/auth/refresh":
                    Calls.Add($"POST {path}");
                    if (RefreshUser is not { } renewed || body?.Contains("refresh-1") != true)
                    {
                        return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                    }

                    var fresh = Token(DateTime.UtcNow.AddMinutes(30));
                    Users[fresh] = renewed;
                    return Json(new
                    {
                        accessToken = fresh,
                        refreshToken = "refresh-2",
                        expiresAt = DateTime.UtcNow.AddMinutes(30),
                        user = new { username = renewed.Username, role = renewed.Role }
                    });

                case "/api/auth/logout":
                    var refreshToken = JsonDocument.Parse(body!).RootElement.GetProperty("refreshToken").GetString();
                    Calls.Add($"POST {path} {token} {refreshToken}");
                    return new HttpResponseMessage(HttpStatusCode.NoContent);

                default:
                    Calls.Add($"{request.Method} {path} {token}");
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        private static HttpResponseMessage Json(object value) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
            };
    }
}
