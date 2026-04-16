using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ShopInventory.DTOs;
using ShopInventory.Features.Auth.Commands.Login;
using ShopInventory.Features.Auth.Commands.Logout;
using ShopInventory.Features.Auth.Commands.RefreshToken;
using ShopInventory.Features.Auth.Commands.Register;
using ShopInventory.Features.Auth.Queries.GetCurrentUser;

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
}
