using ShopInventory.Common.Fiscalization;
using ShopInventory.Features.VanSalesCompatibility;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// The fiscal details a van sales handset is sent with an invoice, for printing under its QR.
/// </summary>
/// <remarks>
/// They come off the invoice's own fiscal record, the one its verification code and QR already come
/// from. The handset used to ask <c>GET /vansales/fiscal</c> for them, which answers with the most
/// recent fiscal transaction on the server, whichever invoice that was.
///
/// <para>For a per-sale invoice that record is the sale itself. Its receipt was signed under the
/// sale's own reference before SAP assigned a number, so the fiscal transaction log — keyed on the
/// DocNum — has nothing, and the invoice was drawn "Not Fiscalised" on the very phone that signed it,
/// dated 02:00:00 on its SAP document date, and titled by its SAP DocEntry. The office lists the same
/// sale as INV2327, signed at 14:51, with a receipt.</para>
/// </remarks>
public sealed class VanSalesLegacyInvoiceFiscalTests
{
    private static Invoice Invoice() => new()
    {
        DocEntry = 9001,
        DocNum = 4021,
        CardCode = "C-VAN-014",
        DocDate = "2026-09-18",
        DocDueDate = "2026-09-18",
        DocCurrency = "USD",
        DocTotal = 118m,
        VatSum = 18m,
        DocumentLines = []
    };

    [Fact]
    public void An_invoice_carries_the_day_device_and_receipt_it_was_signed_under()
    {
        var mapped = VanSalesCompatibilityMapper.MapLegacyInvoice(
            Invoice(),
            new DesktopFiscalTransactionEntity
            {
                DocNum = 4021,
                DocumentType = "Invoice",
                Status = "Success",
                VerificationCode = "ABCD1234EFGH5678",
                QRCode = "https://fdms.zimra.co.zw/verify?x",
                FiscalDay = "37",
                DeviceSerialNumber = "SN-REVMAX-01",
                ReceiptGlobalNo = 4211
            });

        Assert.Equal("37", mapped.FiscalDay);
        Assert.Equal("SN-REVMAX-01", mapped.DeviceSerial);
        Assert.Equal("4211", mapped.ReceiptGlobalNo);
        Assert.Equal("ABCD1234EFGH5678", mapped.Verification);
    }

    /// <summary>
    /// Nothing fiscalised means nothing to print — empty rather than a zero, which the handset would
    /// print as "Fiscal day 0".
    /// </summary>
    [Fact]
    public void An_unfiscalised_invoice_carries_none()
    {
        var mapped = VanSalesCompatibilityMapper.MapLegacyInvoice(Invoice(), fiscalTransaction: null);

        Assert.Equal(string.Empty, mapped.FiscalDay);
        Assert.Equal(string.Empty, mapped.DeviceSerial);
        Assert.Equal(string.Empty, mapped.ReceiptGlobalNo);
        Assert.Equal(string.Empty, mapped.SaleNumber);
    }

    // ── An invoice that records a sale signed before SAP ────────────────────

    [Fact]
    public void A_per_sale_invoice_is_fiscalised_under_its_sales_receipt_and_number()
    {
        // No fiscal transaction — there never is one for these — and the sale row instead.
        var mapped = VanSalesCompatibilityMapper.MapLegacyInvoice(Invoice(), fiscalTransaction: null, Sale());

        Assert.Equal(1, mapped.Fiscalized);
        Assert.Equal("Fiscalised", mapped.FiscalizedText);
        Assert.Equal(2, mapped.Status);

        Assert.Equal("INV2327", mapped.SaleNumber);
        Assert.Equal("07E3-19D4-D197-3BB5", mapped.Verification);
        Assert.Equal("https://fdms.zimra.co.zw/verify?y", mapped.QrCode);
        Assert.Equal("538", mapped.FiscalDay);
        Assert.Equal("8DE6996C0188", mapped.DeviceSerial);
        Assert.Equal("221595", mapped.ReceiptGlobalNo);
    }

    /// <summary>
    /// The sale number rides beside the id, not in it.
    /// </summary>
    /// <remarks>
    /// The handset sends <c>id</c> back as the document to file a proof of delivery against, and the
    /// server reads that number as a sales order id first. A sale id there would name whichever sales
    /// order happens to share the number.
    /// </remarks>
    [Fact]
    public void The_id_stays_the_SAP_document_entry()
    {
        var mapped = VanSalesCompatibilityMapper.MapLegacyInvoice(Invoice(), fiscalTransaction: null, Sale());

        Assert.Equal(9001, mapped.Id);
        Assert.Equal("9001", mapped.DocEntry);
        Assert.Equal("4021", mapped.DocNum);
    }

