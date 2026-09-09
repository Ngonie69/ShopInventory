using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Models.Revmax;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// What <see cref="RevmaxFiscalizationService"/> puts on the wire, and the two guards that stop a
/// second fiscal receipt being filed for one invoice.
/// </summary>
/// <remarks>
/// Every assertion here is about a value ZIMRA sees. A receipt filed with the wrong numbers cannot be
/// corrected — only reversed with a manual credit note — so these are checked against the payload
/// itself rather than against the service's own report of what it did.
/// </remarks>
public class RevmaxFiscalPayloadTests
{
    private static readonly RevmaxSettings Settings = new()
    {
        Enabled = true,
        BaseUrl = "http://revmax.invalid",
        DefaultCurrency = "USD",
        DefaultBranchName = "Kefalos",
        DefaultRefDeviceId = 22862,
        TaxIdMappings = new(StringComparer.OrdinalIgnoreCase) { ["O01"] = 1, ["O8"] = 1, ["O0"] = 2 },
        DefaultTaxId = 1
    };

    private static readonly TaxSettings Tax = new()
    {
        VatRate = 0.155m,
        RatesByTaxCode = new(StringComparer.OrdinalIgnoreCase) { ["O01"] = 0.155m, ["O0"] = 0m }
    };

    [Fact]
    public async Task A_line_declares_its_amount_on_the_same_tax_basis_as_its_price()
    {
        // PRICE is tax-inclusive. An AMT taken from SAP's net LineTotal beside it would declare a net
        // amount against a gross unit price on one receipt line, and the lines would sum short of
        // InvoiceAmount by exactly the VAT.
        var client = new RecordingRevmaxClient();
        var result = await Service(client).FiscalizeInvoiceAsync(Invoice());

        Assert.True(result.Success);

        var line = client.LastInvoice!.ItemsXml as List<RevmaxRequestItem>;
        Assert.NotNull(line);

        // 2 x 50.00 gross = 100.00, not SAP's net LineTotal of 86.58.
        Assert.Equal("50.00", line![0].Price);
        Assert.Equal("100.00", line[0].Amt);
        Assert.Equal("Test Customer", client.LastInvoice.CustomerName);

        // Present and empty rather than omitted: the device dereferences these and an absent one
        // comes back as a null reference error that reads like a fault on their side.
        Assert.Equal(string.Empty, client.LastInvoice.CustomerVatNumber);
        Assert.Equal(string.Empty, client.LastInvoice.CustomerBPN);
    }

    [Fact]
    public async Task A_discounted_line_declares_what_was_charged_not_the_list_price()
    {
        // SAP's GrossPrice is the PRE-discount gross price off the price list. On real invoice 769617
        // it is 2.76 against a PriceAfterVAT of 1.38 — reading it declared 845.26 to ZIMRA for an
        // invoice whose total is 422.89, near enough double. PriceAfterVat is the only field that is
        // both gross and net-of-discount.
        var client = new RecordingRevmaxClient();
        var invoice = Invoice();
        invoice.Lines![0] = new InvoiceLineDto
        {
            LineNum = 0, ItemCode = "YOG051", ItemDescription = "500g Aloe Vera Orange Yoghurt",
            Quantity = 71m,
            UnitPrice = 2.39m,        // pre-discount net
            GrossPrice = 2.76045m,    // pre-discount gross — the trap
            PriceAfterVat = 1.38m,    // post-discount gross — the truth
            GrossTotal = 97.98m,
            LineTotal = 84.83m,
            DiscountPercent = 50.008159m,
            VatGroup = "O01"
        };

        await Service(client).FiscalizeInvoiceAsync(invoice);

        var line = ((List<RevmaxRequestItem>)client.LastInvoice!.ItemsXml!)[0];
        Assert.Equal("1.38", line.Price);
        Assert.Equal("97.98", line.Amt);
    }

