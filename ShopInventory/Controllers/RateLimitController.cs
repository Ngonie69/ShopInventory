using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Authentication;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Controllers;

/// <summary>
/// Controller for API Rate Limiting management
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
[Produces("application/json")]
public class RateLimitController : ControllerBase
{
    private readonly IRateLimitService _rateLimitService;
    private readonly ILogger<RateLimitController> _logger;

    public RateLimitController(IRateLimitService rateLimitService, ILogger<RateLimitController> logger)
    {
        _rateLimitService = rateLimitService;
        _logger = logger;
    }

    /// <summary>
    /// Get all rate limit records with pagination
    /// </summary>
    [HttpGet]
    [RequirePermission(Permission.EditUsers)]
    [ProducesResponseType(typeof(RateLimitListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] bool? blockedOnly = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _rateLimitService.GetAllAsync(page, pageSize, blockedOnly, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Get rate limit status for a specific client
    /// </summary>
    [HttpGet("client/{clientId}")]
    [RequirePermission(Permission.EditUsers)]
    [ProducesResponseType(typeof(ApiRateLimitDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByClientId(string clientId, CancellationToken cancellationToken)
    {
        var rateLimit = await _rateLimitService.GetByClientIdAsync(clientId, cancellationToken);
        if (rateLimit == null)
            return NotFound(new { message = $"Rate limit record for client '{clientId}' not found" });

        return Ok(rateLimit);
    }

    /// <summary>
    /// Get rate limit for current request
    /// </summary>
    [HttpGet("current")]
    [ProducesResponseType(typeof(RateLimitStatusDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCurrentStatus(CancellationToken cancellationToken)
    {
        var clientId = GetClientIdentifier();
        var status = await _rateLimitService.GetRateLimitStatusAsync(clientId, cancellationToken);
        return Ok(status);
    }

    /// <summary>
    /// Check if a request is allowed (does not increment counter)
    /// </summary>
    [HttpGet("check")]
    [ProducesResponseType(typeof(RateLimitCheckResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> CheckLimit(CancellationToken cancellationToken)
    {
        var clientId = GetClientIdentifier();
        var isAllowed = await _rateLimitService.IsRequestAllowedAsync(clientId, cancellationToken);
        var status = await _rateLimitService.GetRateLimitStatusAsync(clientId, cancellationToken);
        
        return Ok(new RateLimitCheckResultDto
        {
            IsAllowed = isAllowed,
            CurrentRequests = status.RequestsInWindow,
            MaxRequests = status.MaxRequests,
            WindowSizeSeconds = status.WindowSizeSeconds,
            WindowResetAt = status.WindowResetAt,
            IsBlocked = status.IsBlocked,
            BlockedUntil = status.BlockedUntil
        });
    }

    /// <summary>
    /// Block a client
    /// </summary>
    [HttpPost("block/{clientId}")]
    [RequirePermission(Permission.EditUsers)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> BlockClient(string clientId, [FromBody] BlockClientRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await _rateLimitService.BlockClientAsync(clientId, request.DurationMinutes, request.Reason, cancellationToken);
            return Ok(new { message = $"Client '{clientId}' blocked for {request.DurationMinutes} minutes" });
        }
        catch (Exception ex)
        {
            return BadRequest(new ErrorResponseDto { Message = ex.Message });
        }
    }

    /// <summary>
    /// Unblock a client
    /// </summary>
    [HttpPost("unblock/{clientId}")]
    [RequirePermission(Permission.EditUsers)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UnblockClient(string clientId, CancellationToken cancellationToken)
    {
        var success = await _rateLimitService.UnblockClientAsync(clientId, cancellationToken);
        if (!success)
            return NotFound(new { message = $"Client '{clientId}' not found or not blocked" });

        return Ok(new { message = $"Client '{clientId}' unblocked successfully" });
    }

    /// <summary>
    /// Reset rate limit counters for a client
    /// </summary>
    [HttpPost("reset/{clientId}")]
    [RequirePermission(Permission.EditUsers)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ResetClient(string clientId, CancellationToken cancellationToken)
    {
        var success = await _rateLimitService.ResetClientAsync(clientId, cancellationToken);
        if (!success)
            return NotFound(new { message = $"Client '{clientId}' not found" });

        return Ok(new { message = $"Rate limit counters for client '{clientId}' reset successfully" });
    }

    /// <summary>
    /// Get blocked clients
    /// </summary>
    [HttpGet("blocked")]
    [RequirePermission(Permission.EditUsers)]
    [ProducesResponseType(typeof(List<ApiRateLimitDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetBlockedClients(CancellationToken cancellationToken)
    {
        var blocked = await _rateLimitService.GetBlockedClientsAsync(cancellationToken);
        return Ok(blocked);
    }

    /// <summary>
    /// Get rate limit statistics
    /// </summary>
    [HttpGet("stats")]
    [RequirePermission(Permission.EditUsers)]
    [ProducesResponseType(typeof(RateLimitStatsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStats(CancellationToken cancellationToken)
    {
        var stats = await _rateLimitService.GetStatsAsync(cancellationToken);
        return Ok(stats);
    }

    /// <summary>
    /// Get rate limit configuration
    /// </summary>
    [HttpGet("config")]
    [RequirePermission(Permission.EditUsers)]
    [ProducesResponseType(typeof(RateLimitConfigDto), StatusCodes.Status200OK)]
    public IActionResult GetConfig()
    {
        var config = _rateLimitService.GetConfiguration();
        return Ok(config);
    }

    /// <summary>
    /// Update rate limit configuration
    /// </summary>
    [HttpPut("config")]
    [RequirePermission(Permission.EditUsers)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateConfig([FromBody] RateLimitConfigDto config, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            await _rateLimitService.UpdateConfigurationAsync(config, cancellationToken);
            return Ok(new { message = "Rate limit configuration updated successfully" });
        }
        catch (Exception ex)
        {
            return BadRequest(new ErrorResponseDto { Message = ex.Message });
        }
    }

    /// <summary>
    /// Cleanup expired rate limit records
    /// </summary>
    [HttpPost("cleanup")]
    [RequirePermission(Permission.EditUsers)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Cleanup(CancellationToken cancellationToken)
    {
        var cleaned = await _rateLimitService.CleanupExpiredAsync(cancellationToken);
        return Ok(new { message = $"Cleaned up {cleaned} expired rate limit records" });
    }

    private string GetClientIdentifier()
    {
        // Try to get from header first (for API key auth)
        var apiKey = HttpContext.Request.Headers["X-Api-Key"].FirstOrDefault();
        if (!string.IsNullOrEmpty(apiKey))
            return $"apikey:{apiKey[..Math.Min(8, apiKey.Length)]}";

        // Fall back to IP address
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var forwarded = HttpContext.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(forwarded))
            ip = forwarded.Split(',')[0].Trim();

        return $"ip:{ip}";
    }
}

public class BlockClientRequest
{
    public int DurationMinutes { get; set; } = 60;
    public string? Reason { get; set; }
}

public class RateLimitCheckResultDto
{
    public bool IsAllowed { get; set; }
    public int CurrentRequests { get; set; }
    public int MaxRequests { get; set; }
    public int WindowSizeSeconds { get; set; }
    public DateTime WindowResetAt { get; set; }
    public bool IsBlocked { get; set; }
    public DateTime? BlockedUntil { get; set; }
}
