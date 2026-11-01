using System.Net;
using System.Text;
using Blazored.LocalStorage;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The Web's sign-in page shows why the API refused a two-factor code.
/// </summary>
/// <remarks>
/// The API refuses this step with a 403 whose <c>detail</c> says what went wrong. The Web's
/// <c>ApiErrorResponse.GetFriendlyMessage</c> replaces any 403 with "You do not have permission to
/// perform this action.", which on a sign-in page reads as though the account had been barred.
/// </remarks>
public sealed class WebTwoFactorRefusalMessageTests
{
    // The shape ApiControllerBase writes, as captured from production on 2026-10-31, with the
    // detail the API now sends for a wrong code.
    private const string WrongCodeBody = """
        {
          "type": "https://tools.ietf.org/html/rfc9110#section-15.5.4",
          "title": "That code is not correct. Enter the code your authenticator app shows now, and check the phone's clock is set automatically.",
          "status": 403,
          "detail": "That code is not correct. Enter the code your authenticator app shows now, and check the phone's clock is set automatically.",
          "instance": "/api/Auth/login/two-factor",
          "code": "Auth.TwoFactorCodeInvalid",
          "errorDetails": [
            {
              "code": "Auth.TwoFactorCodeInvalid",
              "description": "That code is not correct. Enter the code your authenticator app shows now, and check the phone's clock is set automatically.",
              "type": "Unauthorized"
            }
          ],
          "traceId": "00-df0ceb05044c71b3d5bfb7697cb5267e-34d36705cf0489d5-00"
        }
        """;

    [Fact]
    public async Task A_refused_code_shows_the_APIs_reason()
    {
        var service = CreateService(HttpStatusCode.Forbidden, WrongCodeBody);

        var (success, message, _) = await service.CompleteTwoFactorAsync("challenge", "123456", isBackupCode: false);

        Assert.False(success);
        Assert.Equal(
            "That code is not correct. Enter the code your authenticator app shows now, and check the phone's clock is set automatically.",
            message);
    }

    [Theory]
    [InlineData("""{"title":"Forbidden","status":403}""")]
    [InlineData("")]
    public async Task A_refusal_with_no_reason_still_reads_as_a_code_problem(string body)
    {
        var service = CreateService(HttpStatusCode.Forbidden, body);

        var (success, message, _) = await service.CompleteTwoFactorAsync("challenge", "123456", isBackupCode: false);

        Assert.False(success);
        Assert.DoesNotContain("permission", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{", message);
        Assert.Contains("code", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_body_without_a_message_gives_no_message_rather_than_its_JSON()
    {
        Assert.Null(ApiErrorResponse.TryGetUserMessage("""{"title":"Forbidden","status":403}"""));
        Assert.Null(ApiErrorResponse.TryGetUserMessage(null));
    }

    private static ShopInventory.Web.Services.AuthService CreateService(HttpStatusCode status, string body)
    {
        var auditContext = new WebClientAuditContext();
        var httpClient = new HttpClient(new FixedAnswer(status, body)) { BaseAddress = new Uri("http://localhost:5106/") };
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

    private sealed class FixedAnswer(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/problem+json"),
                RequestMessage = request
            });
    }
}
