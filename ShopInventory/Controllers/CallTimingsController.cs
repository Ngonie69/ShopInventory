using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Features.CallTimings.Queries.GetCallTimings;

namespace ShopInventory.Controllers;

/// <summary>
/// How long this API's own requests and its SAP Service Layer calls have been taking, from memory.
/// </summary>
/// <remarks>
/// Admin only: watching the system is administering it. Nothing behind it touches SAP or the database.
/// </remarks>
[Route("api/call-timings")]
[Authorize(Policy = "AdminOnly")]
public class CallTimingsController(IMediator mediator) : ApiControllerBase
{
    /// <summary>
    /// The slowest SAP calls, p50/p95 per read family, SAP calls in flight, slot waits and the busiest
    /// inbound routes, over the last 15 minutes, the last hour or today (since midnight CAT).
    /// </summary>
    /// <param name="window"><c>15m</c>, <c>1h</c> or <c>today</c>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    [HttpGet]
    [ProducesResponseType(typeof(GetCallTimingsResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(
        [FromQuery] string window = CallTimingWindows.Today,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetCallTimingsQuery(window), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }
}
