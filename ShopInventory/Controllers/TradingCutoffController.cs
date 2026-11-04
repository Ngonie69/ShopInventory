using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Common.Security;
using ShopInventory.Features.TradingCutoff.Commands.UpdateTradingCutoffSettings;
using ShopInventory.Features.TradingCutoff.Queries.GetTradingCutoffSettings;

namespace ShopInventory.Controllers;

/// <summary>
/// The daily trading cut-off: the time of day from which shops, vending and vans stop taking new
/// transactions, per channel, with exceptions per account.
/// </summary>
/// <remarks>
/// Enforced where each transaction is taken — the till and vending sale, the van sale, the van order and
/// the van order conversion — by <c>TradingCutoffGate</c>. These routes only read and change the setting.
/// </remarks>
[Route("api/trading-cutoff")]
[Authorize(Roles = "Admin")]
public class TradingCutoffController(IMediator mediator) : ApiControllerBase
{
    /// <summary>
    /// The cut-off for every channel, and every selling account with its exception.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetTradingCutoffSettingsQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Saves every channel's cut-off and the complete list of account exceptions. An account left out of
    /// the list follows its channel again.
    /// </summary>
    [HttpPut]
    public async Task<IActionResult> Update(
        [FromBody] UpdateTradingCutoffSettingsRequest request,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
            return Unauthorized();

        var result = await mediator.Send(
            new UpdateTradingCutoffSettingsCommand(
                userId.Value,
                User.Identity?.Name,
                request.Channels ?? [],
                request.Accounts ?? []),
            cancellationToken);

        return result.Match(value => Ok(value), errors => Problem(errors));
    }
}
