using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopIntegration.Commands.CancelQueuedInvoice;
using ShopInventory.Features.DesktopIntegration.Commands.CreateDesktopSale;
using ShopInventory.Features.DesktopIntegration.Commands.CreateInvoiceDirect;
using ShopInventory.Features.DesktopIntegration.Commands.RetryQueuedInvoice;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins what the desktop money events write into the audit trail.
///
/// The blanket filter records that an endpoint was called and how it answered. For these it is not
/// enough: the sale, the two invoice paths and the consolidation all carry their subject in the
/// request body, so an endpoint name alone says something was sold without saying what, to whom, or
/// for how much. That was the state of the whole surface until now — a till could sell, invoice,
/// cancel a queued invoice or consolidate the day's takings into SAP and leave nothing behind but
/// the documents themselves.
/// </summary>
public sealed class DesktopIntegrationAuditRowTests
{
    // ---- The sale -------------------------------------------------------------------------------

    [Fact]
    public void A_sale_names_the_customer_the_warehouse_and_the_money()
    {
        var text = CreateDesktopSaleHandler.Describe(
            Sale(reference: "TILL-0007", paymentMethod: "Cash"),
            new CreateDesktopSaleResult(Sold("TILL-0007", 143.75m, 18.75m), WasExisting: false));

        Assert.Contains("Sold TILL-0007 to ABS006 from KEFSHOP", text);
        Assert.Contains("143.75 incl. 18.75 VAT", text);
        Assert.Contains("tendered Cash", text);
        Assert.Contains("receipt KEF-0002", text);
    }

    /// <summary>
    /// A replay is answered with the sale the first request made. Recording it as a creation would
    /// put two sales in the trail where the till made one — the exact confusion the 200/201 split on
    /// this endpoint exists to remove.
    /// </summary>
    [Fact]
    public void A_replayed_sale_is_not_recorded_as_a_second_sale()
    {
        var text = CreateDesktopSaleHandler.Describe(
            Sale(reference: "TILL-0007", paymentMethod: "Cash"),
            new CreateDesktopSaleResult(Sold("TILL-0007", 143.75m, 18.75m), WasExisting: true));

        Assert.StartsWith("Replayed TILL-0007", text);
        Assert.DoesNotContain("Sold", text);
    }

    [Fact]
    public void A_refused_sale_still_names_the_reference_it_was_refused_under()
    {
        var text = CreateDesktopSaleHandler.Describe(
            Sale(reference: "TILL-0007", paymentMethod: "Card"),
            ShopInventory.Common.Errors.Errors.DesktopSales.StockLedgerRefused("NRI049 short by 3"));

        Assert.Contains("Refused sale TILL-0007", text);
        Assert.Contains("tendered Card", text);
    }

    /// <summary>
    /// A till may leave the reference to the server. The row still has to read as a sentence rather
    /// than trail off after "Refused sale ".
    /// </summary>
    [Fact]
    public void A_refused_sale_with_no_reference_says_so()
    {
        var text = CreateDesktopSaleHandler.Describe(
            Sale(reference: null, paymentMethod: "Cash"),
            ShopInventory.Common.Errors.Errors.DesktopSales.VendorRequired);

        Assert.Contains("Refused an unreferenced sale", text);
    }

    // ---- The invoice posted on the request ------------------------------------------------------

    [Fact]
    public void A_posted_invoice_names_the_sap_document()
    {
        var text = CreateInvoiceDirectHandler.Describe(
            Invoice(),
            new ConfirmReservationResponseDto { Success = true, SAPDocNum = 40122, SAPDocEntry = 9871 });

        Assert.Contains("posted to SAP as DocNum 40122", text);
        Assert.Contains("DocEntry 9871", text);
    }

    /// <summary>
    /// A deferral and a posting are the same 200 to the caller, and only one of them means the money
    /// reached SAP. The row is the only place that difference survives.
    /// </summary>
    [Fact]
    public void A_deferred_invoice_is_not_recorded_as_posted()
    {
        var text = CreateInvoiceDirectHandler.Describe(
            Invoice(),
            new ConfirmReservationResponseDto
            {
                Success = true,
                WasQueued = true,
                QueueId = 55,
                ReservationId = "RSV-11"
            });

        Assert.Contains("deferred to queue entry 55", text);
        Assert.DoesNotContain("posted to SAP", text);
    }

    // ---- The queue -------------------------------------------------------------------------------

