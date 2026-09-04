using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Common.Security;
using ShopInventory.DTOs;
using ShopInventory.Features.FiscalisationConfiguration.Commands.AssignOfflineSigningLease;
using ShopInventory.Features.FiscalisationConfiguration.Commands.RegisterFiscalDeviceHandset;
using ShopInventory.Features.FiscalisationConfiguration.Queries.GetFiscalDeviceHandsets;
using ShopInventory.Features.FiscalisationConfiguration.Queries.GetOfflineSigningLease;
using ShopInventory.Features.FiscalisationConfiguration.Queries.PreviewFiscalDevice;

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

    /// <summary>Active van accounts, and the device each already carries. Who a device can be given to.</summary>
    [HttpGet("handsets")]
    public async Task<IActionResult> GetHandsets(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetFiscalDeviceHandsetsQuery(), cancellationToken);
        return result.Match<IActionResult>(Ok, errors => Problem(errors));
    }

    /// <summary>
    /// What the Fiscalisation platform says about a device, and whether it may be given to a van.
    /// </summary>
    /// <remarks>
    /// Answers for ids this application has never seen — that is the point. Reads only; the same checks
    /// run again on the save, because a device's mode or certificate can change between looking and
    /// deciding.
    ///
    /// <c>handsetUserId</c> is the van it is intended for, once one is chosen. Without it the device is
    /// judged on its own merits, which is what the screen needs while someone is still typing.
    /// </remarks>
    [HttpGet("{deviceId:int}/preview")]
    public async Task<IActionResult> Preview(
        int deviceId,
        CancellationToken cancellationToken,
        [FromQuery] Guid? handsetUserId = null)
    {
        var result = await mediator.Send(new PreviewFiscalDeviceQuery(deviceId, handsetUserId), cancellationToken);
        return result.Match<IActionResult>(Ok, errors => Problem(errors));
    }

    /// <summary>
    /// Registers the one handset that signs as this device, or releases it when
    /// <see cref="RegisterFiscalDeviceHandsetRequest.HandsetUserId"/> is null.
    /// </summary>
    /// <remarks>
    /// Answers 409 when the handset losing the device is still carrying signed receipts the server has
    /// not seen — the same guard as moving a nomination, for the same reason, and cleared the same way
    /// with <c>force</c>.
    /// </remarks>
    [HttpPut("{deviceId:int}/handset")]
    public async Task<IActionResult> RegisterHandset(
        int deviceId,
        [FromBody] RegisterFiscalDeviceHandsetRequest request,
        CancellationToken cancellationToken,
        [FromQuery] bool force = false)
    {
        var actorId = UserClaimReader.GetUserId(User);
        if (actorId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(
            new RegisterFiscalDeviceHandsetCommand(
                deviceId,
                request.HandsetUserId,
                force,
                actorId.Value,
                User.Identity?.Name ?? "Unknown"),
            cancellationToken);

        return result.Match<IActionResult>(Ok, errors => Problem(errors));
    }

    /// <summary>
    /// One device's offline signing lease
    /// </summary>
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
