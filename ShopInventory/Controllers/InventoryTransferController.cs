using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Authentication;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;
using ShopInventory.Features.InventoryTransfers.Commands.CancelPendingTransfer;
using ShopInventory.Features.InventoryTransfers.Commands.CloseTransferRequest;
using ShopInventory.Features.InventoryTransfers.Commands.ConvertTransferRequest;
using ShopInventory.Features.InventoryTransfers.Commands.CreateInventoryTransfer;
using ShopInventory.Features.InventoryTransfers.Commands.CreateTransferRequest;
using ShopInventory.Features.InventoryTransfers.Commands.DecidePendingRequestEdit;
using ShopInventory.Features.InventoryTransfers.Commands.DecidePendingTransfer;
using ShopInventory.Features.InventoryTransfers.Commands.EditTransferRequest;
using ShopInventory.Features.InventoryTransfers.Queries.GetPendingRequestEdits;
using ShopInventory.Features.InventoryTransfers.Commands.RetryPendingTransferPost;
using ShopInventory.Features.InventoryTransfers.Queries.GetPendingTransferById;
using ShopInventory.Features.InventoryTransfers.Queries.GetPendingTransfers;
using ShopInventory.Features.InventoryTransfers.Queries.GetPagedTransferRequests;
using ShopInventory.Features.InventoryTransfers.Queries.GetPagedTransfers;
using ShopInventory.Features.InventoryTransfers.Queries.GetTransferByDocEntry;
using ShopInventory.Features.InventoryTransfers.Queries.GetTransferRequestByDocEntry;
using ShopInventory.Features.InventoryTransfers.Queries.GetTransferRequestsByWarehouse;
using ShopInventory.Features.InventoryTransfers.Queries.GetTransfersByDate;
using ShopInventory.Features.InventoryTransfers.Queries.GetTransfersByDateRange;
using ShopInventory.Features.InventoryTransfers.Queries.GetTransfersByWarehouse;
using ShopInventory.Common.Security;

namespace ShopInventory.Controllers;

