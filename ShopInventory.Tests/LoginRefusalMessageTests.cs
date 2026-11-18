using System.Net;
using System.Text;
using Blazored.LocalStorage;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins that the sign-in page shows the API's reason for refusing a sign-in, not "You do not have
/// permission to perform this action."
/// </summary>
/// <remarks>
/// The API answers a wrong username or password with <c>Errors.Auth.InvalidCredentials</c>, an
/// <c>Unauthorized</c> error that <c>ApiControllerBase</c> sends as 403. The Web turned every 403 into
/// the generic permission sentence, so a staff member who mistyped their username read that their
/// account was not allowed in, and asked for it to be fixed. The two-factor step already passed the
/// API's sentence through; the password and passkey steps did not.
/// </remarks>
public sealed class LoginRefusalMessageTests
{
    // The body ApiControllerBase.Problem writes for Errors.Auth.InvalidCredentials.
    private const string InvalidCredentialsBody =
        "{\"type\":\"https://tools.ietf.org/html/rfc9110#section-15.5.4\"," +
        "\"title\":\"Invalid username or password\",\"status\":403," +
        "\"detail\":\"Invalid username or password\",\"code\":\"Auth.InvalidCredentials\"," +
        "\"errorDetails\":[{\"code\":\"Auth.InvalidCredentials\",\"description\":\"Invalid username or password\",\"type\":\"Unauthorized\"}]}";

    private const string PermissionSentence = "You do not have permission to perform this action.";

    [Fact]
    public async Task A_refused_password_sign_in_shows_the_api_reason()
    {
        var service = CreateAuthService(HttpStatusCode.Forbidden, InvalidCredentialsBody);

        var (success, message, _) = await service.LoginAsync("someone", "not-a-real-password");

        Assert.False(success);
        Assert.Equal("Invalid username or password.", message);
    }

    [Fact]
    public async Task A_refused_passkey_sign_in_shows_the_api_reason()
    {
        const string body =
            "{\"title\":\"This passkey is not registered\",\"status\":403," +
            "\"detail\":\"This passkey is not registered\",\"code\":\"Auth.PasskeyLoginFailed\"}";
        var service = CreateAuthService(HttpStatusCode.Forbidden, body);

        var (success, message, _) = await service.CompletePasskeyLoginAsync(
            "session", "{}", "https://localhost", "localhost");

        Assert.False(success);
        Assert.Equal("This passkey is not registered.", message);
    }

    /// <summary>
    /// A 403 with no sentence in it (a proxy, a gate in front of the controller) still must not say
    /// the account lacks a permission: at the sign-in page there is no account yet.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task A_refusal_without_a_reason_does_not_blame_permissions(HttpStatusCode status)
    {
        var service = CreateAuthService(status, body: "");

        var (success, message, _) = await service.LoginAsync("someone", "not-a-real-password");

        Assert.False(success);
        Assert.NotEqual(PermissionSentence, message);
        Assert.Contains("username and password", message);
    }

    private static ShopInventory.Web.Services.AuthService CreateAuthService(HttpStatusCode status, string body)
    {
        var auditContext = new WebClientAuditContext();
        var httpClient = new HttpClient(new FixedResponseHandler(status, body))
        {
            BaseAddress = new Uri("http://localhost:5106/")
        };

        var authStateProvider = new CustomAuthStateProvider(
            StubProxy.Unused<ILocalStorageService>(),
            httpClient,
            NullLogger<CustomAuthStateProvider>.Instance,
            auditContext);

        return new ShopInventory.Web.Services.AuthService(
            httpClient,
            authStateProvider,
            NullLogger<ShopInventory.Web.Services.AuthService>.Instance,
            auditContext);
    }

    private sealed class FixedResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/problem+json"),
                RequestMessage = request
            });
    }
}
