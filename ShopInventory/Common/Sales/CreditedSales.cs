using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Features.DesktopCreditNotes;
using ShopInventory.Models.Entities;

namespace ShopInventory.Common.Sales;

/// <summary>
/// A sale something has been credited back on, with everything a report groups a sale by.
/// </summary>
/// <param name="TotalAmount">What the sale was rung up for, tax included.</param>
/// <param name="CreditedAmount">
/// What its credits gave back, tax included — the same basis as <paramref name="TotalAmount"/>, so the
/// two net without anyone working out VAT. Not capped at the total: it is what the credits say.
/// </param>
/// <param name="CreditNoteNumbers">The numbers a person would quote: SAP memo DocNums and <c>CN1753</c>-style till credits.</param>
/// <param name="Lines">What was given back of each item, where a credit could be tied to a line of the sale.</param>
public sealed record CreditedSale(
    int SaleId,
    string? Currency,
    DateTime DocDate,
    DateTime CreatedAt,
    string? WarehouseCode,
    string? CardCode,
    string? SourceSystem,
    string? CreatedBy,
    string? PaymentMethod,
    string? CostCentreCode,
    int? RouteCustomerId,
    decimal TotalAmount,
    decimal CreditedAmount,
    IReadOnlyList<string> CreditNoteNumbers,
    IReadOnlyList<CreditedSaleLine> Lines)
{
    /// <summary>Whether the credits give back the whole sale, to the cent.</summary>
    public bool IsFullyCredited => new SaleCredit(CreditedAmount, CreditNoteNumbers).Covers(TotalAmount);
}

/// <summary>What a sale's credits gave back of one of its lines.</summary>
/// <param name="Quantity">Units given back, never more than the line sold.</param>
/// <param name="Value">
/// Their share of the line's own <c>LineTotal</c>, and so on whatever basis the sale's lines are held —
/// before tax for a till sale. It nets against an item's line value, not against a sale's total.
/// </param>
public sealed record CreditedSaleLine(string ItemCode, decimal Quantity, decimal Value);

/// <summary>
/// The sales in a report's scope that have been credited back, so the report can say what was sold, what
/// was credited and what is left.
/// </summary>
/// <remarks>
/// <para>
/// A credit is counted against the sale it reverses, and so on that sale's day — not on the day the
/// credit was raised. The till and the van handset net their takings the same way, from
/// <see cref="SaleCredits.ForSalesAsync"/>, which is what decides here too which credits count: a till
/// credit once ZIMRA has accepted it, a SAP memo that has not been cancelled, and each of them once.
/// </para>
/// <para>
/// Credits are rare, so only the sales that have one are read row by row: the reports stay the grouped
/// queries they are, and take these few sales off afterwards.
/// </para>
/// </remarks>
public static class CreditedSales
{
    /// <summary>The credited sales among <paramref name="sales"/>; a sale nothing was credited on is absent.</summary>
    public static async Task<List<CreditedSale>> InAsync(
        ApplicationDbContext db, IQueryable<DesktopSaleEntity> sales, CancellationToken ct)
    {
        // Every sale that could carry a credit: its own till credits, or a memo against its own invoice.
        // SaleCredits then decides what each is worth, so the rule is stated once.
        var candidates = await sales
            .Where(s => db.DesktopCreditNotes.Any(credit =>
                            credit.SaleId == s.Id && credit.Status == DesktopCreditStatuses.Fiscalised)
                        || (s.SapDocEntry != null
                            && db.SapCreditNoteLineSnapshots.Any(line =>
                                line.BaseType == VanSaleCreditNotes.InvoiceBaseType
                                && line.BaseEntry == s.SapDocEntry
                                && !line.CreditNote.IsCancelled)))
            .Select(s => new
            {
                s.Id,
                s.SapDocEntry,
                s.Currency,
                s.DocDate,
                s.CreatedAt,
                s.WarehouseCode,
                s.CardCode,
                s.SourceSystem,
                s.CreatedBy,
                s.PaymentMethod,
                s.CostCentreCode,
                s.RouteCustomerId,
                s.TotalAmount,
                Lines = s.Lines.Select(line => new { line.Id, line.ItemCode, line.Quantity, line.LineTotal }).ToList()
            })
            .ToListAsync(ct);

        if (candidates.Count == 0)
            return [];

        var credits = await SaleCredits.ForSalesAsync(
            db, candidates.Select(sale => (sale.Id, sale.SapDocEntry)), ct);

        var credited = new List<CreditedSale>();

        foreach (var sale in candidates)
        {
            if (!credits.TryGetValue(sale.Id, out var credit) || credit.Amount <= 0m)
                continue;

            var byLine = SaleCredits.BySaleLine(credit);

            credited.Add(new CreditedSale(
                sale.Id,
                sale.Currency,
                sale.DocDate,
                sale.CreatedAt,
                sale.WarehouseCode,
                sale.CardCode,
                sale.SourceSystem,
                sale.CreatedBy,
                sale.PaymentMethod,
                sale.CostCentreCode,
                sale.RouteCustomerId,
                sale.TotalAmount,
                credit.Amount,
                credit.Numbers,
                sale.Lines
                    .Where(line => byLine.ContainsKey(line.Id) && line.Quantity > 0m)
                    .Select(line =>
                    {
                        // A credit cannot give back more of a line than it sold; a memo's quantity is
                        // worked out from its money and can land a fraction over.
                        var quantity = Math.Min(Math.Max(byLine[line.Id].Quantity, 0m), line.Quantity);
                        return new CreditedSaleLine(
                            line.ItemCode,
                            quantity,
                            Math.Round(line.LineTotal * quantity / line.Quantity, 2, MidpointRounding.AwayFromZero));
                    })
                    .Where(line => line.Quantity > 0m)
                    .ToList()));
        }

        return credited;
    }
}
