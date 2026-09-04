using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Authentication;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Features.RateLimit.Queries.GetAllRateLimits;
using ShopInventory.Features.RateLimit.Queries.GetRateLimitByClient;
using ShopInventory.Features.RateLimit.Queries.GetCurrentStatus;
using ShopInventory.Features.RateLimit.Queries.CheckRateLimit;
using ShopInventory.Features.RateLimit.Queries.GetBlockedClients;
using ShopInventory.Features.RateLimit.Queries.GetRateLimitStats;
using ShopInventory.Features.RateLimit.Queries.GetRateLimitConfig;
using ShopInventory.Features.RateLimit.Commands.BlockClient;
using ShopInventory.Features.RateLimit.Commands.UnblockClient;
using ShopInventory.Features.RateLimit.Commands.ResetClient;
using ShopInventory.Features.RateLimit.Commands.UpdateRateLimitConfig;
using ShopInventory.Features.RateLimit.Commands.CleanupRateLimits;

namespace ShopInventory.Controllers;

[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
public class RateLimitController(IMediator mediator) : ApiControllerBase
{
    /// <summary>
    /// List all rate limits
    /// </summary>
    [HttpGet]
    [RequirePermission(Permission.EditUsers)]
    public async Task<IActionResult> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] bool? blockedOnly = null,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetAllRateLimitsQuery(page, pageSize, blockedOnly), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Get client's rate limit info
    /// </summary>
    [HttpGet("client/{clientId}")]
    [RequirePermission(Permission.EditUsers)]
    public async Task<IActionResult> GetByClientId(string clientId, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetRateLimitByClientQuery(clientId), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Get current request's rate limit status
    /// </summary>
    [HttpGet("current")]
    public async Task<IActionResult> GetCurrentStatus(CancellationToken cancellationToken)
    {
        var clientId = GetClientIdentifier();
        var result = await mediator.Send(new GetCurrentStatusQuery(clientId), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Check if request would be allowed (non-incrementing)
    /// </summary>
    [HttpGet("check")]
    public async Task<IActionResult> CheckLimit(CancellationToken cancellationToken)
    {
        var clientId = GetClientIdentifier();
        var result = await mediator.Send(new CheckRateLimitQuery(clientId), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Block a client
    /// </summary>
    [HttpPost("block/{clientId}")]
    [RequirePermission(Permission.EditUsers)]
    public async Task<IActionResult> BlockClient(string clientId, [FromBody] BlockClientRequest request, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new BlockClientCommand(clientId, request.DurationMinutes, request.Reason), cancellationToken);
        return result.Match(value => Ok(new { message = value }), errors => Problem(errors));
    }

    /// <summary>
    /// Unblock a rate-limited client
    /// </summary>
    [HttpPost("unblock/{clientId}")]
    [RequirePermission(Permission.EditUsers)]
    public async Task<IActionResult> UnblockClient(string clientId, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new UnblockClientCommand(clientId), cancellationToken);
        return result.Match(value => Ok(new { message = value }), errors => Problem(errors));
    }

    /// <summary>
    /// Reset a client's counters
    /// </summary>
    [HttpPost("reset/{clientId}")]
    [RequirePermission(Permission.EditUsers)]
    public async Task<IActionResult> ResetClient(string clientId, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new ResetClientCommand(clientId), cancellationToken);
        return result.Match(value => Ok(new { message = value }), errors => Problem(errors));
    }

    /// <summary>
    /// Get blocked clients
    /// </summary>
    [HttpGet("blocked")]
    [RequirePermission(Permission.EditUsers)]
    public async Task<IActionResult> GetBlockedClients(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetBlockedClientsQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Get rate limit statistics
    /// </summary>
    [HttpGet("stats")]
    [RequirePermission(Permission.EditUsers)]
    public async Task<IActionResult> GetStats(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetRateLimitStatsQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Get rate limit configuration
    /// </summary>
    [HttpGet("config")]
    [RequirePermission(Permission.EditUsers)]
    public async Task<IActionResult> GetConfig(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetRateLimitConfigQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Update the rate limit configuration
    /// </summary>
    [HttpPut("config")]
    [RequirePermission(Permission.EditUsers)]
    public async Task<IActionResult> UpdateConfig([FromBody] RateLimitConfigDto config, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new UpdateRateLimitConfigCommand(config), cancellationToken);
        return result.Match(value => Ok(new { message = value }), errors => Problem(errors));
    }

    /// <summary>
    /// Clear expired counters
    /// </summary>
    [HttpPost("cleanup")]
    [RequirePermission(Permission.EditUsers)]
    public async Task<IActionResult> Cleanup(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new CleanupRateLimitsCommand(), cancellationToken);
        return result.Match(value => Ok(new { message = $"Cleaned up {value} expired rate limit records" }), errors => Problem(errors));
    }

    private string GetClientIdentifier()
    {
        var apiKey = HttpContext.Request.Headers["X-Api-Key"].FirstOrDefault();
        if (!string.IsNullOrEmpty(apiKey))
            return $"apikey:{apiKey[..Math.Min(8, apiKey.Length)]}";

        return $"ip:{HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
    }
}
