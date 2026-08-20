using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Authentication;
using ShopInventory.DTOs;
using ShopInventory.Features.CreditControl.Queries.GetCreditHeadroom;
using ShopInventory.Features.CreditControl.Queries.GetCreditLimitReview;
using ShopInventory.Models;

namespace ShopInventory.Controllers;

[Route("api/credit-control")]
[Authorize(Policy = "ApiAccess")]
public class CreditControlController(ISender mediator) : ApiControllerBase
{
    /// <summary>
    /// Customers and consolidated groups currently over their SAP credit limit — the accounts whose
    /// sales orders will be refused at capture. Same finding as the evening review notification,
    /// available on demand and in full.
    /// </summary>
    /// <param name="refresh">
    /// Re-reads SAP instead of serving the cached result. Use after taking a payment, to confirm
    /// the account is back under its limit.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("over-limit")]
    [RequirePermission(Permission.ViewCustomers)]
    [ProducesResponseType(typeof(CreditLimitReviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetOverLimitAccounts(
        [FromQuery] bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetCreditLimitReviewQuery(refresh), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// How much credit room these customers have left, so a screen can say so before somebody
    /// approves an order that will be refused.
    /// </summary>
    /// <param name="cardCodes">
    /// Customer codes, repeated or comma-separated. Answered from the same cached sweep as
    /// <see cref="GetOverLimitAccounts"/>, so a page's worth costs no extra SAP reads.
    /// </param>
    /// <param name="refresh">Re-reads SAP instead of serving the cached result.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("headroom")]
    [RequirePermission(Permission.ViewCustomers)]
    [ProducesResponseType(typeof(CreditHeadroomResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetCreditHeadroom(
        [FromQuery] string[]? cardCodes = null,
        [FromQuery] bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        var requested = (cardCodes ?? [])
            .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToArray();

        var result = await mediator.Send(new GetCreditHeadroomQuery(requested, refresh), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }
}
