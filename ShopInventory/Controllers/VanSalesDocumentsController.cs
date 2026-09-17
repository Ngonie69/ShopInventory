using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Authentication;
using ShopInventory.Features.VanSalesDocuments.Queries.GetVanSalesCreditNotes;
using ShopInventory.Features.VanSalesDocuments.Queries.GetVanSalesInvoice;
using ShopInventory.Features.VanSalesDocuments.Queries.GetVanSalesInvoices;
using ShopInventory.Features.VanSalesReports;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Controllers;

/// <summary>
/// The documents the van sales app created, for the portal's Van Sales section.
/// </summary>
/// <remarks>
/// Beside <c>VanSalesReportController</c> under the same route and the same read audit, and apart from it
/// because these are documents rather than reports, and gated on viewing invoices rather than on viewing
/// van attendance.
/// </remarks>
[ServiceFilter(typeof(VanSalesPortalReadAuditFilter))]
[Route("api/van-sales")]
[Authorize(Policy = "ApiAccess")]
[Produces("application/json")]
public class VanSalesDocumentsController(IMediator mediator) : ApiControllerBase
{
    /// <summary>Invoices the app created in a period, newest first.</summary>
    /// <param name="fromDate">Inclusive CAT trading day. Defaults to seven days back.</param>
    /// <param name="toDate">Inclusive CAT trading day. Defaults to today.</param>
    /// <param name="repUserId">One rep, or all.</param>
    /// <param name="state">One of Complete, AwaitingSap, NotFiscalised, InProgress, NeedsAttention.</param>
    /// <param name="search">Van order, customer, rep, receipt number or SAP number.</param>
    /// <param name="page">1-based.</param>
    /// <param name="pageSize">At most 200.</param>
    /// <param name="channel">Online or Offline, or both.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("invoices")]
    [RequirePermission(Permission.ViewInvoices)]
    [ProducesResponseType(typeof(VanSalesInvoicesResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetInvoices(
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        [FromQuery] Guid? repUserId = null,
        [FromQuery] string? state = null,
        [FromQuery] string? search = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? channel = null,
        CancellationToken cancellationToken = default)
    {
        var today = AuditService.ToCAT(DateTime.UtcNow).Date;

        var result = await mediator.Send(
            new GetVanSalesInvoicesQuery(
                fromDate?.Date ?? today.AddDays(-7),
                toDate?.Date ?? today,
                repUserId,
                string.IsNullOrWhiteSpace(state) ? null : state,
                search,
                page,
                pageSize,
                string.IsNullOrWhiteSpace(channel) ? null : channel),
            cancellationToken);

        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>One invoice the app created, by van order.</summary>
    [HttpGet("invoices/{reference}")]
    [RequirePermission(Permission.ViewInvoices)]
    [ProducesResponseType(typeof(VanSalesInvoiceDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInvoice(string reference, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetVanSalesInvoiceQuery(reference), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>Credit notes raised against invoices the app created, newest first.</summary>
    /// <param name="fromDate">Inclusive CAT trading day. Defaults to thirty days back.</param>
    /// <param name="toDate">Inclusive CAT trading day. Defaults to today.</param>
    /// <param name="state">One of Complete, AwaitingSap, NotFiscalised, InProgress, NeedsAttention.</param>
    /// <param name="search">Number, customer, van order, SAP number or rep.</param>
    /// <param name="page">1-based.</param>
    /// <param name="pageSize">At most 200.</param>
    /// <param name="origin">SAP or Till, or both.</param>
    /// <param name="includeCancelled">False leaves cancelled SAP memos out. They are counted either way.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("credit-notes")]
    [RequirePermission(Permission.ViewInvoices)]
    [ProducesResponseType(typeof(VanSalesCreditNotesResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetCreditNotes(
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        [FromQuery] string? state = null,
        [FromQuery] string? search = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? origin = null,
        [FromQuery] bool includeCancelled = true,
        CancellationToken cancellationToken = default)
    {
        var today = AuditService.ToCAT(DateTime.UtcNow).Date;

        var result = await mediator.Send(
            new GetVanSalesCreditNotesQuery(
                fromDate?.Date ?? today.AddDays(-30),
                toDate?.Date ?? today,
                string.IsNullOrWhiteSpace(state) ? null : state,
                search,
                page,
                pageSize,
                string.IsNullOrWhiteSpace(origin) ? null : origin,
                includeCancelled),
            cancellationToken);

        return result.Match(value => Ok(value), errors => Problem(errors));
    }
}
