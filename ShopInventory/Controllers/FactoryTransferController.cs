using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Authentication;
using ShopInventory.Common.Security;
using ShopInventory.DTOs;
using ShopInventory.Features.FactoryTransfers.Commands.CancelFactoryTransfer;
using ShopInventory.Features.FactoryTransfers.Commands.DecideFactoryTransfer;
using ShopInventory.Features.FactoryTransfers.Commands.RecallFactoryTransfer;
using ShopInventory.Features.FactoryTransfers.Commands.RetryFactoryTransfer;
using ShopInventory.Features.FactoryTransfers.Commands.SendFactoryTransfer;
using ShopInventory.Features.FactoryTransfers.Queries.GetFactoryTransfer;
using ShopInventory.Features.FactoryTransfers.Queries.GetFactoryTransferOptions;
using ShopInventory.Features.FactoryTransfers.Queries.GetFactoryTransfers;
using ShopInventory.Models;

namespace ShopInventory.Controllers;

/// <summary>
/// Factory transfers: production releasing stock to dispatch, and dispatch returning it, through the
/// holding warehouse (FSRW). The sender's post moves the stock into FSRW; somebody holding the
/// destination warehouse then accepts, part-accepts or rejects it, which moves it out again. Every
/// action is limited to the warehouses on the caller's account.
/// </summary>
[Route("api/factory-transfers")]
[Authorize(Policy = "ApiAccess")]
[Produces("application/json")]
public sealed class FactoryTransferController(IMediator mediator) : ApiControllerBase
{
    /// <summary>
    /// The transfers the caller may see, newest first. <c>view</c> is awaiting (in transit and the
    /// caller's to decide), open (not yet settled), all, or a status. <c>search</c> matches the number,
    /// the sender, a warehouse or an item code.
    /// </summary>
    [HttpGet]
    [RequirePermission(Permission.ViewFactoryTransfers)]
    [ProducesResponseType(typeof(FactoryTransferListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(
        [FromQuery] string? view = null,
        [FromQuery] string? search = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
            return Unauthorized();

        var result = await mediator.Send(new GetFactoryTransfersQuery(userId.Value, view, search, page, pageSize), cancellationToken);
        return result.Match(Ok, Problem);
    }

    /// <summary>Where the caller may send from and to, and whether they may accept.</summary>
    [HttpGet("options")]
    [RequirePermission(Permission.ViewFactoryTransfers)]
    [ProducesResponseType(typeof(FactoryTransferOptionsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetOptions(CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
            return Unauthorized();

        var result = await mediator.Send(new GetFactoryTransferOptionsQuery(userId.Value), cancellationToken);
        return result.Match(Ok, Problem);
    }

    /// <summary>One transfer with its lines, the batches SAP moved and both SAP documents.</summary>
    [HttpGet("{id:int}")]
    [RequirePermission(Permission.ViewFactoryTransfers)]
    [ProducesResponseType(typeof(FactoryTransferDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(int id, CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
            return Unauthorized();

        var result = await mediator.Send(new GetFactoryTransferQuery(id, userId.Value), cancellationToken);
        return result.Match(Ok, Problem);
    }

    /// <summary>
    /// Send stock from one of the caller's warehouses to the other side of its department. Posts it into
    /// the holding warehouse at once. A repeat with the same <c>clientRequestId</c> returns the transfer
    /// it already made.
    /// </summary>
    [HttpPost]
    [RequirePermission(Permission.SendFactoryTransfers)]
    [ProducesResponseType(typeof(FactoryTransferActionResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Send([FromBody] SendFactoryTransferRequestDto request, CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
            return Unauthorized();

        var result = await mediator.Send(
            new SendFactoryTransferCommand(
                request.ClientRequestId,
                request.SourceWarehouseCode,
                request.DestinationWarehouseCode,
                request.Reason,
                request.Lines,
                userId.Value),
            cancellationToken);
        return result.Match(Ok, Problem);
    }

    /// <summary>
    /// Accept (with the quantity taken on every line) or reject a transfer in the holding warehouse.
    /// What is not accepted goes back to the sender. A reason is required to reject or to accept short.
    /// </summary>
    [HttpPost("{id:int}/decision")]
    [RequirePermission(Permission.AcceptFactoryTransfers, Permission.AcceptAnyFactoryTransfers)]
    [ProducesResponseType(typeof(FactoryTransferActionResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Decide(
        int id,
        [FromBody] DecideFactoryTransferRequestDto request,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
            return Unauthorized();

        var result = await mediator.Send(
            new DecideFactoryTransferCommand(id, request.Decision, request.Reason, request.Lines, userId.Value),
            cancellationToken);
        return result.Match(Ok, Problem);
    }

    /// <summary>The sender takes back a transfer nobody has accepted yet; the stock returns to its warehouse.</summary>
    [HttpPost("{id:int}/recall")]
    [RequirePermission(Permission.SendFactoryTransfers)]
    [ProducesResponseType(typeof(FactoryTransferActionResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Recall(
        int id,
        [FromBody] RecallFactoryTransferRequestDto request,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
            return Unauthorized();

        var result = await mediator.Send(new RecallFactoryTransferCommand(id, request.Reason, userId.Value), cancellationToken);
        return result.Match(Ok, Problem);
    }

    /// <summary>
    /// Post again whichever leg failed or was stranded. Looks the leg up in SAP first, so a post whose
    /// reply was lost is adopted rather than repeated.
    /// </summary>
    [HttpPost("{id:int}/retry")]
    [RequirePermission(Permission.ViewFactoryTransfers)]
    [ProducesResponseType(typeof(FactoryTransferActionResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Retry(int id, CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
            return Unauthorized();

        var result = await mediator.Send(new RetryFactoryTransferCommand(id, userId.Value), cancellationToken);
        return result.Match(Ok, Problem);
    }

    /// <summary>
    /// Abandon a send that never reached SAP. SAP is checked first; a send that did reach it is finished
    /// into transit instead.
    /// </summary>
    [HttpPost("{id:int}/cancel")]
    [RequirePermission(Permission.SendFactoryTransfers)]
    [ProducesResponseType(typeof(FactoryTransferActionResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cancel(int id, CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
            return Unauthorized();

        var result = await mediator.Send(new CancelFactoryTransferCommand(id, userId.Value), cancellationToken);
        return result.Match(Ok, Problem);
    }
}