    /// <summary>
    /// "Sale date" on the handset is <c>due_date</c>, and for a sale it is the moment the receipt was
    /// signed — not the calendar day SAP filed the invoice under, which for a sale posted from the
    /// queue can be the next day.
    /// </summary>
    [Fact]
    public void A_per_sale_invoice_is_dated_by_its_signing_not_by_the_SAP_document_date()
    {
        var invoice = Invoice();
        invoice.DocDate = "2026-09-22T00:00:00Z";
        invoice.DocDueDate = "2026-09-22T00:00:00Z";

        var mapped = VanSalesCompatibilityMapper.MapLegacyInvoice(invoice, fiscalTransaction: null, Sale());

        Assert.Equal("2026-09-21 14:51:07", mapped.DueDate);
        Assert.Equal("2026-09-21 14:51:07", mapped.Timestamps.CreateDate);
        Assert.Equal("2026-09-21 14:51:07", mapped.Timestamps.ApprovalDate);

        // The document's own date is still the document's.
        Assert.Equal("2026-09-22 00:00:00", mapped.DocDate);
    }

    /// <summary>
    /// A sale with no signed wall clock — an online sale from a handset that does not stamp — is
    /// dated by the row's UTC creation, moved to CAT like every other UTC instant on the wire.
    /// </summary>
    [Fact]
    public void An_online_sale_with_no_signed_clock_is_dated_by_its_creation_in_CAT()
    {
        var sale = Sale() with { SoldAt = new DateTime(2026, 9, 21, 12, 51, 7, DateTimeKind.Utc) };

        var mapped = VanSalesCompatibilityMapper.MapLegacyInvoice(Invoice(), fiscalTransaction: null, sale);

        Assert.Equal("2026-09-21 14:51:07", mapped.DueDate);
    }

    /// <summary>
    /// A van sale that reached SAP through the invoice queue is matched by its reservation, and when
    /// its receipt row cannot be found the queue entry still vouches for the receipt — just without a
    /// sale number to show, or a QR to print.
    /// </summary>
    [Fact]
    public void A_queue_marker_without_its_receipt_row_is_fiscalised_with_no_sale_number()
    {
        var sale = new PerSaleInvoiceSaleFacts(
            SaleId: null,
            ExternalReferenceId: "VAN014-INV-20260918-1B2C3D",
            SoldAt: null,
            FiscalReceiptNumber: "R-9001",
            ReceiptGlobalNo: null,
            VerificationCode: null,
            QrCode: null,
            FiscalDay: null,
            DeviceSerial: null);

        var mapped = VanSalesCompatibilityMapper.MapLegacyInvoice(Invoice(), fiscalTransaction: null, sale);

        Assert.Equal(1, mapped.Fiscalized);
        Assert.Equal(string.Empty, mapped.SaleNumber);
        Assert.Equal("R-9001", mapped.ReceiptGlobalNo);
        Assert.Equal(string.Empty, mapped.QrCode);

        // Nothing says when it was sold, so the SAP document date stands.
        Assert.Equal("2026-09-18 00:00:00", mapped.DueDate);
    }

    // ── SAP's dates are days ────────────────────────────────────────────────

    /// <summary>
    /// SAP writes a document date as midnight UTC. Read as an instant and moved to CAT, every invoice
    /// said 02:00:00, which is what the handset showed under "Sale date".
    /// </summary>
    [Fact]
    public void A_SAP_document_date_is_a_day_not_an_instant()
    {
        var invoice = Invoice();
        invoice.DocDate = "2026-09-21T00:00:00Z";
        invoice.DocDueDate = "2026-09-21T00:00:00Z";

        var mapped = VanSalesCompatibilityMapper.MapLegacyInvoice(invoice, fiscalTransaction: null);

        Assert.Equal("2026-09-21 00:00:00", mapped.DocDate);
        Assert.Equal("2026-09-21 00:00:00", mapped.DueDate);
        Assert.Equal("2026-09-21 00:00:00", mapped.Timestamps.CreateDate);
    }

    /// <summary>The sale in the office's Van Sales drawer: INV2327, signed 21 Sep 2026 at 14:51.</summary>
    private static PerSaleInvoiceSaleFacts Sale() => new(
        SaleId: 2327,
        ExternalReferenceId: "VAN005-INV-20260921-FC1EDE",
        SoldAt: new DateTime(2026, 9, 21, 14, 51, 7, DateTimeKind.Unspecified),
        FiscalReceiptNumber: "221595",
        ReceiptGlobalNo: 221595,
        VerificationCode: "07E3-19D4-D197-3BB5",
        QrCode: "https://fdms.zimra.co.zw/verify?y",
        FiscalDay: "538",
        DeviceSerial: "8DE6996C0188");
}
