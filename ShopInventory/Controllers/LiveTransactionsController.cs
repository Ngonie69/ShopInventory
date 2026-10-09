using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.DTOs;
using ShopInventory.Features.LiveTransactions.Queries.GetLiveTransactionFeed;

namespace ShopInventory.Controllers;

[Route("api/live-transactions")]
[Authorize(Policy = "ApiAccess")]
[Authorize(Roles = "Admin,Manager")]
public class LiveTransactionsController(IMediator mediator) : ApiControllerBase
{
    /// <summary>
    /// Sales, invoices, payments and fiscal activity at or after <paramref name="since"/>, oldest first
    /// </summary>
    /// <remarks>
    /// Omit <paramref name="since"/> for the start of today (CAT). When <c>hasMore</c> is true, ask again
    /// from the last event's time.
    /// </remarks>
    [HttpGet("feed")]
    [ProducesResponseType(typeof(LiveTransactionFeedDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetFeed(
        [FromQuery] DateTime? since = null,
        [FromQuery] int limit = GetLiveTransactionFeedQuery.DefaultLimit,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetLiveTransactionFeedQuery(since, limit), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }
}
