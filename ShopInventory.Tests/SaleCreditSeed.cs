using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Features.DesktopCreditNotes;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Credits for the report tests to seed: a till credit as the credit dialog files it, and a SAP memo as
/// the memo projection holds it. One place, so every report is tested against the same two shapes.
/// </summary>
internal static class SaleCreditSeed
{
    private static readonly DateTime RaisedUtc = new(2026, 9, 25, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// A till credit against a sale. <paramref name="lines"/> name the receipt lines it gives back,
    /// 1-based in the order they were taken, at a tax-inclusive price.
    /// </summary>
    public static async Task TillCreditAsync(
        ApplicationDbContext context,
        int saleId,
        decimal amount,
        string status = DesktopCreditStatuses.Fiscalised,
        int? sapDocEntry = null,
        params (int ReceiptLine, decimal Quantity, decimal UnitPrice)[] lines)
    {
        var plan = lines.Length == 0
            ? ""
            : System.Text.Json.JsonSerializer.Serialize(
                new DesktopCreditPlan(
                    new DesktopCreditSource(
                        "SALE", "USD", amount, 0, 0, 0, null,
                        lines.Select(line => new DesktopCreditLine(
                            line.ReceiptLine, $"Line {line.ReceiptLine}", line.Quantity, line.UnitPrice, 1, 15.5m, null, null)).ToList()),
                    lines.Select(line => new DesktopCreditQuantity(line.ReceiptLine, line.Quantity)).ToList(),
                    null!,
                    lines.Sum(line => line.Quantity * line.UnitPrice)),
                DesktopCreditNoteService.Json);

        context.DesktopCreditNotes.Add(new DesktopCreditNoteEntity
        {
            Id = Guid.NewGuid(),
            SaleId = saleId,
            RequestKey = Guid.NewGuid().ToString("N"),
            Number = $"DCN-{Guid.NewGuid():N}",
            OriginalFiscalNumber = $"SALE-{saleId}",
            Reason = "Damaged",
            Currency = "USD",
            Amount = amount,
            Status = status,
            SapStatus = sapDocEntry is null ? DesktopCreditSapStatuses.Deferred : DesktopCreditSapStatuses.Posted,
            SapDocEntry = sapDocEntry,
            PlanJson = plan,
            CreatedAtUtc = RaisedUtc.AddMinutes(await context.DesktopCreditNotes.CountAsync()),
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }

    /// <summary>
    /// A SAP credit memo against invoices. Each line names the invoice it is based on, the invoice line
    /// (its position on the invoice), and its money before tax and the tax on it.
    /// </summary>
    public static async Task MemoAsync(
        ApplicationDbContext context,
        int docEntry,
        int docNum,
        bool isCancelled = false,
        params (int BaseEntry, int BaseLine, string ItemCode, decimal LineTotal, decimal Vat)[] lines)
    {
        context.SapCreditNoteSnapshots.Add(new SapCreditNoteSnapshotEntity
        {
            SapDocEntry = docEntry,
            SapDocNum = docNum,
            DocDate = RaisedUtc.Date,
            CardCode = "FARM-BP",
            DocCurrency = "USD",
            DocTotal = lines.Sum(line => line.LineTotal + line.Vat),
            VatSum = lines.Sum(line => line.Vat),
            IsCancelled = isCancelled,
            LastSeenInSapAtUtc = RaisedUtc,
            SyncedAtUtc = RaisedUtc,
            Lines = lines
                .Select((line, index) => new SapCreditNoteLineSnapshotEntity
                {
                    LineNum = index,
                    ItemCode = line.ItemCode,
                    BaseType = VanSaleCreditNotes.InvoiceBaseType,
                    BaseEntry = line.BaseEntry,
                    BaseLine = line.BaseLine,
                    LineTotal = line.LineTotal,
                    VatSum = line.Vat,
                })
                .ToList()
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }
}
