using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Services;
using System.Security.Claims;
using System.Text.Encodings.Web;

namespace ShopInventory.Authentication;

/// <summary>
/// API Key authentication handler for service-to-service communication
/// </summary>
public class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
{
    private const string ApiKeyHeaderName = "X-API-Key";
    private readonly IAuthService _authService;
    private readonly ILogger<ApiKeyAuthenticationHandler> _logger;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IAuthService authService)
        : base(options, logger, encoder)
    {
        _authService = authService;
        _logger = logger.CreateLogger<ApiKeyAuthenticationHandler>();
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Check if API key header exists
        if (!Request.Headers.TryGetValue(ApiKeyHeaderName, out var apiKeyHeaderValues))
        {
            return AuthenticateResult.NoResult();
        }

        var apiKey = apiKeyHeaderValues.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return AuthenticateResult.NoResult();
        }

        // A key never stands in for a user token that was sent and did not validate. The request is
        // then the user's, not the key's, and a user whose token failed is unauthenticated: the caller
        // gets the 401 that tells it to renew the token (the Web's ApiTokenAuthentication waits on it).
        if (await BearerTokenFailedAsync())
        {
            return AuthenticateResult.Fail("A bearer token was sent and did not validate. The API key is not counted in its place.");
        }

        // Validate API key
        var keyConfig = _authService.ValidateApiKey(apiKey);
        if (keyConfig == null)
        {
            _logger.LogWarning("Invalid API key attempt from IP: {IpAddress}",
                Context.Connection.RemoteIpAddress);
            return AuthenticateResult.Fail("Invalid API key");
        }

        // Create claims for API key authentication
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, keyConfig.Name),
            new(ClaimTypes.AuthenticationMethod, "ApiKey"),
            new("api_key_name", keyConfig.Name)
        };

        // Add roles
        foreach (var role in keyConfig.Roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        // Debug: a valid API key is the normal case on every single integration request — 718 lines
        // of one production day, 9% of the file, all saying the same key worked again. The failures
        // below are what an operator needs, and they stay where they are.
        _logger.LogDebug("API key authentication successful for: {KeyName}", keyConfig.Name);

        return AuthenticateResult.Success(ticket);
    }

    /// <summary>
    /// Whether the request carried a bearer token that the JWT bearer scheme refused: expired, signed
    /// with another secret, or not a JWT at all. A request with no token answers false.
    /// </summary>
    /// <remarks>
    /// Asks the bearer scheme itself rather than reading the Authorization header, so a hub's query-string
    /// token counts as well (see HubAccessToken). The bearer handler is per request and caches its result,
    /// so this does not validate the token a second time.
    /// </remarks>
    private async Task<bool> BearerTokenFailedAsync()
    {
        var schemes = Context.RequestServices.GetRequiredService<IAuthenticationSchemeProvider>();
        if (await schemes.GetSchemeAsync(AuthenticationSchemes.Jwt) is null)
        {
            return false;
        }

        var bearer = await Context.AuthenticateAsync(AuthenticationSchemes.Jwt);
        return bearer.Failure is not null;
    }

}

/// <summary>
/// Options for API Key authentication
/// </summary>
public class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
}

/// <summary>
/// Authentication scheme constants
/// </summary>
public static class AuthenticationSchemes
{
    public const string ApiKey = "ApiKey";
    public const string Jwt = "Bearer";
}