    [Fact]
    public async Task The_tax_code_is_read_from_the_vat_group_where_sap_actually_puts_it()
    {
        // SAP returns TaxCode null on a marketing document line and puts the code in VatGroup. Reading
        // TaxCode alone matched nothing, so every line fell to the standard-rated default — declaring
        // a zero-rated line to ZIMRA at 15.5%.
        var client = new RecordingRevmaxClient();
        var invoice = Invoice();
        invoice.Lines![1] = new InvoiceLineDto
        {
            LineNum = 1, ItemCode = "NRI049", ItemDescription = "Zero rated",
            Quantity = 1m, UnitPrice = 15.50m, PriceAfterVat = 15.50m, GrossTotal = 15.50m,
            LineTotal = 15.50m,
            TaxCode = null,
            VatGroup = "O0"
        };

        await Service(client).FiscalizeInvoiceAsync(invoice);

        var line = ((List<RevmaxRequestItem>)client.LastInvoice!.ItemsXml!)[1];
        Assert.Equal("2", line.Tax);
        Assert.Equal("0", line.TaxR);
    }

    [Fact]
    public async Task Every_line_declares_an_amount_equal_to_quantity_times_price()
    {
        // Not a style rule. REVMax recomputes each line total from QTY and PRICE and discards the AMT
        // it was sent -- invoice 769617's last line went out as 1.91 and was stored as 1.89. Any AMT
        // that disagrees only makes our own record differ from the receipt in the customer's hand.
        var client = new RecordingRevmaxClient();
        var invoice = Invoice();
        invoice.Lines =
        [
            new InvoiceLineDto
            {
                LineNum = 0, ItemCode = "A", Quantity = 3m,
                PriceAfterVat = 1.89m,
                GrossTotal = 99.99m,   // deliberately inconsistent; must be ignored
                LineTotal = 4.91m,
                VatGroup = "O01"
            }
        ];

        await Service(client).FiscalizeInvoiceAsync(invoice);

        var line = ((List<RevmaxRequestItem>)client.LastInvoice!.ItemsXml!)[0];
        Assert.Equal("1.89", line.Price);
        Assert.Equal("5.67", line.Amt);
    }

    [Fact]
    public async Task A_document_with_no_comment_still_carries_one()
    {
        // REVMax refuses a blank InvoiceComment -- "InvoiceComment is null or empty", returned as
        // HTTP 200 with Code "0". It is not marked required in the device's Swagger, and real invoice
        // 769617 carries an empty Comments, so passing SAP's value straight through refused it.
        var client = new RecordingRevmaxClient();
        var invoice = Invoice();
        invoice.Comments = string.Empty;
        invoice.Remarks = null;

        await Service(client).FiscalizeInvoiceAsync(invoice);

        Assert.Equal("Invoice 123456", client.LastInvoice!.InvoiceComment);
    }

    [Fact]
    public async Task A_documents_own_comment_is_preferred_to_the_fallback()
    {
        var client = new RecordingRevmaxClient();
        var invoice = Invoice();
        invoice.Comments = "  Counter sale  ";

        await Service(client).FiscalizeInvoiceAsync(invoice);

        Assert.Equal("Counter sale", client.LastInvoice!.InvoiceComment);
    }

    [Fact]
    public async Task A_zero_rated_line_declares_its_own_tax_id_and_rate()
    {
        var client = new RecordingRevmaxClient();
        await Service(client).FiscalizeInvoiceAsync(Invoice());

        var lines = (List<RevmaxRequestItem>)client.LastInvoice!.ItemsXml!;

        Assert.Equal("1", lines[0].Tax);
        Assert.Equal("15.5", lines[0].TaxR);
        Assert.Equal("2", lines[1].Tax);
        Assert.Equal("0", lines[1].TaxR);
    }

    [Fact]
    public async Task Every_numeric_field_goes_on_the_wire_as_a_string()
    {
        // The device's contract types amounts, quantities and rates as strings. A number where it
        // wants a string is refused, and the refusal reads as a validation error about the invoice.
        var client = new RecordingRevmaxClient();
        await Service(client).FiscalizeInvoiceAsync(Invoice());

        var body = JsonDocument.Parse(JsonSerializer.Serialize(client.LastInvoice)).RootElement;

        Assert.Equal(JsonValueKind.String, body.GetProperty("InvoiceAmount").ValueKind);
        Assert.Equal(JsonValueKind.String, body.GetProperty("InvoiceTaxAmount").ValueKind);
        Assert.Equal(JsonValueKind.String, body.GetProperty("ItemsXml")[0].GetProperty("QTY").ValueKind);
    }

