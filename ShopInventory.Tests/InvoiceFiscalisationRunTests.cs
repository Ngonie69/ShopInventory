using System.Net;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;
using Xunit;

namespace ShopInventory.Tests;

/// <summary>
/// The invoice list's bulk fiscalise. Two rules make it safe to offer at all: a thrown call is only
/// "nothing was sent" when the API refused it before submitting, and a run stops at the first outcome
/// nobody could establish rather than sending more invoices into whatever caused it.
/// </summary>
public sealed class InvoiceFiscalisationRunTests
{
    [Theory]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task A_refusal_from_the_api_is_nothing_sent(HttpStatusCode statusCode)
    {
        // The handler's refusals — a consolidated invoice, a sale fiscalised before SAP — are 409s raised
        // before the fiscal service is called. Locking those out as unknown would strand them for good.
        var service = new ScriptedInvoiceService(docEntry => throw new HttpRequestException(
            "Invoice 772394 is the end-of-day consolidation of 14 till sale(s).", null, statusCode));

        var result = await InvoiceFiscalisationRun.FiscaliseAsync(service, Invoice(1));

        Assert.Equal(FiscalRetryOutcome.Blocked, result.Outcome);
        Assert.False(result.MustNotRetry);
        Assert.Contains("consolidation", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task A_server_fault_or_timeout_is_unknown(HttpStatusCode statusCode)
    {
        var service = new ScriptedInvoiceService(docEntry => throw new HttpRequestException(
            "The fiscalisation platform did not answer within 30 seconds.", null, statusCode));

        var result = await InvoiceFiscalisationRun.FiscaliseAsync(service, Invoice(1));

        Assert.Equal(FiscalRetryOutcome.Unknown, result.Outcome);
        Assert.True(result.MustNotRetry);
    }

    [Fact]
    public async Task A_dropped_connection_is_unknown()
    {
        // No status code at all: the request may have been read and acted on before the socket closed.
        var service = new ScriptedInvoiceService(docEntry => throw new TaskCanceledException("The request was canceled."));

        var result = await InvoiceFiscalisationRun.FiscaliseAsync(service, Invoice(1));

        Assert.Equal(FiscalRetryOutcome.Unknown, result.Outcome);
    }

    [Fact]
    public async Task A_run_sends_every_invoice_in_order_and_counts_each_outcome()
    {
        var service = new ScriptedInvoiceService(docEntry => docEntry switch
        {
            2 => new FiscalizationResult { Success = true, Skipped = true, AlreadyFiscalised = true },
            3 => new FiscalizationResult { Success = false, ErrorCode = "RCPT025", Message = "Tax id 3 is not on this device." },
            _ => new FiscalizationResult { Success = true, ReceiptGlobalNo = "88214" }
        });

        var summary = await InvoiceFiscalisationRun.RunAsync(
            service, [Invoice(1), Invoice(2), Invoice(3), Invoice(4)], stopRequested: () => false);

        Assert.Equal([1, 2, 3, 4], service.Sent);
        Assert.Equal(2, summary.Fiscalised);
        Assert.Equal(1, summary.AlreadyFiscalised);
        Assert.Equal(3, Assert.Single(summary.NotFiscalised).Item.DocEntry);
        Assert.Null(summary.Unresolved);
        Assert.Equal(0, summary.NotSent);
    }

    [Fact]
    public async Task A_run_stops_at_the_first_unresolved_outcome()
    {
        var service = new ScriptedInvoiceService(docEntry => docEntry == 2
            ? throw new HttpRequestException("Bad gateway.", null, HttpStatusCode.BadGateway)
            : new FiscalizationResult { Success = true });

        var summary = await InvoiceFiscalisationRun.RunAsync(
            service, [Invoice(1), Invoice(2), Invoice(3), Invoice(4)], stopRequested: () => false);

        Assert.Equal([1, 2], service.Sent);
        Assert.Equal(2, summary.Unresolved?.Item.DocEntry);
        Assert.Equal(2, summary.NotSent);
    }

    [Fact]
    public async Task A_reconciliation_answer_stops_the_run_too()
    {
        var service = new ScriptedInvoiceService(docEntry => docEntry == 1
            ? new FiscalizationResult { Success = false, RequiresReconciliation = true }
            : new FiscalizationResult { Success = true });

        var summary = await InvoiceFiscalisationRun.RunAsync(
            service, [Invoice(1), Invoice(2)], stopRequested: () => false);

        Assert.Equal([1], service.Sent);
        Assert.Equal(FiscalRetryOutcome.Reconcile, summary.Unresolved?.Result.Outcome);
    }

    [Fact]
    public async Task Stopping_takes_effect_between_invoices_not_during_one()
    {
        var stop = false;
        var service = new ScriptedInvoiceService(docEntry => new FiscalizationResult { Success = true });

        var summary = await InvoiceFiscalisationRun.RunAsync(
            service,
            [Invoice(1), Invoice(2), Invoice(3)],
            stopRequested: () => stop,
            onFinished: (invoice, result) =>
            {
                // Pressed while invoice 1 was in flight: it still finishes, and nothing after it is sent.
                stop = true;
                return Task.CompletedTask;
            });

        Assert.Equal([1], service.Sent);
        Assert.Equal(1, summary.Fiscalised);
        Assert.True(summary.StoppedByUser);
        Assert.Equal(2, summary.NotSent);
    }

    private static InvoiceDto Invoice(int docEntry) => new() { DocEntry = docEntry, DocNum = 772390 + docEntry };

    /// <summary>Answers each fiscalise call from a script; everything else is refused.</summary>
    private sealed class ScriptedInvoiceService(Func<int, FiscalizationResult> answer) : IInvoiceService
    {
        public List<int> Sent { get; } = [];

        public Task<FiscalizationResult> FiscalizeInvoiceAsync(int docEntry)
        {
            Sent.Add(docEntry);
            return Task.FromResult(answer(docEntry));
        }

        public Task<InvoiceDto?> GetInvoiceByDocNumAsync(int docNum) => throw new NotSupportedException();

        public Task<InvoiceListResponse?> GetInvoicesAsync(
            int page = 1, int pageSize = 20, int? docNum = null, string? cardCode = null,
            DateTime? fromDate = null, DateTime? toDate = null, bool? vanSalesOnly = null) =>
            throw new NotSupportedException();

        public Task<InvoiceDto?> GetInvoiceByDocEntryAsync(int docEntry) => throw new NotSupportedException();

        public Task<CancelInvoiceOutcome> CancelInvoiceAsync(
            int docEntry, string reason, string? comments, string? clientRequestId = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<InvoiceDateResponse?> GetInvoicesByCustomerAsync(
            string cardCode, DateTime? fromDate = null, DateTime? toDate = null,
            int? page = null, int? pageSize = null, bool includeLines = false) =>
            throw new NotSupportedException();

        public Task<InvoiceDateResponse?> GetOpenInvoicesByCustomersAsync(IEnumerable<string> cardCodes) =>
            throw new NotSupportedException();

        public Task<InvoiceDateResponse?> GetInvoicesByDateAsync(DateTime date) => throw new NotSupportedException();

        public Task<InvoiceDateResponse?> GetInvoicesByDateRangeAsync(
            DateTime fromDate, DateTime toDate, int? page = null, int? pageSize = null) =>
            throw new NotSupportedException();

        public Task<(bool Success, string Message, InvoiceDto? Invoice, FiscalizationResult? Fiscalization)>
            CreateInvoiceAsync(CreateInvoiceRequest request) => throw new NotSupportedException();

        public Task<byte[]?> GetInvoicePdfAsync(int docEntry, string? fiscalQrCode = null) =>
            throw new NotSupportedException();
    }
}
