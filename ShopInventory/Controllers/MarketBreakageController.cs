using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Authentication;
using ShopInventory.Common.Security;
using ShopInventory.DTOs;
using ShopInventory.Features.MarketBreakages.Commands.ConfirmMarketBreakage;
using ShopInventory.Features.MarketBreakages.Commands.RejectMarketBreakage;
using ShopInventory.Features.MarketBreakages.Queries.GetMarketBreakage;
using ShopInventory.Features.MarketBreakages.Queries.GetMarketBreakages;
using ShopInventory.Models;

namespace ShopInventory.Controllers;

/// <summary>
/// The office's side of market breakages: stock broken in transit, reported by
/// van reps from the handset (<c>POST /api/vansales/breakages</c>). Count it, then confirm it into a
/// SAP transfer from the van to the returns warehouse, or reject it.
/// </summary>
[Route("api/market-breakages")]
[Authorize(Policy = "ApiAccess")]
[Produces("application/json")]
public sealed class MarketBreakageController(IMediator mediator) : ApiControllerBase
{
    /// <summary>
    /// Breakage reports, newest first. <c>status</c> is open (pending, failed or stranded), Pending,
    /// Transferring, Transferred, TransferFailed, Rejected, or empty for all. <c>search</c> matches the
    /// rep, van, shop or an item code.
    /// </summary>
    [HttpGet]
    [RequirePermission(Permission.ConfirmMarketBreakages)]
    [ProducesResponseType(typeof(MarketBreakageListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(
        [FromQuery] string? status = null,
        [FromQuery] string? search = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetMarketBreakagesQuery(status, search, page, pageSize), cancellationToken);
        return result.Match(Ok, Problem);
    }

    /// <summary>One breakage report with its lines, the office's count and the transfer it became.</summary>
    [HttpGet("{id:int}")]
    [RequirePermission(Permission.ConfirmMarketBreakages)]
    [ProducesResponseType(typeof(MarketBreakageDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(int id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetMarketBreakageQuery(id), cancellationToken);
        return result.Match(Ok, Problem);
    }

    /// <summary>
    /// Record the counted quantity for every line and transfer the counted stock from the van to the
    /// returns warehouse. Also how a failed transfer is retried. Only one transfer per report can be in
    /// flight; a second confirm while one is running is a 409.
    /// </summary>
    [HttpPost("{id:int}/confirm")]
    [RequirePermission(Permission.ConfirmMarketBreakages)]
    [ProducesResponseType(typeof(MarketBreakageDecisionResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Confirm(
        int id,
        [FromBody] ConfirmMarketBreakageRequestDto request,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(
            new ConfirmMarketBreakageCommand(id, request.Lines, request.Remarks, userId.Value),
            cancellationToken);
        return result.Match(Ok, Problem);
    }

    /// <summary>Turn the report down. Nothing is transferred; the reason is required.</summary>
    [HttpPost("{id:int}/reject")]
    [RequirePermission(Permission.ConfirmMarketBreakages)]
    [ProducesResponseType(typeof(MarketBreakageDecisionResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Reject(
        int id,
        [FromBody] RejectMarketBreakageRequestDto request,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(new RejectMarketBreakageCommand(id, request.Remarks, userId.Value), cancellationToken);
        return result.Match(Ok, Problem);
    }
}
