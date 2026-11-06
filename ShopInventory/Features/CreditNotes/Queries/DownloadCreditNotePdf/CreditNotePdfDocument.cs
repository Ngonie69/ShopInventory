using ShopInventory.DTOs;
using ShopInventory.Models;

namespace ShopInventory.Features.CreditNotes.Queries.DownloadCreditNotePdf;

/// <summary>A SAP credit memo in the shape the invoice sheet prints from.</summary>
/// <remarks>
/// Every amount is in the memo's own currency (<see cref="CreditNoteFigures"/>): the sheet prints the
/// currency code beside the total, so a ZiG memo printed from SAP's USD fields would state its USD value
/// as ZiG.
/// </remarks>
internal static class CreditNotePdfDocument
{
    public static InvoiceDto From(SAPCreditNote creditNote, CreditNoteReceipt? receipt) => new()
    {
        DocEntry = creditNote.DocEntry,
        DocNum = creditNote.DocNum,
        DocDate = creditNote.DocDate,
        CardCode = creditNote.CardCode,
        CardName = creditNote.CardName,
        DocCurrency = CreditNoteFigures.Currency(creditNote),
        DocTotal = CreditNoteFigures.Total(creditNote),
        VatSum = CreditNoteFigures.Vat(creditNote),
        BillToAddress = creditNote.Address,
        IsFiscalized = receipt is not null,
        FiscalQrCode = receipt?.QrCode,
        FiscalReceiptGlobalNo = receipt?.ReceiptGlobalNo,
        FiscalVerificationCode = receipt?.VerificationCode,
        FiscalDay = receipt?.FiscalDay,
        FiscalDeviceId = receipt?.DeviceId,
        Lines = (creditNote.DocumentLines ?? [])
            .OrderBy(line => line.LineNum)
            .Select(line => new InvoiceLineDto
            {
                LineNum = line.LineNum,
                ItemCode = line.ItemCode,
                ItemDescription = line.ItemDescription,
                Quantity = line.Quantity,
                UnitPrice = line.UnitPrice,
                PriceAfterVat = line.PriceAfterVAT,
                LineTotal = CreditNoteFigures.LineNet(creditNote, line),
                GrossTotal = CreditNoteFigures.LineGross(creditNote, line),
                DiscountPercent = line.DiscountPercent ?? 0m,
                VatGroup = line.VatGroup,
                UoMCode = line.UoMCode
            })
            .ToList()
    };
}
