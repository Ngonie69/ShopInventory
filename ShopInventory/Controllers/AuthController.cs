using System.Security.Claims;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ShopInventory.DTOs;
using ShopInventory.Features.Auth.Commands.BeginPasskeyLogin;
using ShopInventory.Features.Auth.Commands.BeginPasskeyRegistration;
using ShopInventory.Features.Auth.Commands.CompletePasskeyLogin;
using ShopInventory.Features.Auth.Commands.CompletePasskeyRegistration;
using ShopInventory.Features.Auth.Commands.CompleteTwoFactorLogin;
using ShopInventory.Features.Auth.Commands.Login;
using ShopInventory.Features.Auth.Commands.Logout;
using ShopInventory.Features.Auth.Commands.RefreshToken;
using ShopInventory.Features.Auth.Commands.Register;
using ShopInventory.Features.Auth.Queries.GetCurrentUser;
using ShopInventory.Features.Auth.Queries.GetPasskeys;

namespace ShopInventory.Controllers;

[Route("api/[controller]")]
public class AuthController(IMediator mediator) : ApiControllerBase
{
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Login([FromBody] AuthLoginRequest request, CancellationToken cancellationToken)
    {
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var result = await mediator.Send(new LoginCommand(request.Username, request.Password, ipAddress), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Step 2 of the login flow when 2FA is enabled.
    /// Exchange the short-lived challenge token + TOTP/backup code for a full JWT.
    /// </summary>
    [HttpPost("login/two-factor")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> CompleteTwoFactorLogin(
        [FromBody] TwoFactorChallengeRequest request, CancellationToken cancellationToken)
    {
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var result = await mediator.Send(
            new CompleteTwoFactorLoginCommand(request.TwoFactorToken, request.Code, request.IsBackupCode, ipAddress),
            cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("passkeys")]
    [Authorize]
    public async Task<IActionResult> GetPasskeys(CancellationToken cancellationToken)
    {
        var userId = GetAuthenticatedUserId();
        if (userId is null)
            return Unauthorized();

        var result = await mediator.Send(new GetPasskeysQuery(userId.Value), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPost("passkeys/register/options")]
    [Authorize]
    public async Task<IActionResult> BeginPasskeyRegistration(
        [FromBody] PasskeyRegistrationOptionsRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetAuthenticatedUserId();
        if (userId is null)
            return Unauthorized();

        var result = await mediator.Send(
            new BeginPasskeyRegistrationCommand(userId.Value, request.FriendlyName, request.Origin, request.RpId),
            cancellationToken);

        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPost("passkeys/register/complete")]
    [Authorize]
    public async Task<IActionResult> CompletePasskeyRegistration(
        [FromBody] PasskeyRegistrationCompleteRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetAuthenticatedUserId();
        if (userId is null)
            return Unauthorized();

        var result = await mediator.Send(
            new CompletePasskeyRegistrationCommand(userId.Value, request.SessionToken, request.CredentialJson, request.Origin, request.RpId),
            cancellationToken);

        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPost("passkeys/login/options")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> BeginPasskeyLogin(
        [FromBody] PasskeyAssertionOptionsRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new BeginPasskeyLoginCommand(request.Origin, request.RpId), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPost("passkeys/login/complete")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> CompletePasskeyLogin(
        [FromBody] PasskeyAssertionCompleteRequest request,
        CancellationToken cancellationToken)
    {
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var result = await mediator.Send(
            new CompletePasskeyLoginCommand(request.SessionToken, request.CredentialJson, request.Origin, request.RpId, ipAddress),
            cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> RefreshToken([FromBody] RefreshTokenRequest request, CancellationToken cancellationToken)
    {
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var result = await mediator.Send(new RefreshTokenCommand(request.RefreshToken, ipAddress), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout([FromBody] RefreshTokenRequest request, CancellationToken cancellationToken)
    {
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var result = await mediator.Send(new LogoutCommand(request.RefreshToken, ipAddress), cancellationToken);
        return result.Match(_ => NoContent(), errors => Problem(errors));
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> GetCurrentUser(CancellationToken cancellationToken)
    {
        var username = User.Identity?.Name;
        if (string.IsNullOrEmpty(username))
            return Unauthorized();

        var result = await mediator.Send(new GetCurrentUserQuery(username), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPost("register")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Register([FromBody] RegisterUserRequest request, CancellationToken cancellationToken)
    {
        var adminUsername = User.Identity?.Name ?? "Unknown";
        var result = await mediator.Send(
            new RegisterCommand(request.Username, request.Email, request.Password, request.Role, adminUsername),
            cancellationToken);
        return result.Match(
            value => CreatedAtAction(nameof(GetCurrentUser), value),
            errors => Problem(errors));
    }

    private Guid? GetAuthenticatedUserId()
    {
        return Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            ? userId
            : null;
    }
}
