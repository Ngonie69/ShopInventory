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

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Check if API key header exists
        if (!Request.Headers.TryGetValue(ApiKeyHeaderName, out var apiKeyHeaderValues))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var apiKey = apiKeyHeaderValues.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        // Validate API key
        var keyConfig = _authService.ValidateApiKey(apiKey);
        if (keyConfig == null)
        {
            _logger.LogWarning("Invalid API key attempt from IP: {IpAddress}",
                Context.Connection.RemoteIpAddress);
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key"));
        }

        // Create claims
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

        _logger.LogInformation("API key authentication successful for: {KeyName}", keyConfig.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
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
