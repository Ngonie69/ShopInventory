using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Common.Security;
using ShopInventory.DTOs;
using ShopInventory.Features.SapTransferApprovals.Commands.AddApprovedSapTransfer;
using ShopInventory.Features.SapTransferApprovals.Queries.GetSapTransferApproval;
using ShopInventory.Features.SapTransferApprovals.Queries.GetSapTransferApprovals;
using ShopInventory.Models;

namespace ShopInventory.Controllers;

/// <summary>
/// Inventory transfers and transfer requests keyed in the SAP client, held by SAP's approval procedure
/// and approved there, that nobody has added yet: list them, look at one, and add it as the real document.
/// </summary>
/// <remarks>
/// SAP is the source of truth; nothing here is mirrored into the local approval engine. Deciding stays in
/// the B1 client — this only adds what SAP has already approved.
///
/// The role gate here is the conversion route's. It does not decide on its own: the Web calls with its
/// integration key alongside the user's token, so the claims include the key's Admin. The handlers ask
/// the account in the database, by user id, the way a transfer request conversion does.
/// </remarks>
[Route("api/sap-transfer-approvals")]
[Authorize(Policy = "ApiAccess")]
[Authorize(Roles = "Admin,StockController,WashBay,DepotController,ShopStock")]
[Produces("application/json")]
public sealed class SapTransferApprovalController(IMediator mediator) : ApiControllerBase
{
    /// <summary>
    /// The approved transfer and transfer request drafts still waiting to be added, newest first.
    /// <c>kind</c> (<c>transfer</c> or <c>request</c>) reads one of the two; without it both come back,
    /// each row saying which it is, and <c>transferCount</c>/<c>requestCount</c> carry both totals either
    /// way. A depot controller or a shop's stock user sees only those leaving their own warehouses.
    /// <c>warehouse</c> narrows to drafts
    /// leaving or entering one warehouse; <c>from</c> and <c>to</c> (repeatable) to drafts leaving or
    /// entering any of several; <c>createdFrom</c>/<c>createdTo</c> to the days the drafts were created;
    /// <c>attachedOnly</c> to drafts with an attachment; <c>draftNum</c> to one draft. Every filter is
    /// applied in SAP, so <c>totalCount</c> is of the filtered queue. <c>beforeDocEntry</c> — the
    /// previous answer's <c>nextCursor</c> — continues below the last row that answer carried.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(SapTransferApprovalListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        [FromQuery] int? beforeDocEntry = null,
        [FromQuery] string? warehouse = null,
        [FromQuery] string[]? from = null,
        [FromQuery] string[]? to = null,
        [FromQuery] DateTime? createdFrom = null,
        [FromQuery] DateTime? createdTo = null,
        [FromQuery] bool attachedOnly = false,
        [FromQuery] int? draftNum = null,
        [FromQuery] SapHeldDraftKind? kind = null,
        CancellationToken cancellationToken = default)
    {
        var filter = new SapTransferDraftFilter
        {
            Kind = kind,
            FromWarehouses = from is { Length: > 0 } ? from : null,
            ToWarehouses = to is { Length: > 0 } ? to : null,
            CreatedFrom = createdFrom,
            CreatedTo = createdTo,
            AttachedOnly = attachedOnly,
            DraftNum = draftNum
        };

        var result = await mediator.Send(
            new GetSapTransferApprovalsQuery(page, pageSize, beforeDocEntry, warehouse, UserClaimReader.GetUserId(User), filter),
            cancellationToken);
        return result.Match(Ok, Problem);
    }

    /// <summary>
    /// One held draft — a transfer or a transfer request: its lines and batch picks, the approvals behind
    /// it, and the request a transfer draft was copied from.
    /// </summary>
    [HttpGet("{draftEntry:int}")]
    [ProducesResponseType(typeof(SapTransferApprovalDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByDraftEntry(int draftEntry, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetSapTransferApprovalQuery(draftEntry, UserClaimReader.GetUserId(User)), cancellationToken);
        return result.Match(Ok, Problem);
    }

    /// <summary>
    /// Add the approved draft as the document it describes. A transfer draft becomes the inventory
    /// transfer, and any transfer request it was copied from is closed; a transfer request draft becomes
    /// an open transfer request, and no stock moves. One add per draft: a repeat replays the first answer.
    /// </summary>
    [HttpPost("{draftEntry:int}/add")]
    [ProducesResponseType(typeof(AddApprovedSapTransferResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Add(
        int draftEntry,
        [FromBody] AddApprovedSapTransferRequestDto? request,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
        {
            return Unauthorized();
        }

        var clientRequestId = !string.IsNullOrWhiteSpace(request?.ClientRequestId)
            ? request.ClientRequestId
            : Request.Headers.TryGetValue("Idempotency-Key", out var values) ? values.FirstOrDefault() : null;

        var result = await mediator.Send(
            new AddApprovedSapTransferCommand(draftEntry, userId.Value, User.Identity?.Name ?? "unknown", clientRequestId),
            cancellationToken);
        return result.Match(Ok, Problem);
    }
}