[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
public class InventoryTransferController(IMediator mediator) : ApiControllerBase
{
    /// <summary>
    /// Submits an inventory transfer. The transfer is held and only posts to SAP once every
    /// stage of the configured approval process has approved it, so this returns 202 Accepted.
    /// </summary>
    [HttpPost]
    [RequirePermission(Permission.TransferStock, Permission.TransferInventory)]
    [ProducesResponseType(typeof(InventoryTransferCreatedResponseDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(InventoryTransferCreatedResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateInventoryTransfer(
        [FromBody] CreateInventoryTransferRequest request,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
            return Unauthorized();

        if (string.IsNullOrWhiteSpace(request.ClientRequestId) && Request.Headers.TryGetValue("Idempotency-Key", out var idempotencyValues))
        {
            request.ClientRequestId = idempotencyValues.FirstOrDefault();
        }

        var result = await mediator.Send(new CreateInventoryTransferCommand(request, userId.Value), cancellationToken);
        return result.Match(
            value =>
            {
                if (value.RequiresApproval && value.PendingTransfer is not null)
                {
                    value.StatusUrl = Url.Action(
                        nameof(GetPendingInventoryTransfer), null, new { id = value.PendingTransfer.Id }, Request.Scheme);
                    return Accepted(value.StatusUrl, value);
                }

                if (value.WasQueued)
                {
                    value.StatusUrl = !string.IsNullOrWhiteSpace(value.QueueExternalReference)
                        ? Url.Action("GetTransferQueueStatus", "DesktopIntegration", new { externalReference = value.QueueExternalReference }, Request.Scheme)
                        : null;
                    return Accepted(value);
                }

                return CreatedAtAction(nameof(GetInventoryTransferByDocEntry), new { docEntry = value.Transfer!.DocEntry }, value);
            },
            errors => Problem(errors));
    }

    #region Pending (approval-held) Transfer Endpoints

    /// <summary>
    /// Lists direct inventory transfers held for approval. Defaults to those still awaiting a
    /// decision; <paramref name="fromDate"/> and <paramref name="toDate"/> bound when they were
    /// raised, both inclusive.
    /// </summary>
    [HttpGet("pending")]
    [ProducesResponseType(typeof(PendingInventoryTransferListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPendingInventoryTransfers(
        [FromQuery] string? status = null,
        [FromQuery] string? warehouseCode = null,
        [FromQuery] bool mineOnly = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        CancellationToken cancellationToken = default)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
            return Unauthorized();

        var result = await mediator.Send(
            new GetPendingTransfersQuery(userId.Value, status, warehouseCode, mineOnly, page, pageSize, fromDate, toDate),
            cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// One held transfer, posting status included
    /// </summary>
    [HttpGet("pending/{id:guid}")]
    [ProducesResponseType(typeof(PendingInventoryTransferDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPendingInventoryTransfer(Guid id, CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
            return Unauthorized();

        var result = await mediator.Send(new GetPendingTransferByIdQuery(id, userId.Value), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Records an approval decision. Depot controllers may only decide on transfers whose
    /// source warehouse is one of their assigned warehouses.
    /// </summary>
    [HttpPost("pending/{id:guid}/decision")]
    [Authorize(Roles = "Admin,StockController,DepotController,Manager")]
    [ProducesResponseType(typeof(PendingInventoryTransferDecisionResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DecidePendingInventoryTransfer(
        Guid id,
        [FromBody] SubmitPendingTransferDecisionDto request,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
            return Unauthorized();

        var result = await mediator.Send(
            new DecidePendingTransferCommand(id, userId.Value, request.Decision, request.StageId, request.Remarks), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Re-attempts the SAP post for a transfer that was approved but failed to post.
    /// </summary>
    [HttpPost("pending/{id:guid}/post")]
    [Authorize(Roles = "Admin,StockController,DepotController,Manager")]
    [ProducesResponseType(typeof(PendingInventoryTransferDecisionResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> RetryPendingInventoryTransferPost(Guid id, CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
            return Unauthorized();

        var result = await mediator.Send(new RetryPendingTransferPostCommand(id, userId.Value), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Withdraws a transfer the caller submitted, before any decision has been recorded.
    /// </summary>
    [HttpPost("pending/{id:guid}/cancel")]
    [ProducesResponseType(typeof(PendingInventoryTransferDecisionResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> CancelPendingInventoryTransfer(Guid id, CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
            return Unauthorized();

        var result = await mediator.Send(new CancelPendingTransferCommand(id, userId.Value), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    #endregion

    /// <summary>
    /// Transfers for a warehouse — the bare {} segment is a warehouse code, not a DocEntry
    /// </summary>
    [HttpGet("{warehouseCode}")]
    [ProducesResponseType(typeof(InventoryTransferListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetInventoryTransfersByWarehouse(string warehouseCode, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetTransfersByWarehouseQuery(warehouseCode), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// List a warehouse's inventory transfers, paginated
    /// </summary>
    [HttpGet("{warehouseCode}/paged")]
    [ProducesResponseType(typeof(InventoryTransferListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPagedInventoryTransfers(
        string warehouseCode, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetPagedTransfersQuery(warehouseCode, page, pageSize), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// A warehouse's transfers on one date
    /// </summary>
    [HttpGet("{warehouseCode}/date/{date}")]
    [ProducesResponseType(typeof(InventoryTransferDateResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetInventoryTransfersByDate(string warehouseCode, string date, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetTransfersByDateQuery(warehouseCode, date), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// A warehouse's transfers between two dates
    /// </summary>
    [HttpGet("{warehouseCode}/daterange")]
    [ProducesResponseType(typeof(InventoryTransferDateResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetInventoryTransfersByDateRange(
        string warehouseCode, [FromQuery] string fromDate, [FromQuery] string toDate,
        [FromQuery] int? page = null, [FromQuery] int? pageSize = null, CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetTransfersByDateRangeQuery(warehouseCode, fromDate, toDate, page, pageSize), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Get one transfer's details
    /// </summary>
    [HttpGet("detail/{docEntry:int}")]
    [ProducesResponseType(typeof(InventoryTransferDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInventoryTransferByDocEntry(int docEntry, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetTransferByDocEntryQuery(docEntry), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    #region Transfer Request Endpoints

    /// <summary>
    /// Create a transfer request
    /// </summary>
    [HttpPost("request")]
    [ProducesResponseType(typeof(TransferRequestCreatedResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateTransferRequest(
        [FromBody] CreateTransferRequestDto request, CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
            return Unauthorized();

        var result = await mediator.Send(new CreateTransferRequestCommand(request, userId.Value), cancellationToken);
        return result.Match(
            value => CreatedAtAction(nameof(GetTransferRequestByDocEntry), new { docEntry = value.TransferRequest!.DocEntry }, value),
            errors => Problem(errors));
    }

    /// <summary>
    /// Authorize a request and generate the SAP transfer. Admin, StockController, DepotController
    /// </summary>
    [HttpPost("request/{docEntry:int}/convert")]
    [Authorize(Roles = "Admin,StockController,DepotController")]
    [ProducesResponseType(typeof(TransferRequestConvertedResponseDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> ConvertTransferRequestToTransfer(int docEntry, CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
            return Unauthorized();

        var result = await mediator.Send(new ConvertTransferRequestCommand(docEntry, userId.Value), cancellationToken);
        return result.Match(
            value => value.Transfer is null
                ? Ok(value)
                : CreatedAtAction(nameof(GetInventoryTransferByDocEntry), new { docEntry = value.Transfer.DocEntry }, value),
            errors => Problem(errors));
    }

    /// <summary>
    /// Close a request in SAP without converting it. Admin, StockController, DepotController
    /// </summary>
    [HttpPost("request/{docEntry:int}/close")]
    [Authorize(Roles = "Admin,StockController,DepotController")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> CloseTransferRequest(int docEntry, CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
            return Unauthorized();

        var result = await mediator.Send(new CloseTransferRequestCommand(docEntry, userId.Value), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Changes an open transfer request: quantities may be adjusted, lines dropped, and either
    /// warehouse reassigned. Callers assigned the source warehouse write straight to SAP; anyone
    /// else has the change held for approval, so this can answer 202 Accepted. A warehouse-scoped
    /// caller may only name warehouses assigned to them, which is refused with 403 rather than
    /// held.
    /// </summary>
    [HttpPatch("request/{docEntry:int}")]
    [Authorize(Roles = "Admin,StockController,DepotController,Manager")]
    [ProducesResponseType(typeof(TransferRequestEditResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(TransferRequestEditResponseDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> EditTransferRequest(
        int docEntry,
        [FromBody] EditTransferRequestDto request,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
            return Unauthorized();

        var result = await mediator.Send(new EditTransferRequestCommand(docEntry, request, userId.Value), cancellationToken);
        return result.Match(
            value => value.RequiresApproval ? Accepted(value) : Ok(value),
            errors => Problem(errors));
    }

    #region Held Transfer Request Changes

    /// <summary>
    /// Lists changes to transfer requests that are held for approval. Defaults to those still
    /// awaiting a decision.
    /// </summary>
    [HttpGet("request-edits")]
    [ProducesResponseType(typeof(PendingTransferRequestEditListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPendingRequestEdits(
        [FromQuery] string? status = null,
        [FromQuery] int? requestDocEntry = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
            return Unauthorized();

        var result = await mediator.Send(
            new GetPendingRequestEditsQuery(userId.Value, status, requestDocEntry, page, pageSize), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// One held change
    /// </summary>
    [HttpGet("request-edits/{id:guid}")]
    [ProducesResponseType(typeof(PendingTransferRequestEditDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPendingRequestEdit(Guid id, CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
            return Unauthorized();

        var result = await mediator.Send(new GetPendingRequestEditByIdQuery(id, userId.Value), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Approves or rejects a held change. Approving the final stage writes it to SAP.
    /// </summary>
    [HttpPost("request-edits/{id:guid}/decision")]
    [Authorize(Roles = "Admin,StockController,DepotController,Manager")]
    [ProducesResponseType(typeof(PendingTransferRequestEditDecisionResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DecidePendingRequestEdit(
        Guid id,
        [FromBody] SubmitPendingTransferDecisionDto request,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
            return Unauthorized();

        var result = await mediator.Send(
            new DecidePendingRequestEditCommand(id, userId.Value, request.Decision, request.StageId, request.Remarks),
            cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Withdraws a change the caller proposed, before any decision has been recorded.
    /// </summary>
    [HttpPost("request-edits/{id:guid}/cancel")]
    [ProducesResponseType(typeof(PendingTransferRequestEditDecisionResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> CancelPendingRequestEdit(Guid id, CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
            return Unauthorized();

        var result = await mediator.Send(new CancelPendingRequestEditCommand(id, userId.Value), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    #endregion

    /// <summary>
    /// One transfer request
    /// </summary>
    [HttpGet("request/{docEntry:int}")]
    [ProducesResponseType(typeof(InventoryTransferRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTransferRequestByDocEntry(int docEntry, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetTransferRequestByDocEntryQuery(docEntry), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// A warehouse's transfer requests
    /// </summary>
    [HttpGet("requests/{warehouseCode}")]
    [ProducesResponseType(typeof(TransferRequestListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTransferRequestsByWarehouse(string warehouseCode, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetTransferRequestsByWarehouseQuery(warehouseCode), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Lists transfer requests newest first. <paramref name="status"/> filters on the SAP document
    /// status — "open", "closed", or "all" (the default). Most of the eleven thousand requests in
    /// SAP are closed, so a screen that actions requests should ask for open ones.
    /// </summary>
    [HttpGet("requests")]
    [ProducesResponseType(typeof(TransferRequestListResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetPagedTransferRequests(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetPagedTransferRequestsQuery(page, pageSize, status), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    #endregion
}
