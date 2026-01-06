using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Controllers;

/// <summary>
/// Controller for password management operations
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class PasswordController : ControllerBase
{
    private readonly IPasswordResetService _passwordResetService;
    private readonly ILogger<PasswordController> _logger;

    public PasswordController(
        IPasswordResetService passwordResetService,
        ILogger<PasswordController> logger)
    {
        _passwordResetService = passwordResetService;
        _logger = logger;
    }

    /// <summary>
    /// Request a password reset email
    /// </summary>
    /// <param name="request">Email address</param>
    /// <returns>Success message (always returns success to prevent email enumeration)</returns>
    [HttpPost("reset/request")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> RequestReset([FromBody] PasswordResetRequest request)
    {
        var ipAddress = GetClientIpAddress();
        var result = await _passwordResetService.InitiateResetAsync(request.Email, ipAddress);

        // Always return success to prevent email enumeration
        return Ok(new { message = result.Message });
    }

    /// <summary>
    /// Validate a password reset token
    /// </summary>
    /// <param name="token">Reset token from email</param>
    /// <returns>Validation result</returns>
    [HttpGet("reset/validate")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ValidateToken([FromQuery] string token)
    {
        var result = await _passwordResetService.ValidateTokenAsync(token);
        if (!result.IsSuccess)
        {
            return BadRequest(new { message = result.Message, isValid = false });
        }

        return Ok(new { message = result.Message, isValid = true });
    }

    /// <summary>
    /// Complete the password reset
    /// </summary>
    /// <param name="request">Reset token and new password</param>
    /// <returns>Success status</returns>
    [HttpPost("reset/complete")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CompleteReset([FromBody] PasswordResetCompleteRequest request)
    {
        var ipAddress = GetClientIpAddress();
        var result = await _passwordResetService.CompleteResetAsync(request.Token, request.NewPassword, ipAddress);

        if (!result.IsSuccess)
        {
            return BadRequest(new { message = result.Message, errors = result.Errors });
        }

        return Ok(new { message = result.Message });
    }

    /// <summary>
    /// Change password for logged-in user
    /// </summary>
    /// <param name="request">Current and new password</param>
    /// <returns>Success status</returns>
    [HttpPost("change")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
        {
            return Unauthorized();
        }

        var result = await _passwordResetService.ChangePasswordAsync(userId.Value, request.CurrentPassword, request.NewPassword);

        if (!result.IsSuccess)
        {
            return BadRequest(new { message = result.Message, errors = result.Errors });
        }

        return Ok(new { message = result.Message });
    }

    private Guid? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (Guid.TryParse(userIdClaim, out var userId))
        {
            return userId;
        }
        return null;
    }

    private string GetClientIpAddress()
    {
        // Check for forwarded IP (behind proxy/load balancer)
        var forwardedFor = HttpContext.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(forwardedFor))
        {
            return forwardedFor.Split(',').First().Trim();
        }

        return HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
