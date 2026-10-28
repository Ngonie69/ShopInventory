using MediatR;
using ShopInventory.Common.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Features.DailyIncomingPayments.Commands.SaveIncomingPaymentGlMapping;
using ShopInventory.Features.DailyIncomingPayments.Commands.SetIncomingPaymentGlMappingActive;
using ShopInventory.Features.DailyIncomingPayments.Queries.GetDailyIncomingPayment;
using ShopInventory.Features.DailyIncomingPayments.Queries.GetDailyIncomingPayments;
using ShopInventory.Features.DailyIncomingPayments.Queries.GetIncomingPaymentGlMappings;

namespace ShopInventory.Controllers;

/// <summary>
/// The one incoming payment each business partner gets per day for its till, vending and van invoices,
/// and the G/L accounts each partner's payment posts to.
/// </summary>
[Route("api/daily-incoming-payments")]
[Authorize(Policy = "ApiAccess")]
public class DailyIncomingPaymentsController(IMediator mediator, ICallerAccountReader callerAccounts) : ApiControllerBase
{
    /// <summary>
    /// Daily incoming payments for trading days <paramref name="from"/> to <paramref name="to"/>, newest first
    /// </summary>
    [Authorize(Roles = "Admin,Manager,Cashier")]
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] DateTime from,
        [FromQuery] DateTime to,
        [FromQuery] string? cardCode,
        [FromQuery] string? status,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetDailyIncomingPaymentsQuery(from, to, cardCode, status), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// One daily incoming payment with the invoices it settles and the sales behind them
    /// </summary>
    [Authorize(Roles = "Admin,Manager,Cashier")]
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetDailyIncomingPaymentQuery(id), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Every business partner's cash and electronic G/L accounts for its daily incoming payment
    /// </summary>
    [Authorize(Roles = "Admin,Manager,Cashier")]
    [HttpGet("gl-mappings")]
    public async Task<IActionResult> GetGlMappings(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetIncomingPaymentGlMappingsQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Creates or replaces a business partner's G/L accounts, run and email recipients. Both accounts are
    /// checked in SAP first.
    /// </summary>
    [Authorize(Roles = "Admin")]
    [HttpPut("gl-mappings/{cardCode}")]
    public async Task<IActionResult> SaveGlMapping(
        string cardCode,
        [FromBody] SaveIncomingPaymentGlMappingRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new SaveIncomingPaymentGlMappingCommand(
                cardCode,
                request.CardName,
                request.CashAccount,
                request.ElectronicAccount,
                request.Run,
                request.NotifyEmails,
                request.IsActive,
                await ActorAsync(cancellationToken)),
            cancellationToken);

        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Switches one business partner's daily incoming payment on or off. Off leaves its invoices open in
    /// SAP; on pays its invoices from the last <c>DailyPaymentLookbackDays</c> days at the next run.
    /// </summary>
    [Authorize(Roles = "Admin")]
    [HttpPut("gl-mappings/{cardCode}/active")]
    public async Task<IActionResult> SetGlMappingActive(
        string cardCode,
        [FromBody] SetIncomingPaymentGlMappingActiveRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new SetIncomingPaymentGlMappingActiveCommand(cardCode, request.IsActive, await ActorAsync(cancellationToken)),
            cancellationToken);

        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// The person who made the change, from their account. <c>User.Identity.Name</c> names the Web's
    /// integration key, which rides on the same request ahead of the user's token.
    /// </summary>
    private async Task<string?> ActorAsync(CancellationToken cancellationToken)
    {
        var caller = await callerAccounts.ReadAsync(User, cancellationToken);
        return caller.IsError || caller.Value.IsServiceCaller
            ? User.Identity?.Name
            : caller.Value.Username;
    }
}
