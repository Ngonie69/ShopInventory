using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Common.Security;
using ShopInventory.Features.VendingCashUps;
using ShopInventory.Features.VendingCashUps.Commands.ApproveVendingCashUp;
using ShopInventory.Features.VendingCashUps.Commands.QueryVendingCashUp;
using ShopInventory.Features.VendingCashUps.Commands.SaveVendingCashUp;
using ShopInventory.Features.VendingCashUps.Queries.GetVendingCashUpSheet;
using ShopInventory.Features.VendingCashUps.Queries.GetVendingReconciliation;
using ShopInventory.Models;

namespace ShopInventory.Controllers;

/// <summary>
/// Vending cash-ups: a cashier's count at the end of a day held against their sales, and its approval.
/// </summary>
/// <remarks>
/// A portal Cashier or Admin keys a count; an Admin who did not key or edit it approves or queries it.
/// Managers read only. Who did what is judged from the signed-in user's account, never the API key.
/// </remarks>
[Route("api/vending/cash-ups")]
[Authorize(Policy = "ApiAccess")]
[Authorize(Roles = $"{ApplicationRoles.Admin},{ApplicationRoles.Manager},{ApplicationRoles.Cashier}")]
public class VendingCashUpsController(ISender mediator) : ApiControllerBase
{
    /// <summary>
    /// Every cashier-day in a range (at most 92 days, both ends inclusive) that had vending sales or a
    /// count: expected, counted, banked, variance and status. A day with sales and no count has status
    /// <c>NotCashedUp</c> and no counted figures.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(VendingReconciliationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetReconciliation(
        [FromQuery] DateTime from,
        [FromQuery] DateTime to,
        [FromQuery] string? warehouseCode,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetVendingReconciliationQuery(from, to, warehouseCode), cancellationToken);
        return result.Match(Ok, Problem);
    }

    /// <summary>One cashier's day at one warehouse, counted or not, with the sales it is counted against.</summary>
    [HttpGet("sheet")]
    [ProducesResponseType(typeof(VendingCashUpSheetDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSheet(
        [FromQuery] Guid cashierUserId,
        [FromQuery] string warehouseCode,
        [FromQuery] DateTime date,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new GetVendingCashUpSheetQuery(cashierUserId, warehouseCode, date, UserClaimReader.GetUserId(User)),
            cancellationToken);
        return result.Match(Ok, Problem);
    }

    /// <summary>
    /// Keys or re-keys a count. <c>expectedRevision</c> is the sheet's revision (0 for a day not yet
    /// counted); a count changed since is refused with 409. An approved count cannot be changed.
    /// </summary>
    [HttpPut]
    [Authorize(Roles = $"{ApplicationRoles.Admin},{ApplicationRoles.Cashier}")]
    [ProducesResponseType(typeof(VendingCashUpSheetDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Save(
        [FromBody] SaveVendingCashUpRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new SaveVendingCashUpCommand(
                request.CashierUserId,
                request.WarehouseCode ?? string.Empty,
                request.BusinessDate,
                request.ExpectedRevision,
                request.Currencies ?? [],
                request.Tenders ?? [],
                request.VarianceReason,
                request.Note,
                UserClaimReader.GetUserId(User)),
            cancellationToken);
        return result.Match(Ok, Problem);
    }

    /// <summary>
    /// Approves a count. Admin only, and never by anyone who keyed or edited it. Refused with 409 when
    /// sales changed since the count, and 400 when it shows a variance with no reason.
    /// </summary>
    [HttpPost("{id:int}/approve")]
    [Authorize(Roles = ApplicationRoles.Admin)]
    [ProducesResponseType(typeof(VendingCashUpSheetDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Approve(
        int id,
        [FromBody] DecideVendingCashUpRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new ApproveVendingCashUpCommand(id, request.ExpectedRevision, UserClaimReader.GetUserId(User)),
            cancellationToken);
        return result.Match(Ok, Problem);
    }

    /// <summary>Sends a count back with a question. Admin only; the note is required.</summary>
    [HttpPost("{id:int}/query")]
    [Authorize(Roles = ApplicationRoles.Admin)]
    [ProducesResponseType(typeof(VendingCashUpSheetDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Query(
        int id,
        [FromBody] DecideVendingCashUpRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new QueryVendingCashUpCommand(id, request.ExpectedRevision, request.Note, UserClaimReader.GetUserId(User)),
            cancellationToken);
        return result.Match(Ok, Problem);
    }
}

/// <summary>A count as the portal submits it.</summary>
public record SaveVendingCashUpRequest(
    Guid CashierUserId,
    string? WarehouseCode,
    DateTime BusinessDate,
    int ExpectedRevision,
    List<SaveVendingCashUpCurrency>? Currencies,
    List<SaveVendingCashUpTender>? Tenders,
    string? VarianceReason,
    string? Note);

/// <summary>An approval or a query, carrying the revision the Admin was looking at.</summary>
public record DecideVendingCashUpRequest(int ExpectedRevision, string? Note = null);
