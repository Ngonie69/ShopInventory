using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.DTOs;
using ShopInventory.Features.Sync.Commands.CancelTransaction;
using ShopInventory.Features.Sync.Commands.ClearTransferRequestItems;
using ShopInventory.Features.Sync.Commands.ProcessQueue;
using ShopInventory.Features.Sync.Commands.RetryTransaction;
using ShopInventory.Features.Sync.Commands.SyncItemTaxGroups;
using ShopInventory.Features.Sync.Commands.TestConnection;
using ShopInventory.Features.Sync.Commands.WarmItemUoms;
using ShopInventory.Middleware;
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
    /// Sync item tax groups from SAP (admin only)
    /// </summary>
    /// <remarks>
    /// Copies every item's VAT group from the SAP item master into the table till sales are taxed
    /// from. Web → Settings → Data Sync is what calls this; nothing runs it on a schedule, so the table
    /// is as fresh as the last time an admin ran it. Tills re-read it within four hours, or at once
    /// when Refresh is pressed on the till. Sales already recorded keep the tax code they were made under.
    /// </remarks>
    [HttpPost("item-tax-groups")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(typeof(ItemTaxGroupSyncResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SyncItemTaxGroups(CancellationToken cancellationToken)
    {
        using var syncTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        syncTimeout.CancelAfter(TimeSpan.FromMinutes(10));
        var result = await mediator.Send(new SyncItemTaxGroupsCommand(), syncTimeout.Token);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Resolve the SAP unit of measure for the item / UoM pairs orders use (admin only)
    /// </summary>
    /// <remarks>
    /// Stores each pair's canonical SAP UoM so an order approval finds it rather than resolving it
    /// against SAP while a rep waits. Pairs already stored cost nothing. Web → Settings → Data Sync is
    /// what calls this; nothing runs it on a schedule. Background SAP priority, because the point of it
    /// is to keep approvals fast, and holding their reserved slots would do the opposite.
    /// </remarks>
    [HttpPost("item-uoms")]
    [SapBackgroundWork]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(typeof(ItemUomWarmResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> WarmItemUoms(CancellationToken cancellationToken)
    {
        using var syncTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        syncTimeout.CancelAfter(TimeSpan.FromMinutes(20));
        var result = await mediator.Send(new WarmItemUomsCommand(), syncTimeout.Token);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Make tills re-read their transfer-request item list from SAP (admin only)
    /// </summary>
    /// <remarks>
    /// That list (<c>DesktopIntegration/transfer-requests/items</c>) is held for an hour. This drops the
    /// hold, so an item just flagged <c>U_SalesItem</c> and <c>U_VanSale</c> in SAP shows the next time a
    /// till opens its request screen. Sent by the Products sync in Settings.
    /// </remarks>
    [HttpPost("transfer-request-items/clear")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ClearTransferRequestItems(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new ClearTransferRequestItemsCommand(), cancellationToken);
        return result.Match(_ => NoContent(), errors => Problem(errors));
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