    [Fact]
    public async Task A_credit_note_references_the_original_receipts_real_fiscal_day()
    {
        // InvoiceData.FiscalDayNo is a compatibility shim hard-coded to 0; the fiscal day is on the
        // response envelope. Reading the shim referenced fiscal day 0 on every credit note filed.
        var client = new RecordingRevmaxClient
        {
            KnownInvoice = new InvoiceResponse
            {
                Code = "1",
                DeviceID = "22862",
                FiscalDay = "524",
                Data = new InvoiceData { ReceiptGlobalNo = 216080, ReceiptCounter = 41 }
            }
        };

        var result = await Service(client).FiscalizeCreditNoteAsync(CreditNote(), "123456");

        Assert.True(result.Success);
        Assert.Equal(524, client.LastCreditNote!.refFiscalDayNo);
        Assert.Equal(216080, client.LastCreditNote.refReceiptGlobalNo);
        Assert.Equal(22862, client.LastCreditNote.refDeviceId);
        Assert.Equal("02", client.LastCreditNote.Istatus);
    }

    [Fact]
    public async Task A_credit_note_is_refused_when_the_device_holds_no_original()
    {
        // Filing it anyway would put an unlinked credit note at ZIMRA, and it cannot be withdrawn.
        var client = new RecordingRevmaxClient();

        var result = await Service(client).FiscalizeCreditNoteAsync(CreditNote(), "123456");

        Assert.False(result.Success);
        Assert.Equal("ORIGINAL_RECEIPT_NOT_FOUND", result.ErrorCode);
        Assert.Null(client.LastCreditNote);
    }

    [Fact]
    public async Task A_submission_that_times_out_is_caught_and_reconciled_not_left_to_escape()
    {
        // An HttpClient timeout arrives as TaskCanceledException, which derives from
        // OperationCanceledException. A filter that excludes the base type lets every timeout escape
        // unhandled — and a timeout is exactly the case where the receipt may already be filed.
        //
        // Here the device answers afterwards and says it holds nothing, which settles it: nothing was
        // filed, so this is an ordinary retryable failure rather than an unknown state.
        var client = new RecordingRevmaxClient { ThrowOnPost = new TaskCanceledException("timeout") };

        var result = await Service(client).FiscalizeInvoiceAsync(Invoice());

        Assert.False(result.Success);
        Assert.False(result.RequiresReconciliation);
        Assert.Equal("REVMAX_UNAVAILABLE", result.ErrorCode);
    }

    [Fact]
    public async Task A_timeout_the_device_cannot_then_be_asked_about_is_left_for_reconciliation()
    {
        // The submission may or may not have filed a receipt and the device cannot say which. This is
        // the one outcome that must never be retried automatically.
        var client = new RecordingRevmaxClient
        {
            ThrowOnPost = new TaskCanceledException("timeout"),
            ThrowOnGet = new HttpRequestException("unreachable")
        };

        var result = await Service(client).FiscalizeInvoiceAsync(Invoice());

        Assert.False(result.Success);
        Assert.True(result.RequiresReconciliation);
        Assert.Equal("REVMAX_INDETERMINATE", result.ErrorCode);
    }

    [Fact]
    public async Task A_receipt_the_device_filed_despite_a_failed_call_is_adopted_not_signed_again()
    {
        var client = new RecordingRevmaxClient
        {
            ThrowOnPost = new HttpRequestException("connection reset"),
            KnownInvoice = new InvoiceResponse
            {
                Code = "1",
                FiscalDay = "524",
                DeviceSerialNumber = "8DE6996C0188",
                Data = new InvoiceData { ReceiptGlobalNo = 216081, ReceiptCounter = 42 }
            }
        };

        var result = await Service(client).FiscalizeInvoiceAsync(Invoice());

        Assert.True(result.Success);
        Assert.False(result.RequiresReconciliation);
        Assert.Equal("216081", result.ReceiptGlobalNo);
    }

