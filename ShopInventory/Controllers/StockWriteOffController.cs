using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Authentication;
using ShopInventory.Common.Security;
using ShopInventory.DTOs;
using ShopInventory.Features.StockWriteOffs.Commands.CreateStockWriteOff;
using ShopInventory.Features.StockWriteOffs.Queries.GetStockWriteOff;
using ShopInventory.Features.StockWriteOffs.Queries.GetStockWriteOffReasons;
using ShopInventory.Features.StockWriteOffs.Queries.GetStockWriteOffs;
using ShopInventory.Models;

namespace ShopInventory.Controllers;

/// <summary>
/// Stock write-offs: counted stock issued out of a warehouse in SAP as a goods issue, so the books
/// stop carrying product that no longer exists.
/// </summary>
/// <remarks>
/// The document every other path here lacks. Sales, purchases and transfers all either move stock or
/// exchange it for something, so a warehouse that only receives — a returns warehouse taking what
/// vans bring back — had no way to be drained before this.
/// </remarks>
[Route("api/stock-write-offs")]
[Authorize(Policy = "ApiAccess")]
[Produces("application/json")]
public sealed class StockWriteOffController(IMediator mediator) : ApiControllerBase
{
    /// <summary>
    /// Write-offs, newest first. <c>status</c> is Pending, Posting, Posted, PostFailed, Cancelled, or
    /// empty for all.
    /// </summary>
    [HttpGet]
    [RequirePermission(Permission.ViewStockWriteOffs)]
    [ProducesResponseType(typeof(StockWriteOffListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(
        [FromQuery] string? status = null,
        [FromQuery] string? warehouseCode = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(
            new GetStockWriteOffsQuery(status, warehouseCode, page, pageSize),
            cancellationToken);
        return result.Match(Ok, Problem);
    }

    /// <summary>
    /// The reasons a write-off may be given, and whether SAP itself will record the one chosen.
    /// </summary>
    /// <remarks>
    /// Read rather than held in the caller, because where the company database defines a reason field
    /// on the goods-issue line table SAP rejects any value that field does not carry.
    /// </remarks>
    [HttpGet("reasons")]
    [RequirePermission(Permission.ViewStockWriteOffs)]
    [ProducesResponseType(typeof(StockWriteOffReasonsResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetReasons(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetStockWriteOffReasonsQuery(), cancellationToken);
        return result.Match(Ok, Problem);
    }

    /// <summary>One write-off with its lines and the goods issue it became.</summary>
    [HttpGet("{id:int}")]
    [RequirePermission(Permission.ViewStockWriteOffs)]
    [ProducesResponseType(typeof(StockWriteOffDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(int id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetStockWriteOffQuery(id), cancellationToken);
        return result.Match(Ok, Problem);
    }

    /// <summary>
    /// Write counted stock off a warehouse, issuing it out of SAP.
    /// </summary>
    /// <remarks>
    /// Resending the same <c>clientRequestId</c> (or <c>Idempotency-Key</c> header) returns the
    /// write-off already raised rather than writing the stock off twice, and is how a post that failed
    /// is retried. Only one post per write-off can be in flight; a second while one is running is a
    /// 409.
    /// </remarks>
    [HttpPost]
    [RequirePermission(Permission.PostStockWriteOffs)]
    [ProducesResponseType(typeof(StockWriteOffResultDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(
        [FromBody] CreateStockWriteOffRequestDto request,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
        {
            return Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(request.ClientRequestId)
            && Request.Headers.TryGetValue("Idempotency-Key", out var idempotencyValues))
        {
            request.ClientRequestId = idempotencyValues.FirstOrDefault();
        }

        var result = await mediator.Send(new CreateStockWriteOffCommand(request, userId.Value), cancellationToken);
        return result.Match(
            value => value.AlreadyPosted
                ? Ok(value)
                : CreatedAtAction(nameof(GetById), new { id = value.WriteOff.Id }, value),
            Problem);
    }
}
