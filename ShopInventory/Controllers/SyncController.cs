using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.DTOs;
using ShopInventory.Features.Sync.Commands.CancelTransaction;
using ShopInventory.Features.Sync.Commands.ProcessQueue;
using ShopInventory.Features.Sync.Commands.RetryTransaction;
using ShopInventory.Features.Sync.Commands.TestConnection;
using ShopInventory.Features.Sync.Queries.CheckSapConnection;
using ShopInventory.Features.Sync.Queries.GetCacheStatus;
using ShopInventory.Features.Sync.Queries.GetConnectionLogs;
using ShopInventory.Features.Sync.Queries.GetHealthSummary;
using ShopInventory.Features.Sync.Queries.GetQueuedItems;
using ShopInventory.Features.Sync.Queries.GetQueueStatus;
using ShopInventory.Features.Sync.Queries.GetSyncStatus;

namespace ShopInventory.Controllers;

/// <summary>
/// Controller for sync status and offline queue
/// </summary>
[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
public class SyncController(IMediator mediator) : ApiControllerBase
{
    /// <summary>
    /// Get sync status dashboard
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(SyncStatusDashboardDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSyncStatus(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetSyncStatusQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Check SAP connection
    /// </summary>
    [HttpGet("sap-connection")]
    [ProducesResponseType(typeof(SapConnectionStatusDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> CheckSapConnection(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new CheckSapConnectionQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Get health summary
    /// </summary>
    [HttpGet("health")]
    [ProducesResponseType(typeof(SyncHealthSummaryDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetHealthSummary(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetHealthSummaryQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Get offline queue status
    /// </summary>
    [HttpGet("queue")]
    [HttpGet("queue/status")]
    [ProducesResponseType(typeof(OfflineQueueStatusDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetQueueStatus(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetQueueStatusQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Get queued transaction items
    /// </summary>
    [HttpGet("queue/items")]
    [ProducesResponseType(typeof(List<QueuedTransactionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetQueuedItems(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetQueuedItemsQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Get cache sync status
    /// </summary>
    [HttpGet("cache-status")]
    [ProducesResponseType(typeof(List<CacheSyncStatusDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCacheStatus(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetCacheStatusQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Get connection logs
    /// </summary>
    [HttpGet("logs")]
    [ProducesResponseType(typeof(List<ConnectionLogDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetConnectionLogs([FromQuery] int count = 50, CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetConnectionLogsQuery(count), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Test SAP connection
    /// </summary>
    [HttpPost("test-connection")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<IActionResult> TestConnection(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new TestConnectionCommand(), cancellationToken);
        return result.Match(value => Ok(new { value.IsConnected, value.Message }), errors => Problem(errors));
    }

    /// <summary>
    /// Retry a failed transaction
    /// </summary>
    [HttpPost("queue/{id}/retry")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RetryTransaction(int id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new RetryTransactionCommand(id), cancellationToken);
        return result.Match(_ => Ok(new { Message = "Transaction retry initiated" }), errors => Problem(errors));
    }

    /// <summary>
    /// Cancel a pending transaction
    /// </summary>
    [HttpPost("queue/{id}/cancel")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CancelTransaction(int id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new CancelTransactionCommand(id), cancellationToken);
        return result.Match(_ => Ok(new { Message = "Transaction cancelled" }), errors => Problem(errors));
    }

    /// <summary>
    /// Process pending queue items (admin only)
    /// </summary>
    [HttpPost("queue/process")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ProcessQueue(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new ProcessQueueCommand(), cancellationToken);
        return result.Match(_ => Ok(new { Message = "Queue processing initiated" }), errors => Problem(errors));
    }
}