    [Fact]
    public async Task Not_being_able_to_ask_never_reads_as_nothing_is_there()
    {
        // The one place that must throw. Answering null would let the caller sign a second receipt for
        // a sale that may already have one.
        var client = new RecordingRevmaxClient { ThrowOnGet = new HttpRequestException("unreachable") };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(client).FindPreSapReceiptAsync("VAN-0001"));
    }

    private static RevmaxFiscalizationService Service(IRevmaxClient client) =>
        new(client,
            Options.Create(Settings),
            Options.Create(Tax),
            Options.Create(new FiscalisationSettings { Provider = FiscalisationProvider.Revmax }),
            NullLogger<RevmaxFiscalizationService>.Instance);

    private static InvoiceDto Invoice() => new()
    {
        DocEntry = 4242,
        DocNum = 123456,
        CardCode = "SPA059",
        CardName = "Test Customer",
        DocCurrency = "USD",
        DocTotal = 115.50m,
        VatSum = 15.50m,
        Lines =
        [
            new InvoiceLineDto
            {
                LineNum = 0, ItemCode = "CHE011", ItemDescription = "Feta 1kg",
                Quantity = 2m, UnitPrice = 43.29m, GrossPrice = 50.00m,
                LineTotal = 86.58m, TaxCode = "O01"
            },
            new InvoiceLineDto
            {
                LineNum = 1, ItemCode = "NRI049", ItemDescription = "Zero rated",
                Quantity = 1m, UnitPrice = 15.50m, GrossPrice = 0m,
                LineTotal = 15.50m, TaxCode = "O0"
            }
        ]
    };

    private static InvoiceDto CreditNote()
    {
        var creditNote = Invoice();
        creditNote.DocNum = 999001;
        creditNote.DocTotal = -115.50m;
        creditNote.VatSum = -15.50m;
        return creditNote;
    }

    /// <summary>Records what the service tried to send, and never sends anything.</summary>
    private sealed class RecordingRevmaxClient : IRevmaxClient
    {
        public TransactMRequest? LastInvoice { get; private set; }

        public TransactMExtRequest? LastCreditNote { get; private set; }

        public InvoiceResponse? KnownInvoice { get; init; }

        public Exception? ThrowOnPost { get; init; }

        public Exception? ThrowOnGet { get; init; }

        public Task<TransactMResponse?> TransactMAsync(
            TransactMRequest request, CancellationToken cancellationToken = default)
        {
            if (ThrowOnPost is not null)
            {
                throw ThrowOnPost;
            }

            LastInvoice = request;
            return Task.FromResult<TransactMResponse?>(new TransactMResponse
            {
                Code = "1", Message = "Success", FiscalDay = "524", ReceiptGlobalNo = "216090"
            });
        }

        public Task<TransactMExtResponse?> TransactMExtAsync(
            TransactMExtRequest request, CancellationToken cancellationToken = default)
        {
            if (ThrowOnPost is not null)
            {
                throw ThrowOnPost;
            }

            LastCreditNote = request;
            LastInvoice = request;
            return Task.FromResult<TransactMExtResponse?>(new TransactMExtResponse
            {
                Code = "1", Message = "Success", FiscalDay = "524", ReceiptGlobalNo = "216091"
            });
        }

        public Task<InvoiceResponse?> GetInvoiceAsync(
            string invoiceNumber, CancellationToken cancellationToken = default)
        {
            if (ThrowOnGet is not null)
            {
                throw ThrowOnGet;
            }

            // The device's own answer for a number it does not hold.
            return Task.FromResult<InvoiceResponse?>(KnownInvoice ?? new InvoiceResponse
            {
                Code = "0", Message = "Invoice not Found", FiscalDay = "524"
            });
        }

        public Task<CardDetailsResponse?> GetCardDetailsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<DayStatusResponse?> GetDayStatusAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<LicenseResponse?> GetLicenseAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<LicenseResponse?> SetLicenseAsync(
            string license, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ZReportResponse?> GetZReportAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<UnprocessedInvoicesSummaryResponse?> GetUnprocessedInvoicesSummaryAsync(
            string? fiscalDayNumber = null,
            string? fiscalDate = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
