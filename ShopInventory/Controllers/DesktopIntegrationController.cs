using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopIntegration.Commands.CancelQueuedInvoice;
using ShopInventory.Features.DesktopIntegration.Commands.CancelQueuedTransfer;
using ShopInventory.Features.DesktopIntegration.Commands.CancelReservation;
using ShopInventory.Features.DesktopIntegration.Commands.CloseTransferRequest;
using ShopInventory.Features.DesktopIntegration.Commands.ConfirmReservation;
using ShopInventory.Features.DesktopIntegration.Commands.ConvertSalesOrderToInvoice;
using ShopInventory.Features.DesktopIntegration.Commands.ConvertTransferRequest;
using ShopInventory.Features.DesktopIntegration.Commands.CreateInvoiceDirect;
using ShopInventory.Features.DesktopIntegration.Commands.CreateQueuedInvoice;
using ShopInventory.Features.DesktopIntegration.Commands.CreateQueuedTransfer;
using ShopInventory.Features.DesktopIntegration.Commands.CreateReservation;
using ShopInventory.Features.DesktopIntegration.Commands.CreateTransfer;
using ShopInventory.Features.DesktopIntegration.Commands.CreateTransferRequest;
using ShopInventory.Features.DesktopIntegration.Commands.RenewReservation;
using ShopInventory.Features.DesktopIntegration.Commands.RetryQueuedInvoice;
using ShopInventory.Features.DesktopIntegration.Commands.RetryQueuedTransfer;
using ShopInventory.Features.DesktopIntegration.Commands.ValidateTransfer;
using ShopInventory.Features.DesktopIntegration.Queries.DownloadInvoicePdf;
using ShopInventory.Features.DesktopIntegration.Queries.GetAvailableBatches;
using ShopInventory.Features.DesktopIntegration.Queries.GetInvoice;
using ShopInventory.Features.DesktopIntegration.Queries.GetInvoiceByDocNum;
using ShopInventory.Features.DesktopIntegration.Queries.GetInvoicesByCustomer;
using ShopInventory.Features.DesktopIntegration.Queries.GetInvoicesByDateRange;
using ShopInventory.Features.DesktopIntegration.Queries.GetInvoicesRequiringReview;
using ShopInventory.Features.DesktopIntegration.Queries.GetItemStock;
using ShopInventory.Features.DesktopIntegration.Queries.GetPagedInvoices;
using ShopInventory.Features.DesktopIntegration.Queries.GetPagedTransferRequests;
using ShopInventory.Features.DesktopIntegration.Queries.GetPagedTransfers;
using ShopInventory.Features.DesktopIntegration.Queries.GetPendingQueue;
using ShopInventory.Features.DesktopIntegration.Queries.GetPendingTransferQueue;
using ShopInventory.Features.DesktopIntegration.Queries.GetQueueStats;
using ShopInventory.Features.DesktopIntegration.Queries.GetQueueStatus;
using ShopInventory.Features.DesktopIntegration.Queries.GetQueueStatusByReservation;
using ShopInventory.Features.DesktopIntegration.Queries.GetReservation;
using ShopInventory.Features.DesktopIntegration.Queries.GetReservationByReference;
using ShopInventory.Features.DesktopIntegration.Queries.GetReservedStockSummary;
using ShopInventory.Features.DesktopIntegration.Queries.GetTransfer;
using ShopInventory.Features.DesktopIntegration.Queries.GetTransferQueueStats;
using ShopInventory.Features.DesktopIntegration.Queries.GetTransferQueueStatus;
using ShopInventory.Features.DesktopIntegration.Queries.GetTransferRequest;
using ShopInventory.Features.DesktopIntegration.Queries.GetTransferRequestsByWarehouse;
using ShopInventory.Features.DesktopIntegration.Queries.GetTransfersByDateRange;
using ShopInventory.Features.DesktopIntegration.Queries.GetTransfersByWarehouse;
using ShopInventory.Features.DesktopIntegration.Queries.GetTransfersRequiringReview;
using ShopInventory.Features.DesktopIntegration.Queries.ListReservations;
using ShopInventory.Features.DesktopIntegration.Queries.ValidateInvoice;
using ShopInventory.Features.DesktopIntegration.Queries.ValidateStockAvailability;
using ShopInventory.Features.DesktopIntegration.Commands.CreateDesktopSale;
using ShopInventory.Features.DesktopIntegration.Commands.ConsolidateDailySales;
using ShopInventory.Features.DesktopIntegration.Commands.FetchDailyStock;
using ShopInventory.Features.DesktopIntegration.Commands.ProcessTransferEvent;
using ShopInventory.Features.DesktopIntegration.Queries.GenerateEndOfDayReport;
using ShopInventory.Features.DesktopIntegration.Queries.GetLocalStock;
using ShopInventory.Services;
using System.Security.Claims;

