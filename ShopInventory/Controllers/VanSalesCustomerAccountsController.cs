using System.Security.Claims;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.DTOs;
using ShopInventory.Features.VanSalesCustomerAuth;
using ShopInventory.Features.VanSalesCustomerAuth.Commands.DeactivateVanSalesCustomerAccount;
using ShopInventory.Features.VanSalesCustomerAuth.Commands.OnboardVanSalesCustomerAccount;
using ShopInventory.Features.VanSalesCustomerAuth.Queries.GetVanSalesCustomerAccounts;

namespace ShopInventory.Controllers;

/// <summary>
/// Operator management of van sales customers' app sign-ins.
/// </summary>
/// <remarks>
/// Staff-facing, and therefore behind "ApiAccess" like every other operator surface — not the
/// "VanSalesCustomerAccess" policy that guards the app itself. The two must not be confused: a
/// customer reaching these actions could grant themselves, or revoke a rival shop's, access to
/// ordering.
/// </remarks>
[Route("api/van-sales-customer-accounts")]
[Authorize(Policy = "ApiAccess")]
public class VanSalesCustomerAccountsController(IMediator mediator) : ApiControllerBase
{
    /// <summary>List customer sign-ins, optionally for one route customer.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<VanSalesCustomerAccountResult>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAccounts(
        [FromQuery] int? routeCustomerId,
        [FromQuery] bool includeInactive,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new GetVanSalesCustomerAccountsQuery(routeCustomerId, includeInactive),
            cancellationToken);

        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>Give a customer a sign-in, or re-point an existing one at a new handset.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(VanSalesCustomerAccountResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> Onboard(
        [FromBody] OnboardVanSalesCustomerAccountRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new OnboardVanSalesCustomerAccountCommand(
                request.RouteCustomerId,
                request.PhoneNumber,
                request.DisplayName,
                GetAuthenticatedUserId(),
                request.Password),
            cancellationToken);

        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>Withdraw a sign-in and end the sessions it holds.</summary>
    [HttpPost("{accountId:int}/deactivate")]
    [ProducesResponseType(typeof(VanSalesCustomerAccountResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> Deactivate(int accountId, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new DeactivateVanSalesCustomerAccountCommand(accountId),
            cancellationToken);

        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    private Guid? GetAuthenticatedUserId()
    {
        var claim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(claim, out var userId) ? userId : null;
    }
}