    /// <summary>
    /// Cancelling destroys the queue entry and releases its reservation, so the status it was
    /// cancelled from survives nowhere else.
    /// </summary>
    [Fact]
    public async Task Cancelling_a_queued_invoice_records_the_status_it_was_cancelled_from()
    {
        var audit = new RecordingAuditService();
        var handler = new CancelQueuedInvoiceHandler(
            Queue(Status("DESKTOP-INV-001", "Pending", canCancel: true), cancelled: true),
            Reservations(),
            audit,
            NullLogger<CancelQueuedInvoiceHandler>.Instance);

        var result = await handler.Handle(
            new CancelQueuedInvoiceCommand("DESKTOP-INV-001", "till01"), CancellationToken.None);

        Assert.False(result.IsError);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.CancelQueuedDesktopInvoice, entry.Action);
        Assert.Equal("DESKTOP-INV-001", entry.EntityId);
        Assert.True(entry.Success);
        Assert.Contains("cancelled from status Pending", entry.Details);
        Assert.Contains("releasing reservation RSV-9", entry.Details);
    }

    [Fact]
    public async Task A_refused_cancellation_is_recorded_as_a_failure()
    {
        var audit = new RecordingAuditService();
        var handler = new CancelQueuedInvoiceHandler(
            Queue(Status("DESKTOP-INV-002", "Processing", canCancel: false), cancelled: false),
            Reservations(),
            audit,
            NullLogger<CancelQueuedInvoiceHandler>.Instance);

        var result = await handler.Handle(
            new CancelQueuedInvoiceCommand("DESKTOP-INV-002", "till01"), CancellationToken.None);

        Assert.True(result.IsError);

        var entry = Assert.Single(audit.Entries);
        Assert.False(entry.Success);
        Assert.Contains("not cancellable from status Processing", entry.Details);
        Assert.NotNull(entry.Error);
    }

    /// <summary>
    /// A queue entry looks the same whether it was retried once or six times, so the row is where
    /// the count lives.
    /// </summary>
    [Fact]
    public async Task Retrying_a_queued_invoice_records_the_status_it_was_retried_from()
    {
        var audit = new RecordingAuditService();
        var handler = new RetryQueuedInvoiceHandler(
            Queue(Status("DESKTOP-INV-003", "Failed", isFailed: true), retried: true),
            audit,
            NullLogger<RetryQueuedInvoiceHandler>.Instance);

        var result = await handler.Handle(
            new RetryQueuedInvoiceCommand("DESKTOP-INV-003"), CancellationToken.None);

        Assert.False(result.IsError);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.RetryQueuedDesktopInvoice, entry.Action);
        Assert.Contains("requeued from status Failed", entry.Details);
    }

    [Fact]
    public async Task A_queue_entry_that_is_not_there_is_recorded_as_a_failure()
    {
        var audit = new RecordingAuditService();
        var handler = new RetryQueuedInvoiceHandler(
            Queue(status: null), audit, NullLogger<RetryQueuedInvoiceHandler>.Instance);

        var result = await handler.Handle(
            new RetryQueuedInvoiceCommand("DESKTOP-INV-404"), CancellationToken.None);

        Assert.True(result.IsError);

        var entry = Assert.Single(audit.Entries);
        Assert.False(entry.Success);
        Assert.Equal("DESKTOP-INV-404", entry.EntityId);
    }

    // ---- Fixtures --------------------------------------------------------------------------------

    private static CreateDesktopSaleCommand Sale(string? reference, string paymentMethod) =>
        new(
            new CreateDesktopSaleRequest
            {
                ExternalReferenceId = reference,
                CardCode = "ABS006",
                PaymentMethod = paymentMethod,
                Lines = [new CreateDesktopSaleLineRequest()]
            },
            Guid.NewGuid());

    private static DesktopSaleResponseDto Sold(string reference, decimal total, decimal vat) =>
        new()
        {
            ExternalReferenceId = reference,
            CardCode = "ABS006",
            WarehouseCode = "KEFSHOP",
            TotalAmount = total,
            VatAmount = vat,
            FiscalizationStatus = "Fiscalized",
            FiscalReceiptNumber = "KEF-0002"
        };

    private static CreateInvoiceDirectCommand Invoice() =>
        new(
            new CreateDesktopInvoiceRequest
            {
                CardCode = "ABS006",
                Lines = [new CreateDesktopInvoiceLineRequest()]
            },
            "till01");

    private static InvoiceQueueStatusDto Status(
        string reference, string status, bool canCancel = false, bool isFailed = false) =>
        new()
        {
            ExternalReference = reference,
            ReservationId = "RSV-9",
            Status = status,
            CanCancel = canCancel,
            CanRetry = isFailed,
            IsFailed = isFailed
        };

    private static IInvoiceQueueService Queue(
        InvoiceQueueStatusDto? status, bool cancelled = false, bool retried = false) =>
        StubProxy.For<IInvoiceQueueService>((method, _) => method.Name switch
        {
            nameof(IInvoiceQueueService.GetQueueStatusAsync) => Task.FromResult(status),
            nameof(IInvoiceQueueService.CancelQueuedInvoiceAsync) => Task.FromResult(cancelled),
            nameof(IInvoiceQueueService.RetryInvoiceAsync) => Task.FromResult(retried),
            _ => throw new InvalidOperationException($"Unexpected call to {method.Name}.")
        });

    private static IStockReservationService Reservations() =>
        StubProxy.For<IStockReservationService>((method, _) =>
            method.Name == nameof(IStockReservationService.CancelReservationAsync)
                ? Task.FromResult(new StockReservationResponseDto { Success = true })
                : throw new InvalidOperationException($"Unexpected call to {method.Name}."));
}