namespace ShopInventory.Controllers;

[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
public class DesktopIntegrationController(IMediator mediator) : ApiControllerBase
{
    private string? GetUserId() =>
        User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("client_id")?.Value;

    #region Stock Reservations

    [HttpPost("reservations")]
    public async Task<IActionResult> CreateReservation(
        [FromBody] CreateStockReservationRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new CreateReservationCommand(request, GetUserId()), cancellationToken);
        return result.Match(
            value => CreatedAtAction(nameof(GetReservation), new { reservationId = value.Reservation?.ReservationId }, value),
            errors => Problem(errors));
    }

    [HttpGet("reservations/{reservationId}")]
    public async Task<IActionResult> GetReservation(
        string reservationId,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetReservationQuery(reservationId), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("reservations/by-reference/{externalReferenceId}")]
    public async Task<IActionResult> GetReservationByReference(
        string externalReferenceId,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetReservationByReferenceQuery(externalReferenceId), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("reservations")]
    public async Task<IActionResult> ListReservations(
        [FromQuery] string? sourceSystem = null,
        [FromQuery] string? status = null,
        [FromQuery] string? cardCode = null,
        [FromQuery] string? externalReferenceId = null,
        [FromQuery] bool activeOnly = true,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(
            new ListReservationsQuery(sourceSystem, status, cardCode, externalReferenceId, activeOnly, page, pageSize),
            cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPost("reservations/confirm")]
    public async Task<IActionResult> ConfirmReservation(
        [FromBody] ConfirmReservationRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new ConfirmReservationCommand(request), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPost("reservations/cancel")]
    public async Task<IActionResult> CancelReservation(
        [FromBody] CancelReservationRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new CancelReservationCommand(request), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPost("reservations/renew")]
    public async Task<IActionResult> RenewReservation(
        [FromBody] RenewReservationRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new RenewReservationCommand(request), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    #endregion

    #region Stock Information

    [HttpGet("stock/{warehouseCode}/{itemCode}")]
    public async Task<IActionResult> GetAvailableStock(
        string warehouseCode,
        string itemCode,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetItemStockQuery(warehouseCode, itemCode), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("stock/{warehouseCode}")]
    public async Task<IActionResult> GetAvailableStockBulk(
        string warehouseCode,
        [FromQuery] string? itemCodes = null,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetReservedStockSummaryQuery(warehouseCode, itemCodes), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("stock/{warehouseCode}/{itemCode}/batches")]
    public async Task<IActionResult> GetAvailableBatches(
        string warehouseCode,
        string itemCode,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetAvailableBatchesQuery(warehouseCode, itemCode), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPost("stock/validate")]
    public async Task<IActionResult> ValidateStockAvailability(
        [FromBody] ValidateStockRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new ValidateStockAvailabilityQuery(request), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    #endregion

    #region Quick Invoice (Reserve + Confirm in one call)

    [HttpPost("invoices")]
    public async Task<IActionResult> CreateInvoiceDirect(
        [FromBody] CreateDesktopInvoiceRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new CreateInvoiceDirectCommand(request, GetUserId()), cancellationToken);
        return result.Match(
            value => CreatedAtAction(nameof(GetReservation), new { reservationId = (string?)null }, value),
            errors => Problem(errors));
    }

    #endregion

    #region Queued Invoice (Reserve + Queue for batch posting)

    [HttpPost("invoices/queued")]
    public async Task<IActionResult> CreateQueuedInvoice(
        [FromBody] CreateDesktopInvoiceRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new CreateQueuedInvoiceCommand(request, GetUserId()), cancellationToken);
        return result.Match(value => Accepted(value), errors => Problem(errors));
    }

    [HttpGet("queue/{externalReference}")]
    public async Task<IActionResult> GetQueueStatus(
        string externalReference,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetQueueStatusQuery(externalReference), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("queue/by-reservation/{reservationId}")]
    public async Task<IActionResult> GetQueueStatusByReservation(
        string reservationId,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetQueueStatusByReservationQuery(reservationId), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("queue")]
    public async Task<IActionResult> GetPendingQueue(
        [FromQuery] string? sourceSystem = null,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetPendingQueueQuery(sourceSystem, limit), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("queue/review")]
    public async Task<IActionResult> GetInvoicesRequiringReview(
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetInvoicesRequiringReviewQuery(limit), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("queue/stats")]
    public async Task<IActionResult> GetQueueStats(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetQueueStatsQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpDelete("queue/{externalReference}")]
    public async Task<IActionResult> CancelQueuedInvoice(
        string externalReference,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        var result = await mediator.Send(new CancelQueuedInvoiceCommand(externalReference, userId), cancellationToken);
        return result.Match(_ => NoContent(), errors => Problem(errors));
    }

    [HttpPost("queue/{externalReference}/retry")]
    public async Task<IActionResult> RetryQueuedInvoice(
        string externalReference,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new RetryQueuedInvoiceCommand(externalReference), cancellationToken);
        return result.Match(_ => Ok(new { message = "Invoice will be retried shortly", status = "Pending" }), errors => Problem(errors));
    }

    #endregion

    #region Invoice Retrieval

    [HttpGet("invoices/{docEntry:int}")]
    public async Task<IActionResult> GetInvoice(int docEntry, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetInvoiceQuery(docEntry), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("invoices/by-docnum/{docNum:int}")]
    public async Task<IActionResult> GetInvoiceByDocNum(int docNum, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetInvoiceByDocNumQuery(docNum), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("invoices/customer/{cardCode}")]
    public async Task<IActionResult> GetInvoicesByCustomer(
        string cardCode,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetInvoicesByCustomerQuery(cardCode, fromDate, toDate), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("invoices/date-range")]
    public async Task<IActionResult> GetInvoicesByDateRange(
        [FromQuery] DateTime fromDate,
        [FromQuery] DateTime toDate,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetInvoicesByDateRangeQuery(fromDate, toDate), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("invoices/paged")]
    public async Task<IActionResult> GetPagedInvoices(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetPagedInvoicesQuery(page, pageSize), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("invoices/{docEntry:int}/pdf")]
    public async Task<IActionResult> DownloadInvoicePdf(int docEntry, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new DownloadInvoicePdfQuery(docEntry), cancellationToken);
        return result.Match(
            value => File(value.PdfBytes, "application/pdf", value.FileName),
            errors => Problem(errors));
    }

    [HttpPost("invoices/validate")]
    public async Task<IActionResult> ValidateInvoice(
        [FromBody] CreateDesktopInvoiceRequest request,
        [FromQuery] bool autoAllocateBatches = true,
        [FromQuery] BatchAllocationStrategy allocationStrategy = BatchAllocationStrategy.FEFO,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(
            new ValidateInvoiceQuery(request, autoAllocateBatches, allocationStrategy),
            cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    #endregion

    #region Sales Order to Invoice Conversion

    [HttpPost("sales-orders/convert-to-invoice")]
    public async Task<IActionResult> ConvertSalesOrderToInvoice(
        [FromBody] ConvertSalesOrderToInvoiceRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new ConvertSalesOrderToInvoiceCommand(request, GetUserId()),
            cancellationToken);
        return result.Match(value => Accepted(value), errors => Problem(errors));
    }

    #endregion

    #region Direct Stock Transfers

    [HttpPost("transfers")]
    public async Task<IActionResult> CreateTransferDirect(
        [FromBody] CreateDesktopTransferRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new CreateTransferCommand(request), cancellationToken);
        return result.Match(
            value => CreatedAtAction(nameof(GetTransfer), new { docEntry = value.Transfer?.DocEntry }, value),
            errors => Problem(errors));
    }

    [HttpPost("transfers/validate")]
    public async Task<IActionResult> ValidateTransfer(
        [FromBody] CreateDesktopTransferRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new ValidateTransferCommand(request), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    #endregion

    #region Transfer Retrieval

    [HttpGet("transfers/{docEntry:int}")]
    public async Task<IActionResult> GetTransfer(int docEntry, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetTransferQuery(docEntry), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("transfers/warehouse/{warehouseCode}")]
    public async Task<IActionResult> GetTransfersByWarehouse(
        string warehouseCode,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetTransfersByWarehouseQuery(warehouseCode), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("transfers/warehouse/{warehouseCode}/date-range")]
    public async Task<IActionResult> GetTransfersByDateRange(
        string warehouseCode,
        [FromQuery] DateTime fromDate,
        [FromQuery] DateTime toDate,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(
            new GetTransfersByDateRangeQuery(warehouseCode, fromDate, toDate),
            cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("transfers/warehouse/{warehouseCode}/paged")]
    public async Task<IActionResult> GetPagedTransfers(
        string warehouseCode,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(
            new GetPagedTransfersQuery(warehouseCode, page, pageSize),
            cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    #endregion

    #region Transfer Requests (Approval Workflow)

    [HttpPost("transfer-requests")]
    public async Task<IActionResult> CreateTransferRequest(
        [FromBody] CreateDesktopTransferRequestDto request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new CreateTransferRequestCommand(request, GetUserId()),
            cancellationToken);
        return result.Match(
            value => CreatedAtAction(nameof(GetTransferRequest), new { docEntry = value.DocEntry }, value),
            errors => Problem(errors));
    }

    [HttpGet("transfer-requests/{docEntry:int}")]
    public async Task<IActionResult> GetTransferRequest(int docEntry, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetTransferRequestQuery(docEntry), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("transfer-requests/warehouse/{warehouseCode}")]
    public async Task<IActionResult> GetTransferRequestsByWarehouse(
        string warehouseCode,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetTransferRequestsByWarehouseQuery(warehouseCode), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("transfer-requests/paged")]
    public async Task<IActionResult> GetPagedTransferRequests(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetPagedTransferRequestsQuery(page, pageSize), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPost("transfer-requests/{docEntry:int}/convert")]
    public async Task<IActionResult> ConvertTransferRequest(int docEntry, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new ConvertTransferRequestCommand(docEntry), cancellationToken);
        return result.Match(
            value => CreatedAtAction(nameof(GetTransfer), new { docEntry = value.Transfer?.DocEntry }, value),
            errors => Problem(errors));
    }

    [HttpPost("transfer-requests/{docEntry:int}/close")]
    public async Task<IActionResult> CloseTransferRequest(int docEntry, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new CloseTransferRequestCommand(docEntry), cancellationToken);
        return result.Match(_ => NoContent(), errors => Problem(errors));
    }

    #endregion

    #region Queued Inventory Transfers

    [HttpPost("transfers/queued")]
    public async Task<IActionResult> CreateQueuedTransfer(
        [FromBody] CreateDesktopTransferRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new CreateQueuedTransferCommand(request, GetUserId()),
            cancellationToken);
        return result.Match(value => Accepted(value), errors => Problem(errors));
    }

    [HttpGet("transfer-queue/{externalReference}")]
    public async Task<IActionResult> GetTransferQueueStatus(
        string externalReference,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetTransferQueueStatusQuery(externalReference), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("transfer-queue")]
    public async Task<IActionResult> GetPendingTransferQueue(
        [FromQuery] string? sourceSystem = null,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetPendingTransferQueueQuery(sourceSystem, limit), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("transfer-queue/review")]
    public async Task<IActionResult> GetTransfersRequiringReview(
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetTransfersRequiringReviewQuery(limit), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("transfer-queue/stats")]
    public async Task<IActionResult> GetTransferQueueStats(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetTransferQueueStatsQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpDelete("transfer-queue/{externalReference}")]
    public async Task<IActionResult> CancelQueuedTransfer(
        string externalReference,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        var result = await mediator.Send(new CancelQueuedTransferCommand(externalReference, userId), cancellationToken);
        return result.Match(_ => NoContent(), errors => Problem(errors));
    }

    [HttpPost("transfer-queue/{externalReference}/retry")]
    public async Task<IActionResult> RetryQueuedTransfer(
        string externalReference,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new RetryQueuedTransferCommand(externalReference), cancellationToken);
        return result.Match(_ => Ok(new { message = "Transfer will be retried shortly", status = "Pending" }), errors => Problem(errors));
    }

    #endregion

    #region Daily Stock & Desktop Sales

    /// <summary>
    /// Manually trigger daily stock snapshot fetch from SAP.
    /// </summary>
    [HttpPost("stock/fetch-daily")]
    public async Task<IActionResult> FetchDailyStock(
        [FromBody] FetchDailyStockCommand? command,
        CancellationToken cancellationToken)
    {
        var cmd = command ?? new FetchDailyStockCommand();
        var result = await mediator.Send(cmd, cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Get local stock for a warehouse from today's snapshot (including transfer adjustments).
    /// </summary>
    [HttpGet("stock/{warehouseCode}/local")]
    public async Task<IActionResult> GetLocalStock(
        string warehouseCode,
        [FromQuery] DateTime? snapshotDate,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetLocalStockQuery(warehouseCode, snapshotDate), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Create a desktop sale — validates against local stock, deducts quantities, and fiscalizes immediately.
    /// </summary>
    [HttpPost("sales")]
    public async Task<IActionResult> CreateDesktopSale(
        [FromBody] CreateDesktopSaleRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new CreateDesktopSaleCommand(request, GetUserId()), cancellationToken);
        return result.Match(value => CreatedAtAction(nameof(GetLocalStock), new { warehouseCode = request.WarehouseCode }, value), errors => Problem(errors));
    }

    /// <summary>
    /// Manually trigger end-of-day consolidation — consolidates sales per BP and posts to SAP.
    /// </summary>
    [HttpPost("end-of-day/consolidate")]
    public async Task<IActionResult> ConsolidateDailySales(
        [FromBody] ConsolidateDailySalesCommand? command,
        CancellationToken cancellationToken)
    {
        var cmd = command ?? new ConsolidateDailySalesCommand();
        var result = await mediator.Send(cmd, cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Get end-of-day sales report with consolidation status, fiscal receipts, and payment matching.
    /// </summary>
    [HttpGet("end-of-day/report")]
    public async Task<IActionResult> GetEndOfDayReport(
        [FromQuery] DateTime? reportDate,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GenerateEndOfDayReportQuery(reportDate), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Manually trigger end-of-day report email.
    /// </summary>
    [HttpPost("end-of-day/email-report")]
    public async Task<IActionResult> EmailEndOfDayReport(
        [FromQuery] DateTime? reportDate,
        CancellationToken cancellationToken)
    {
        var consolidationService = HttpContext.RequestServices.GetRequiredService<EndOfDayConsolidationService>();
        await consolidationService.RunConsolidationAndReportAsync(cancellationToken);
        return Ok(new { message = "End-of-day consolidation and report email triggered" });
    }

    /// <summary>
    /// Webhook endpoint for TransferEventListener to notify about stock transfers.
    /// </summary>
    [HttpPost("webhook/transfer-event")]
    [AllowAnonymous]
    public async Task<IActionResult> TransferEventWebhook(
        [FromBody] ProcessTransferEventCommand command,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(command, cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    #endregion
}
