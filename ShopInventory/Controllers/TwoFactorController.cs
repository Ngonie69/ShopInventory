using System.Security.Claims;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.DTOs;
using ShopInventory.Features.TwoFactor.Commands.DisableTwoFactor;
using ShopInventory.Features.TwoFactor.Commands.EnableTwoFactor;
using ShopInventory.Features.TwoFactor.Commands.InitiateTwoFactorSetup;
using ShopInventory.Features.TwoFactor.Commands.RegenerateBackupCodes;
using ShopInventory.Features.TwoFactor.Commands.VerifyTwoFactorCode;
using ShopInventory.Features.TwoFactor.Queries.GetTwoFactorStatus;

namespace ShopInventory.Controllers;

/// <summary>
/// Controller for Two-Factor Authentication operations
/// </summary>
[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
[Produces("application/json")]
public class TwoFactorController(IMediator mediator) : ApiControllerBase
{
    /// <summary>
    /// Get 2FA status for the current user
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(TwoFactorStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetStatus(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var result = await mediator.Send(new GetTwoFactorStatusQuery(userId.Value), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Initiate 2FA setup - generates secret key and QR code
    /// </summary>
    [HttpPost("setup")]
    [ProducesResponseType(typeof(TwoFactorSetupResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> InitiateSetup(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var result = await mediator.Send(new InitiateTwoFactorSetupCommand(userId.Value), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Enable 2FA after verifying the setup code
    /// </summary>
    [HttpPost("enable")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> EnableTwoFactor([FromBody] TwoFactorEnableRequest request, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var result = await mediator.Send(new EnableTwoFactorCommand(request.Code, userId.Value), cancellationToken);
        return result.Match(value => Ok(new { message = value }), errors => Problem(errors));
    }

    /// <summary>
    /// Disable 2FA for the current user
    /// </summary>
    [HttpPost("disable")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DisableTwoFactor([FromBody] TwoFactorDisableRequest request, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var result = await mediator.Send(new DisableTwoFactorCommand(request.Password, request.Code, userId.Value), cancellationToken);
        return result.Match(value => Ok(new { message = value }), errors => Problem(errors));
    }

    /// <summary>
    /// Verify a 2FA code (used during login flow)
    /// </summary>
    [HttpPost("verify")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> VerifyCode([FromBody] TwoFactorVerifyRequest request, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var result = await mediator.Send(new VerifyTwoFactorCodeCommand(request.Code, request.IsBackupCode, userId.Value), cancellationToken);
        return result.Match(value => Ok(new { message = value }), errors => Problem(errors));
    }

    /// <summary>
    /// Regenerate backup codes
    /// </summary>
    [HttpPost("backup-codes/regenerate")]
    [ProducesResponseType(typeof(List<string>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RegenerateBackupCodes([FromBody] TwoFactorEnableRequest request, CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var result = await mediator.Send(new RegenerateBackupCodesCommand(request.Code, userId.Value), cancellationToken);
        return result.Match(value => Ok(new { backupCodes = value }), errors => Problem(errors));
    }

    private Guid? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (Guid.TryParse(userIdClaim, out var userId))
            return userId;
        return null;
    }
}
