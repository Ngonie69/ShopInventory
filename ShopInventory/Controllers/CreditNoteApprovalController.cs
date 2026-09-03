using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Authentication;
using ShopInventory.Common.Security;
using ShopInventory.DTOs;
using ShopInventory.Features.CreditNoteApprovals.Commands.AddApprovedCreditNote;
using ShopInventory.Features.CreditNoteApprovals.Commands.DecideCreditNoteApproval;
using ShopInventory.Features.CreditNoteApprovals.Queries.DownloadCreditNoteDraftAttachment;
using ShopInventory.Features.CreditNoteApprovals.Queries.GetCreditNoteApproval;
using ShopInventory.Features.CreditNoteApprovals.Queries.GetCreditNoteApprovals;
using ShopInventory.Models;

namespace ShopInventory.Controllers;

/// <summary>
/// A/R credit memos raised in the SAP client and held by SAP's own approval procedure: list them,
/// look at the draft and its attachments, decide them, and add the approved ones.
/// </summary>
/// <remarks>
/// SAP is the source of truth. Nothing here is mirrored into the local approval engine behind
/// <c>/api/approval-process</c>, which governs documents this app posts.
/// </remarks>
[Route("api/credit-note-approvals")]
[Authorize(Policy = "ApiAccess")]
[Produces("application/json")]
public sealed class CreditNoteApprovalController(IMediator mediator) : ApiControllerBase
{
    /// <summary>
    /// The requests SAP holds. <c>status</c> is open (the default: pending, or approved and not yet
    /// added), pending, approved or all.
    /// </summary>
    /// <remarks>
    /// Two ways to page, and the second is the one to use when walking the queue. <c>page</c>
    /// offsets from the top, which is simple and fine for a single read. <c>beforeCode</c> — the
    /// previous answer's <c>nextCursor</c> — continues below the last row that answer carried, and
    /// is the only one that is stable: the queue is newest-first and live, so every credit memo
    /// raised while somebody pages pushes a row they have already read onto their next offset page,
    /// and drops one out of sight. Send <c>page</c> alongside it if you want the label to say how
    /// far in you are; it does not affect which rows come back.
    /// </remarks>
    [HttpGet]
    [RequirePermission(Permission.ApproveSapCreditNotes, Permission.AddApprovedCreditNotes)]
    [ProducesResponseType(typeof(CreditNoteApprovalListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(
        [FromQuery] string? status = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        [FromQuery] int? beforeCode = null,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(
            new GetCreditNoteApprovalsQuery(status, page, pageSize, beforeCode), cancellationToken);
        return result.Match(Ok, Problem);
    }

    /// <summary>One request with the draft it holds, its lines, attachments, approver lines and stage.</summary>
    [HttpGet("{code:int}")]
    [RequirePermission(Permission.ApproveSapCreditNotes, Permission.AddApprovedCreditNotes)]
    [ProducesResponseType(typeof(CreditNoteApprovalDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByCode(int code, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetCreditNoteApprovalQuery(code), cancellationToken);
        return result.Match(Ok, Problem);
    }

    /// <summary>The bytes of one file attached to the draft, streamed from SAP.</summary>
    [HttpGet("{code:int}/attachments/{lineNum:int}/download")]
    [RequirePermission(Permission.ApproveSapCreditNotes, Permission.AddApprovedCreditNotes)]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadAttachment(int code, int lineNum, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new DownloadCreditNoteDraftAttachmentQuery(code, lineNum), cancellationToken);
        return result.Match(
            download => File(download.Content, download.ContentType, download.FileName),
            Problem);
    }

    /// <summary>
    /// Approve or reject the request. Recorded in SAP as the service approver, with the caller named
    /// in the remarks. Send an <c>Idempotency-Key</c> (or <c>clientRequestId</c>) so a repeat replays
    /// the first answer instead of reaching SAP twice.
    /// </summary>
    [HttpPost("{code:int}/decision")]
    [RequirePermission(Permission.ApproveSapCreditNotes)]
    [ProducesResponseType(typeof(CreditNoteApprovalDecisionResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Decide(
        int code,
        [FromBody] CreditNoteApprovalDecisionRequestDto request,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(
            new DecideCreditNoteApprovalCommand(
                code,
                request.Decision,
                request.Remarks,
                userId.Value,
                User.Identity?.Name ?? "unknown",
                ClientRequestId(request.ClientRequestId)),
            cancellationToken);

        return result.Match(Ok, Problem);
    }

    /// <summary>
    /// Add the approved draft as the credit note, then project and fiscalise it. One add per draft:
    /// a repeat replays the first answer.
    /// </summary>
    [HttpPost("{code:int}/add")]
    [RequirePermission(Permission.AddApprovedCreditNotes)]
    [ProducesResponseType(typeof(AddApprovedCreditNoteResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Add(
        int code,
        [FromBody] AddApprovedCreditNoteRequestDto? request,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(
            new AddApprovedCreditNoteCommand(
                code,
                userId.Value,
                User.Identity?.Name ?? "unknown",
                ClientRequestId(request?.ClientRequestId)),
            cancellationToken);

        return result.Match(Ok, Problem);
    }

    private string? ClientRequestId(string? fromBody)
    {
        if (!string.IsNullOrWhiteSpace(fromBody))
        {
            return fromBody;
        }

        return Request.Headers.TryGetValue("Idempotency-Key", out var values) ? values.FirstOrDefault() : null;
    }
}
