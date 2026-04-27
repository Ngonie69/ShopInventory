using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

public sealed class ApiBearerAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IHttpClientFactory httpClientFactory) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiBearer";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!TryGetBearerToken(Request.Headers.Authorization.ToString(), out var token))
        {
            return AuthenticateResult.NoResult();
        }

        try
        {
            var client = httpClientFactory.CreateClient("ShopInventoryApiUser");
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/auth/me");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await client.SendAsync(request, Context.RequestAborted);
            if (!response.IsSuccessStatusCode)
            {
                return AuthenticateResult.Fail($"API bearer token rejected with status {(int)response.StatusCode}");
            }

            var user = await response.Content.ReadFromJsonAsync<UserInfo>(Context.RequestAborted);
            if (user == null || string.IsNullOrWhiteSpace(user.Username) || string.IsNullOrWhiteSpace(user.Role))
            {
                return AuthenticateResult.Fail("API bearer token did not resolve to a valid user");
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, user.Username),
                new(ClaimTypes.Role, user.Role)
            };

            if (!string.IsNullOrWhiteSpace(user.Email))
            {
                claims.Add(new Claim(ClaimTypes.Email, user.Email));
            }

            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
        }
        catch (OperationCanceledException) when (Context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "API bearer token validation failed");
            return AuthenticateResult.Fail("API bearer token validation failed");
        }
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    private static bool TryGetBearerToken(string authorization, out string token)
    {
        token = string.Empty;
        if (string.IsNullOrWhiteSpace(authorization) ||
            !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        token = NormalizeToken(authorization["Bearer ".Length..]);
        return !string.IsNullOrWhiteSpace(token);
    }

    private static string NormalizeToken(string token)
    {
        token = token.Trim();
        if (token.Length >= 2 && token[0] == '"' && token[^1] == '"')
        {
            try
            {
                return JsonSerializer.Deserialize<string>(token)?.Trim() ?? string.Empty;
            }
            catch (JsonException)
            {
                return token.Trim('"').Trim();
            }
        }

        return token;
    }
}