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
    }
}
