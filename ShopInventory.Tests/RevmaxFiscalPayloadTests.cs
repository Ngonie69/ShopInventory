using System.Globalization;
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
    public async Task A_zero_rated_line_the_device_taxed_anyway_is_reported_not_buried()
    {
        // The one failure this integration cannot otherwise see. REVMax answers "Upload Success" with
        // no tax breakdown, so a wrong TAX id is invisible at the point of sending: the receipt is
        // filed, the customer holds it, and the VAT declared to ZIMRA is simply wrong. The feed
        // already writing to this device does exactly that -- SAP 771225 was wholly zero-rated
        // (VatSum 0.00) and its receipt declared 0.27 of VAT, with nothing reported anywhere.
        var client = new RecordingRevmaxClient
        {
            FiledReceipt = new InvoiceResponse
            {
                Code = "1",
                FiscalDay = "524",
                Data = new InvoiceData
                {
                    ReceiptGlobalNo = 216204,
                    ReceiptLines =
                    [
                        new ReceiptLine { ReceiptLineNo = 0, TaxID = 515, TaxPercent = 15.5m },
                        // Declared zero-rated; the device recorded it standard-rated.
                        new ReceiptLine { ReceiptLineNo = 1, TaxID = 515, TaxPercent = 15.5m }
                    ]
                }
            }
        };

        var invoice = Invoice();
        invoice.Lines![1].VatGroup = "O0";
        invoice.Lines[1].TaxCode = null;

        var result = await Service(client).FiscalizeInvoiceAsync(invoice);

        // Filed is filed. Never a failure and never a retry -- that would add a second receipt.
        Assert.True(result.Success);
        Assert.False(result.RequiresReconciliation);

        Assert.True(result.TaxDeclarationMismatch);
        Assert.Contains("declared at 0% but recorded at 15.5%", result.TaxDeclarationDetail);
        Assert.Contains("taxID 515", result.TaxDeclarationDetail);
    }

    [Fact]
    public async Task A_receipt_taxed_the_way_it_was_declared_raises_nothing()
    {
        // taxID 2 / code "B" / 0% is this device's zero-rated tax, read off real receipt 216204 for
        // SAP invoice 771191; 515 / "A" / 15.5% is its standard rate, read off 216192.
        var client = new RecordingRevmaxClient
        {
            FiledReceipt = new InvoiceResponse
            {
                Code = "1",
                FiscalDay = "524",
                Data = new InvoiceData
                {
                    ReceiptGlobalNo = 216204,
                    ReceiptLines =
                    [
                        new ReceiptLine { ReceiptLineNo = 0, TaxID = 515, TaxPercent = 15.5m },
                        new ReceiptLine { ReceiptLineNo = 1, TaxID = 2, TaxPercent = 0m }
                    ]
                }
            }
        };

        var invoice = Invoice();
        invoice.Lines![1].VatGroup = "O0";
        invoice.Lines[1].TaxCode = null;

        var result = await Service(client).FiscalizeInvoiceAsync(invoice);

        Assert.True(result.Success);
        Assert.False(result.TaxDeclarationMismatch);
        Assert.Null(result.TaxDeclarationDetail);
    }

    [Fact]
    public async Task A_read_back_that_cannot_be_made_is_not_reported_as_a_mismatch()
    {
        // Not being able to look says nothing about the receipt, and a filed receipt must not be
        // flagged on the strength of a failed lookup.
        var result = await Service(new UnreadableAfterFilingClient())
            .FiscalizeInvoiceAsync(Invoice());

        Assert.True(result.Success);
        Assert.False(result.TaxDeclarationMismatch);
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
            // The original invoice, and only it. The credit note's own number is not on file, or
            // there would be nothing to file.
            KnownInvoiceNumber = "123456",
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

        // Our own device filed the original, so this is an ordinary reversal.
        Assert.Equal("TransactM", client.LastEndpoint);

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
            // Filed by the very call that threw, so the pre-flight lookup finds nothing.
            KnownOnlyAfterPost = true,
            KnownInvoice = new InvoiceResponse
            {
                Code = "1",
                FiscalDay = "524",
                DeviceID = "22862",
                DeviceSerialNumber = "8DE6996C0188",
                Data = new InvoiceData
                {
                    ReceiptType = "FiscalInvoice", ReceiptGlobalNo = 216081, ReceiptCounter = 42
                }
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

    /// <summary>The device's own refusal, wording and all, for a number it already holds.</summary>
    private static TransactMResponse DuplicateRefusal(string invoiceNumber) => new()
    {
        Code = "0",
        Message =
            "Transaction error: Duplicate Invoice Number - The invoice number (" + invoiceNumber
            + ") already exists in your Device. Please check for duplicates (Invoice Numbers should "
            + "be unique per TIN).",
        DeviceID = "22862",
        DeviceSerialNumber = "8DE6996C0188",
        FiscalDay = "525"
    };

    [Fact]
    public async Task An_invoice_the_device_already_holds_is_never_sent_at_all()
    {
        // The pre-flight GET /GetInvoice/{n}, and the case it exists for: this application is not the
        // only thing filing to device 22862. The vendor's own SAP add-on files invoices to it, leaving
        // no row in our fiscal log, so the invoice list reads "Not Fiscalised" and offers a Fiscalise
        // button for a document ZIMRA already has a receipt for.
        //
        // The fixture is the device's real answer for 771485, read from
        // http://172.16.16.201:8001/api/RevmaxAPI/GetInvoice/771485 on 2026-09-10.
        var client = new RecordingRevmaxClient
        {
            KnownInvoice = new InvoiceResponse
            {
                Code = "1",
                Message = "Success",
                QRcode = "https://fdms.zimra.co.zw/000002286210092026000021640729B2D2C28D25A35C",
                VerificationCode = "29B2-D2C2-8D25-A35C",
                DeviceID = "22862",
                DeviceSerialNumber = "8DE6996C0188",
                FiscalDay = "524",
                Data = new InvoiceData
                {
                    ReceiptType = "FiscalInvoice",
                    ReceiptGlobalNo = 216407,
                    ReceiptCounter = 515,
                    InvoiceNo = "22862-771485",
                    ReceiptNotes = "22862- From Comex Van Sales"
                }
            }
        };

        var result = await Service(client).FiscalizeInvoiceAsync(Invoice());

        // Nothing reached the device. That is the whole point: the post could only be refused, and a
        // refusal is what left the page showing an error and a raw payload.
        Assert.Null(client.LastInvoice);

        Assert.True(result.Success);
        Assert.True(result.AlreadyFiscalised);
        Assert.Equal("216407", result.ReceiptGlobalNo);
        Assert.Equal("515", result.ReceiptCounter);

        // The receipt's own fiscal day, not the device's current one.
        Assert.Equal("524", result.FiscalDayNo);
        Assert.Equal("29B2-D2C2-8D25-A35C", result.VerificationCode);
        Assert.Equal("8DE6996C0188", result.DeviceSerial);
    }

    [Fact]
    public async Task Another_devices_receipt_with_the_same_number_is_not_ours_to_adopt()
    {
        // GetInvoice is NOT scoped to our device. Verified against the live box 2026-09-10: number
        // 700000 answers 3180-700000 and 50000 answers 3044-50000, both beside our own 22862-771485,
        // and all three report DeviceSerialNumber 8DE6996C0188 - so the serial cannot tell them apart.
        // Our SAP DocNums run through those ranges. Adopting on "found" alone would report our invoice
        // fiscalised on another taxpayer's receipt and it would never actually be filed.
        var client = new RecordingRevmaxClient
        {
            KnownInvoice = new InvoiceResponse
            {
                Code = "1",
                Message = "Success",
                FiscalDay = "543",
                DeviceID = "3180",
                DeviceSerialNumber = "8DE6996C0188",
                Data = new InvoiceData
                {
                    ReceiptType = "FiscalInvoice",
                    ReceiptGlobalNo = 49896,
                    InvoiceNo = "3180-123456"
                }
            }
        };

        var result = await Service(client).FiscalizeInvoiceAsync(Invoice());

        Assert.NotNull(client.LastInvoice);
        Assert.True(result.Success);
        Assert.False(result.AlreadyFiscalised);

        // Ours, from the filing - not the stranger's 49896.
        Assert.Equal("216090", result.ReceiptGlobalNo);
    }

    /// <summary>An original receipt on our device, with the lines and total a test needs.</summary>
    private static InvoiceResponse OriginalReceipt(
        decimal receiptTotal,
        params ReceiptLine[] lines) => new()
    {
        Code = "1",
        Message = "Success",
        DeviceID = "22862",
        FiscalDay = "524",
        Data = new InvoiceData
        {
            ReceiptType = "FiscalInvoice",
            ReceiptGlobalNo = 216407,
            ReceiptCounter = 515,
            ReceiptTotal = receiptTotal,
            ReceiptLines = lines.ToList()
        }
    };

    /// <summary>What the DEVICE will total: QTY x PRICE per line, not the AMT it was sent.</summary>
    private static decimal DeviceTotal(IEnumerable<RevmaxRequestItem> lines)
        => lines.Sum(line =>
            Math.Round(decimal.Parse(line.Qty!, CultureInfo.InvariantCulture)
                       * decimal.Parse(line.Price!, CultureInfo.InvariantCulture),
                2, MidpointRounding.AwayFromZero));

    [Fact]
    public async Task An_invoices_rounding_residual_is_carried_on_price_where_the_device_reads_it()
    {
        // The device recomputes each line from QTY x PRICE and discards AMT, so the residual has to
        // move PRICE. This used to move AMT on the last line, which changed nothing on the receipt and
        // left our record disagreeing with the customer's copy.
        var invoice = Invoice();
        invoice.DocTotal = 13.35m;
        invoice.Lines =
        [
            new InvoiceLineDto
            {
                LineNum = 0, ItemCode = "A", ItemDescription = "Rounds up",
                Quantity = 3m, PriceAfterVat = 1.115m, VatGroup = "O01"
            },
            new InvoiceLineDto
            {
                LineNum = 1, ItemCode = "B", ItemDescription = "Biggest line",
                Quantity = 1m, PriceAfterVat = 10.00m, VatGroup = "O01"
            }
        ];

        var client = new RecordingRevmaxClient();
        var result = await Service(client).FiscalizeInvoiceAsync(invoice);

        Assert.True(result.Success);

        var lines = (List<RevmaxRequestItem>)client.LastInvoice!.ItemsXml!;

        // What ZIMRA will hold, not what our AMTs claim.
        Assert.Equal(13.35m, DeviceTotal(lines));

        // The largest line carried it, so the per-unit change is the smallest available.
        Assert.Equal("9.99", lines[1].Price);
        Assert.Equal("1.12", lines[0].Price);
    }

    [Fact]
    public async Task Every_reconciled_line_still_declares_amount_equal_to_quantity_times_price()
    {
        // The invariant the reconciliation used to break: it moved AMT and left PRICE, so the adjusted
        // line went out claiming an amount the device would never compute.
        var invoice = Invoice();
        invoice.DocTotal = 13.35m;
        invoice.Lines =
        [
            new InvoiceLineDto
            {
                LineNum = 0, ItemCode = "A", ItemDescription = "Rounds up",
                Quantity = 3m, PriceAfterVat = 1.115m, VatGroup = "O01"
            },
            new InvoiceLineDto
            {
                LineNum = 1, ItemCode = "B", ItemDescription = "Biggest line",
                Quantity = 1m, PriceAfterVat = 10.00m, VatGroup = "O01"
            }
        ];

        var client = new RecordingRevmaxClient();
        await Service(client).FiscalizeInvoiceAsync(invoice);

        foreach (var line in (List<RevmaxRequestItem>)client.LastInvoice!.ItemsXml!)
        {
            var expected = Math.Round(
                decimal.Parse(line.Qty!, CultureInfo.InvariantCulture)
                * decimal.Parse(line.Price!, CultureInfo.InvariantCulture),
                2, MidpointRounding.AwayFromZero);

            Assert.Equal(expected, decimal.Parse(line.Amt!, CultureInfo.InvariantCulture));
        }
    }

    [Fact]
    public async Task An_invoice_too_far_out_to_be_rounding_is_still_filed()
    {
        // Deliberately unlike a credit note, whose figure is measured against the original receipt and
        // is refused outright. A gap this size is a wrong price or a dropped line, and papering over it
        // would file a wrong receipt that looks right — but not filing the sale at all is worse.
        // Invoice 769617 went to ZIMRA two cents short.
        var invoice = Invoice();
        invoice.DocTotal = 115.50m;
        invoice.Lines =
        [
            new InvoiceLineDto
            {
                LineNum = 0, ItemCode = "A", ItemDescription = "Only line",
                Quantity = 1m, PriceAfterVat = 80.00m, VatGroup = "O01"
            }
        ];

        var client = new RecordingRevmaxClient();
        var result = await Service(client).FiscalizeInvoiceAsync(invoice);

        Assert.True(result.Success);

        var lines = (List<RevmaxRequestItem>)client.LastInvoice!.ItemsXml!;

        // Left visibly unbalanced rather than quietly adjusted.
        Assert.Equal("80.00", lines[0].Price);
        Assert.Equal(80.00m, DeviceTotal(lines));
    }

    [Fact]
    public async Task A_credit_note_is_capped_at_the_original_receipts_total()
    {
        // REVMax measures a credit note against the ORIGINAL RECEIPT, not against SAP. The two are
        // rounded by different systems and SAP can land a cent above, which the device refuses
        // outright: "Credit Note Amount X exceeds the original Invoice Amount Y".
        var creditNote = CreditNote();
        creditNote.DocTotal = -115.50m;
        creditNote.Lines =
        [
            new InvoiceLineDto
            {
                LineNum = 0, ItemCode = "A", ItemDescription = "Widget",
                Quantity = 1m, PriceAfterVat = 115.50m, VatGroup = "O01"
            }
        ];

        var client = new RecordingRevmaxClient
        {
            KnownInvoiceNumber = "123456",
            KnownInvoice = OriginalReceipt(115.49m)
        };

        var result = await Service(client).FiscalizeCreditNoteAsync(creditNote, "123456");

        Assert.True(result.Success);

        // Capped to the receipt, and the line follows it — the device recomputes QTY x PRICE and
        // discards AMT, so a line left at 115.50 would still overshoot.
        Assert.Equal(115.49m, client.LastCreditNote!.InvoiceAmount);

        var line = ((List<RevmaxRequestItem>)client.LastCreditNote.ItemsXml!)[0];
        Assert.Equal("115.49", line.Amt);
        Assert.Equal("115.49", line.Price);
    }

    [Fact]
    public async Task A_partial_credit_note_is_never_raised_to_the_receipt_total()
    {
        // The cap only ever lowers. Raising it would turn a partial credit note into a full one and
        // credit the customer money they were not owed.
        var creditNote = CreditNote();
        creditNote.DocTotal = -20.00m;
        creditNote.Lines =
        [
            new InvoiceLineDto
            {
                LineNum = 0, ItemCode = "A", ItemDescription = "Widget",
                Quantity = 1m, PriceAfterVat = 20.00m, VatGroup = "O01"
            }
        ];

        var client = new RecordingRevmaxClient
        {
            KnownInvoiceNumber = "123456",
            KnownInvoice = OriginalReceipt(115.49m)
        };

        var result = await Service(client).FiscalizeCreditNoteAsync(creditNote, "123456");

        Assert.True(result.Success);
        Assert.Equal(20.00m, client.LastCreditNote!.InvoiceAmount);
    }

    [Fact]
    public async Task A_credit_note_line_declares_the_tax_the_original_receipt_declared()
    {
        // Tax ids are configured per taxpayer on the device, so the receipt being reversed is the
        // authority — not Revmax:TaxIdMappings. Our config maps O01 to 1; this device filed the line
        // under 515. Crediting under a different id credits the wrong tax, and neither receipt can be
        // amended.
        var creditNote = CreditNote();
        creditNote.DocTotal = -115.50m;
        creditNote.Lines =
        [
            new InvoiceLineDto
            {
                LineNum = 0, ItemCode = "A", ItemDescription = "Widget",
                Quantity = 1m, PriceAfterVat = 115.50m, VatGroup = "O01"
            }
        ];

        var client = new RecordingRevmaxClient
        {
            KnownInvoiceNumber = "123456",
            KnownInvoice = OriginalReceipt(
                115.50m,
                new ReceiptLine
                {
                    ReceiptLineName = "Widget", TaxID = 515, TaxCode = "A", TaxPercent = 15.5m
                })
        };

        var result = await Service(client).FiscalizeCreditNoteAsync(creditNote, "123456");

        Assert.True(result.Success);

        var line = ((List<RevmaxRequestItem>)client.LastCreditNote!.ItemsXml!)[0];
        Assert.Equal("515", line.Tax);
        Assert.Equal("15.5", line.TaxR);
    }

    [Fact]
    public async Task An_unmatched_line_borrows_the_receipts_tax_id_for_its_own_rate()
    {
        // An edited description matches no receipt line. It keeps the rate its VAT group gives it, but
        // takes the receipt's id for that rate, so one credit note never mixes id schemes.
        var creditNote = CreditNote();
        creditNote.DocTotal = -115.50m;
        creditNote.Lines =
        [
            new InvoiceLineDto
            {
                LineNum = 0, ItemCode = "A", ItemDescription = "Retyped by hand",
                Quantity = 1m, PriceAfterVat = 115.50m, VatGroup = "O01"
            }
        ];

        var client = new RecordingRevmaxClient
        {
            KnownInvoiceNumber = "123456",
            KnownInvoice = OriginalReceipt(
                115.50m,
                new ReceiptLine
                {
                    ReceiptLineName = "Something else", TaxID = 515, TaxCode = "A", TaxPercent = 15.5m
                })
        };

        var result = await Service(client).FiscalizeCreditNoteAsync(creditNote, "123456");

        Assert.True(result.Success);

        var line = ((List<RevmaxRequestItem>)client.LastCreditNote!.ItemsXml!)[0];
        Assert.Equal("515", line.Tax);
        Assert.Equal("15.5", line.TaxR);
    }

    [Fact]
    public async Task Credit_note_lines_are_made_to_sum_to_the_credited_total()
    {
        // ZIMRA's RCPT019 rejects a receipt whose declared total differs from the sum of its lines,
        // and the device builds those lines from QTY x PRICE. Three lines of 33.33 sum to 99.99
        // against a document total of 100.00.
        var creditNote = CreditNote();
        creditNote.DocTotal = -100.00m;
        creditNote.Lines = Enumerable.Range(0, 3).Select(i => new InvoiceLineDto
        {
            LineNum = i, ItemCode = "A" + i, ItemDescription = "Line " + i,
            Quantity = 1m, PriceAfterVat = 33.33m, VatGroup = "O01"
        }).ToList();

        var client = new RecordingRevmaxClient
        {
            KnownInvoiceNumber = "123456",
            KnownInvoice = OriginalReceipt(100.00m)
        };

        var result = await Service(client).FiscalizeCreditNoteAsync(creditNote, "123456");

        Assert.True(result.Success);

        var lines = (List<RevmaxRequestItem>)client.LastCreditNote!.ItemsXml!;
        var recomputed = lines.Sum(l =>
            Math.Round(decimal.Parse(l.Qty!, CultureInfo.InvariantCulture)
                       * decimal.Parse(l.Price!, CultureInfo.InvariantCulture),
                2, MidpointRounding.AwayFromZero));

        // What the DEVICE will total, not what our AMTs say.
        Assert.Equal(100.00m, recomputed);
        Assert.Equal(100.00m, client.LastCreditNote.InvoiceAmount);
    }

    [Fact]
    public async Task A_gap_too_large_to_be_rounding_is_refused_not_absorbed()
    {
        // Freight, a discount or a rounding row missing from the lines. Absorbing it would misstate a
        // line on a document that cannot be amended.
        var creditNote = CreditNote();
        creditNote.DocTotal = -100.00m;
        creditNote.Lines =
        [
            new InvoiceLineDto
            {
                LineNum = 0, ItemCode = "A", ItemDescription = "Widget",
                Quantity = 1m, PriceAfterVat = 80.00m, VatGroup = "O01"
            }
        ];

        var client = new RecordingRevmaxClient
        {
            KnownInvoiceNumber = "123456",
            KnownInvoice = OriginalReceipt(100.00m)
        };

        var result = await Service(client).FiscalizeCreditNoteAsync(creditNote, "123456");

        Assert.False(result.Success);
        Assert.Equal("CREDIT_NOTE_NOT_RECONCILED", result.ErrorCode);

        // Nothing was sent.
        Assert.Null(client.LastCreditNote);
    }

    [Fact]
    public async Task A_credit_note_against_another_devices_receipt_goes_to_TransactMExt()
    {
        // TransactMExt is for reversing a receipt filed on a DIFFERENT device: this device cannot
        // resolve that reference itself, so it is carried explicitly. Everything else belongs on
        // TransactM, which takes the same ref fields on the payload.
        var client = new RecordingRevmaxClient
        {
            KnownInvoiceNumber = "123456",
            KnownInvoice = new InvoiceResponse
            {
                Code = "1",
                DeviceID = "3180",
                FiscalDay = "543",
                Data = new InvoiceData
                {
                    ReceiptType = "FiscalInvoice", ReceiptGlobalNo = 49896, ReceiptCounter = 49
                }
            }
        };

        var result = await Service(client).FiscalizeCreditNoteAsync(CreditNote(), "123456");

        Assert.True(result.Success);
        Assert.Equal("TransactMExt", client.LastEndpoint);

        // The reference is the other device's, not ours — that is the whole point of the endpoint.
        Assert.Equal(3180, client.LastCreditNote!.refDeviceId);
        Assert.Equal(49896, client.LastCreditNote.refReceiptGlobalNo);
        Assert.Equal(543, client.LastCreditNote.refFiscalDayNo);
    }

    [Fact]
    public async Task An_invoice_receipt_is_not_adopted_for_a_credit_note()
    {
        // One number can hold either kind: REVMax keys a receipt on its number alone ("Invoice Numbers
        // should be unique per TIN"), so a credit note whose DocNum matches an invoice's would find the
        // invoice's receipt. Adopting it would report the reversal filed while ZIMRA never saw it, and
        // the customer's refund would stay declared as a sale.
        var client = new RecordingRevmaxClient
        {
            // An INVOICE receipt carrying the credit note's own number, and nothing under the
            // original's - so the only thing the pre-flight check can find is the wrong kind.
            KnownInvoiceNumber = "999001",
            KnownInvoice = new InvoiceResponse
            {
                Code = "1",
                Message = "Success",
                FiscalDay = "524",
                DeviceID = "22862",
                Data = new InvoiceData
                {
                    ReceiptType = "FiscalInvoice",
                    ReceiptGlobalNo = 216407,
                    InvoiceNo = "22862-999001"
                }
            }
        };

        var result = await Service(client).FiscalizeCreditNoteAsync(CreditNote(), "123456");

        // It refuses for the ordinary reason - no original receipt to reference - rather than
        // reporting the credit note already fiscalised.
        Assert.False(result.AlreadyFiscalised);
        Assert.Equal("ORIGINAL_RECEIPT_NOT_FOUND", result.ErrorCode);
    }

    [Fact]
    public async Task A_credit_note_the_device_already_holds_is_never_sent_again()
    {
        // The same pre-flight check as an invoice, against a CreditNote receipt on our own device.
        var client = new RecordingRevmaxClient
        {
            KnownInvoiceNumber = "999001",
            KnownInvoice = new InvoiceResponse
            {
                Code = "1",
                Message = "Success",
                FiscalDay = "828",
                DeviceID = "22862",
                VerificationCode = "AAAA-BBBB-CCCC-DDDD",
                Data = new InvoiceData
                {
                    ReceiptType = "CreditNote",
                    ReceiptGlobalNo = 47281,
                    ReceiptCounter = 426,
                    InvoiceNo = "22862-999001"
                }
            }
        };

        var result = await Service(client).FiscalizeCreditNoteAsync(CreditNote(), "123456");

        Assert.Null(client.LastCreditNote);
        Assert.True(result.Success);
        Assert.True(result.AlreadyFiscalised);
        Assert.Equal("47281", result.ReceiptGlobalNo);
        Assert.Equal("828", result.FiscalDayNo);
    }

    [Fact]
    public async Task A_device_that_cannot_be_asked_first_still_files_the_invoice()
    {
        // The pre-flight lookup is the cheap path, not the guard. Treating a failed lookup as "already
        // fiscalised" would silently stop filing real invoices the moment the device went quiet.
        var client = new RecordingRevmaxClient { ThrowOnGet = new HttpRequestException("unreachable") };

        var result = await Service(client).FiscalizeInvoiceAsync(Invoice());

        Assert.NotNull(client.LastInvoice);
        Assert.True(result.Success);
        Assert.False(result.AlreadyFiscalised);
    }

    [Fact]
    public async Task A_duplicate_refusal_adopts_the_receipt_the_device_already_holds()
    {
        // The device refusing as a duplicate is it saying the document is fiscalised — ZIMRA has the
        // receipt. Reported as a failure it left the invoice reading "Not Fiscalised" with a
        // Fiscalise button on it, so the only visible state was one that invites another press.
        var client = new RecordingRevmaxClient
        {
            RefusePostWith = DuplicateRefusal("123456"),
            // Reaches the refusal rather than the pre-flight lookup: this is the backstop for a
            // receipt filed between the two, and for a device that could not be asked first.
            KnownOnlyAfterPost = true,
            KnownInvoice = new InvoiceResponse
            {
                Code = "1",
                Message = "Success",
                FiscalDay = "525",
                DeviceID = "22862",
                DeviceSerialNumber = "8DE6996C0188",
                QRcode = "https://fdms.zimra.co.zw/QR",
                VerificationCode = "1234-5678",
                Data = new InvoiceData
                {
                    ReceiptType = "FiscalInvoice", ReceiptGlobalNo = 216090, ReceiptCounter = 41
                }
            }
        };

        var result = await Service(client).FiscalizeInvoiceAsync(Invoice());

        Assert.True(result.Success);
        Assert.True(result.AlreadyFiscalised);
        Assert.False(result.RequiresReconciliation);

        // The fiscal details are the ones on file, not blanks — they are what marks the document
        // fiscalised and what the reprint puts on the customer's copy.
        Assert.Equal("216090", result.ReceiptGlobalNo);
        Assert.Equal("41", result.ReceiptCounter);
        Assert.Equal("525", result.FiscalDayNo);
        Assert.Equal("8DE6996C0188", result.DeviceSerial);
        Assert.Equal("https://fdms.zimra.co.zw/QR", result.QRCode);
        Assert.Equal("1234-5678", result.VerificationCode);
    }

    [Fact]
    public async Task A_duplicate_whose_receipt_cannot_be_read_is_never_reported_as_a_plain_failure()
    {
        // The device says a receipt exists and the read did not confirm it. Resubmitting is not the
        // remedy for either half of that, so it must not come back as an ordinary retryable failure.
        var client = new RecordingRevmaxClient
        {
            RefusePostWith = DuplicateRefusal("123456"),
            ThrowOnGet = new HttpRequestException("unreachable")
        };

        var result = await Service(client).FiscalizeInvoiceAsync(Invoice());

        Assert.False(result.Success);
        Assert.False(result.AlreadyFiscalised);
        Assert.True(result.RequiresReconciliation);
    }

    [Fact]
    public async Task An_ordinary_refusal_is_still_a_refusal()
    {
        // The adoption path is entered on the device's duplicate wording alone. Anything else must
        // stay a failure, or a refused invoice gets marked fiscalised on the strength of a receipt
        // lookup that answered for some other reason.
        var client = new RecordingRevmaxClient
        {
            RefusePostWith = new TransactMResponse
            {
                Code = "0",
                Message = "Transaction error: Fiscal day is closed. Open a fiscal day and retry.",
                FiscalDay = "525"
            },
            KnownOnlyAfterPost = true,
            KnownInvoice = new InvoiceResponse
            {
                Code = "1",
                Data = new InvoiceData { ReceiptGlobalNo = 999999 }
            }
        };

        var result = await Service(client).FiscalizeInvoiceAsync(Invoice());

        Assert.False(result.Success);
        Assert.False(result.AlreadyFiscalised);
        Assert.Null(result.ReceiptGlobalNo);
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

    /// <summary>Files successfully, then refuses every read.</summary>
    private sealed class UnreadableAfterFilingClient : IRevmaxClient
    {
        private bool _filed;

        public Task<TransactMResponse?> TransactMAsync(
            TransactMRequest request, CancellationToken cancellationToken = default)
        {
            _filed = true;
            return Task.FromResult<TransactMResponse?>(new TransactMResponse
            {
                Code = "1", Message = "Success", FiscalDay = "524"
            });
        }

        public Task<InvoiceResponse?> GetInvoiceAsync(
            string invoiceNumber, CancellationToken cancellationToken = default)
            => _filed
                ? throw new HttpRequestException("unreachable")
                : Task.FromResult<InvoiceResponse?>(new InvoiceResponse { Code = "0" });

        public Task<TransactMExtResponse?> TransactMExtAsync(
            TransactMExtRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

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

    /// <summary>Records what the service tried to send, and never sends anything.</summary>
    private sealed class RecordingRevmaxClient : IRevmaxClient
    {
        public TransactMRequest? LastInvoice { get; private set; }

        public TransactMExtRequest? LastCreditNote { get; private set; }

        /// <summary>Which endpoint the last post went to.</summary>
        public string? LastEndpoint { get; private set; }

        public InvoiceResponse? KnownInvoice { get; init; }

        public Exception? ThrowOnPost { get; init; }

        public Exception? ThrowOnGet { get; init; }

        /// <summary>Refuse the post with this body instead of filing. Code "0" over HTTP 200, as the
        /// device does.</summary>
        public TransactMResponse? RefusePostWith { get; init; }

        /// <summary>Answer <see cref="KnownInvoice"/> for this number only. Null answers it for any
        /// number, which is what most of these tests want.</summary>
        public string? KnownInvoiceNumber { get; init; }

        /// <summary>The receipt does not exist until a post has been attempted — the device filed it
        /// during the call, or refused the call because of something filed by another feed.</summary>
        public bool KnownOnlyAfterPost { get; init; }

        private bool _postAttempted;

        public Task<TransactMResponse?> TransactMAsync(
            TransactMRequest request, CancellationToken cancellationToken = default)
        {
            _postAttempted = true;

            if (ThrowOnPost is not null)
            {
                throw ThrowOnPost;
            }

            LastInvoice = request;
            LastEndpoint = "TransactM";

            // A credit note posted to TransactM is still a TransactMExtRequest — the ref fields ride
            // on the payload either way, and only the endpoint distinguishes the two cases.
            if (request is TransactMExtRequest ext)
            {
                LastCreditNote = ext;
            }

            if (RefusePostWith is not null)
            {
                return Task.FromResult<TransactMResponse?>(RefusePostWith);
            }

            _hasFiled = true;
            return Task.FromResult<TransactMResponse?>(new TransactMResponse
            {
                Code = "1", Message = "Success", FiscalDay = "524", ReceiptGlobalNo = "216090"
            });
        }

        public Task<TransactMExtResponse?> TransactMExtAsync(
            TransactMExtRequest request, CancellationToken cancellationToken = default)
        {
            _postAttempted = true;

            if (ThrowOnPost is not null)
            {
                throw ThrowOnPost;
            }

            LastCreditNote = request;
            LastInvoice = request;
            LastEndpoint = "TransactMExt";
            _hasFiled = true;
            return Task.FromResult<TransactMExtResponse?>(new TransactMExtResponse
            {
                Code = "1", Message = "Success", FiscalDay = "524", ReceiptGlobalNo = "216091"
            });
        }

        /// <summary>What the device answers once something has been filed under this number.</summary>
        public InvoiceResponse? FiledReceipt { get; init; }

        private bool _hasFiled;

        public Task<InvoiceResponse?> GetInvoiceAsync(
            string invoiceNumber, CancellationToken cancellationToken = default)
        {
            if (ThrowOnGet is not null)
            {
                throw ThrowOnGet;
            }

            if (_hasFiled && FiledReceipt is not null)
            {
                return Task.FromResult<InvoiceResponse?>(FiledReceipt);
            }

            if (KnownInvoice is not null
                && (KnownInvoiceNumber is null || KnownInvoiceNumber == invoiceNumber)
                && (!KnownOnlyAfterPost || _postAttempted))
            {
                return Task.FromResult<InvoiceResponse?>(KnownInvoice);
            }

            // The device's own answer for a number it does not hold.
            return Task.FromResult<InvoiceResponse?>(new InvoiceResponse
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
