using System.Security.Claims;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.DTOs;
using ShopInventory.Features.Password.Commands.ChangePassword;
using ShopInventory.Features.Password.Commands.CompleteReset;
using ShopInventory.Features.Password.Commands.RequestReset;
using ShopInventory.Features.Password.Commands.UpdateCredentials;
using ShopInventory.Features.Password.Queries.GetCredentials;
using ShopInventory.Features.Password.Queries.ValidateToken;

namespace ShopInventory.Controllers;

/// <summary>
/// Controller for password management operations
/// </summary>
[Route("api/[controller]")]
[Produces("application/json")]
public class PasswordController(IMediator mediator) : ApiControllerBase
{
    /// <summary>
    /// Request a password reset email
    /// </summary>
    [HttpPost("reset/request")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> RequestReset([FromBody] PasswordResetRequest request, CancellationToken cancellationToken)
    {
        var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var result = await mediator.Send(new RequestResetCommand(request.Email, clientIp), cancellationToken);
        return result.Match(value => Ok(new { message = value }), errors => Problem(errors));
    }

    /// <summary>
    /// Validate a password reset token
    /// </summary>
    [HttpGet("reset/validate")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ValidateToken([FromQuery] string token, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new ValidateTokenQuery(token), cancellationToken);
        return result.Match(
            value => Ok(new { message = "Token is valid", isValid = true }),
            errors => Problem(errors));
    }

    /// <summary>
    /// Complete the password reset
    /// </summary>
    [HttpPost("reset/complete")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CompleteReset([FromBody] PasswordResetCompleteRequest request, CancellationToken cancellationToken)
    {
        var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var result = await mediator.Send(new CompleteResetCommand(
            request.Token,
            request.NewPassword,
            request.NewPassword,
            clientIp), cancellationToken);
        return result.Match(value => Ok(new { message = value }), errors => Problem(errors));
    }

    /// <summary>
    /// Change password for logged-in user
    /// </summary>
    [HttpPost("change")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        var result = await mediator.Send(new ChangePasswordCommand(
            userId,
            request.Username,
            request.CurrentPassword,
            request.NewPassword,
            request.NewPassword), cancellationToken);
        return result.Match(value => Ok(new { message = value }), errors => Problem(errors));
    }

    /// <summary>
    /// Get current credentials for logged-in user
    /// </summary>
    [HttpGet("credentials")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetCredentials(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var result = await mediator.Send(new GetCredentialsQuery(userId.Value), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Update login credentials (username/email) for logged-in user
    /// </summary>
    [HttpPut("credentials")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateCredentials([FromBody] UpdateCredentialsRequest request, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var result = await mediator.Send(new UpdateCredentialsCommand(userId.Value, request), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    private Guid? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (Guid.TryParse(userIdClaim, out var userId))
            return userId;
        return null;
    }
}
