using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Common.Security;
using ShopInventory.DTOs;
using ShopInventory.Features.FiscalisationConfiguration.Commands.AssignOfflineSigningLease;
using ShopInventory.Features.FiscalisationConfiguration.Queries.GetOfflineSigningLease;

namespace ShopInventory.Controllers;

/// <summary>
/// Which single handset may sign receipts offline on a fiscal device.
///
/// A fiscal device is one hash-chained receipt sequence, not a pool, so offline signing is nominated to
/// one van at a time and every other handset is refused a lease with a message naming the holder. This is
/// where the office moves it.
/// </summary>
[Route("api/fiscal-devices")]
[Authorize(Policy = "AdminOnly")]
public class FiscalDeviceOfflineLeaseController(IMediator mediator) : ApiControllerBase
{
    /// <summary>Every device the fleet's handsets are registered against, with holder and candidates.</summary>
    [HttpGet("offline-leases")]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetOfflineSigningLeaseOverviewQuery(), cancellationToken);
        return result.Match<IActionResult>(Ok, errors => Problem(errors));
    }

    [HttpGet("{deviceId:int}/offline-lease")]
    public async Task<IActionResult> Get(int deviceId, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetOfflineSigningLeaseQuery(deviceId), cancellationToken);
        return result.Match<IActionResult>(Ok, errors => Problem(errors));
    }

    /// <summary>
    /// Nominates a handset, or clears the nomination when <see cref="AssignOfflineSigningLeaseRequest.HolderUserId"/>
    /// is null.
    /// </summary>
    /// <remarks>
    /// Answers 409 when the outgoing handset is still carrying signed receipts the server has not seen.
    /// That is not a lock — the body says what is at stake and the same call with
    /// <see cref="AssignOfflineSigningLeaseRequest.Force"/> goes through — but it has to be read first.
    /// </remarks>
    [HttpPut("{deviceId:int}/offline-lease")]
    public async Task<IActionResult> Assign(
        int deviceId,
        [FromBody] AssignOfflineSigningLeaseRequest request,
        CancellationToken cancellationToken)
    {
        var actorId = UserClaimReader.GetUserId(User);
        if (actorId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(
            new AssignOfflineSigningLeaseCommand(
                deviceId,
                request.HolderUserId,
                request.Force,
                actorId.Value,
                User.Identity?.Name ?? "Unknown"),
            cancellationToken);

        return result.Match<IActionResult>(Ok, errors => Problem(errors));
    }
}
