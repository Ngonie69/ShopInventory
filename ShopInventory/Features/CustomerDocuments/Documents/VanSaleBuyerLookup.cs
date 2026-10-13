using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.CustomerDocuments.Documents;

/// <summary>
/// The customer name a van sale carries, for an invoice with no route customer record to print.
/// </summary>
/// <remarks>
/// <para>
/// A van's customers live only here; SAP has the van's own selling account, so its card name on the
/// invoice is the van's — "Van Sales West 1" — and never the customer's. A sale tied to a route
/// customer prints that shop (<see cref="Delivery.RouteCustomerInvoiceResolver"/>). A sale that is
/// not — sent under the van's account alone, or its shop since removed — still names its customer,
/// as every van sale does: the shop's name as it stood at the sale, else what the handset called the
/// customer in <c>CardName</c>. The Van Sales Invoices page and SAP's <c>NumAtCard</c> read the same
/// two fields in the same order, and so does the document sent to the customer.
/// </para>
/// <para>
/// Only the name is taken: with no route customer there is no address or VAT number that is the
/// buyer's. And only for a selling account — an account customer's invoice prints its own card, which
/// is the customer. The sale is found and believed by the resolver's rules: the reference SAP keeps on
/// the invoice, the same card, and no other SAP document recorded against it.
/// </para>
/// </remarks>
internal static class VanSaleBuyerLookup
{
    public static async Task<string?> RecordedNameAsync(
        ApplicationDbContext dbContext,
        int? desktopSaleId,
        string? saleReference,
        string? cardCode,
        int? sapDocEntry,
        CancellationToken cancellationToken)
    {
        var reference = string.IsNullOrWhiteSpace(saleReference) ? null : saleReference.Trim();
        var card = string.IsNullOrWhiteSpace(cardCode) ? null : cardCode.Trim();
        if (card is null || (desktopSaleId is null && reference is null))
            return null;

        var sales = dbContext.DesktopSales
            .AsNoTracking()
            .Where(sale => SaleSourceSystems.VanSaleSources.Contains(sale.SourceSystem));
        sales = desktopSaleId is { } saleId
            ? sales.Where(sale => sale.Id == saleId)
            : sales.Where(sale => sale.ExternalReferenceId == reference);

        var recorded = await sales
            .OrderByDescending(sale => sale.Id)
            .Select(sale => new Recorded(sale.CardCode, sale.RouteCustomerName ?? sale.CardName, sale.SapDocEntry))
            .FirstOrDefaultAsync(cancellationToken);

        // An online sale from before receipts were stored has no sale row, only its reservation.
        if (recorded is null && reference is not null)
        {
            recorded = await dbContext.StockReservations
                .AsNoTracking()
                .Where(reservation => reservation.ExternalReferenceId == reference
                    && reservation.SourceSystem == SaleSourceSystems.VanSales
                    && reservation.Status == ReservationStatus.Confirmed)
                .OrderByDescending(reservation => reservation.Id)
                .Select(reservation => new Recorded(
                    reservation.CardCode, reservation.RouteCustomerName ?? reservation.CardName, reservation.SAPDocEntry))
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (recorded is null
            || !string.Equals(recorded.CardCode?.Trim(), card, StringComparison.OrdinalIgnoreCase)
            || (recorded.SapDocEntry is { } posted && sapDocEntry is { } asked && posted != asked))
        {
            return null;
        }

        var name = string.IsNullOrWhiteSpace(recorded.CustomerName)
            ? null
            : string.Join(' ', recorded.CustomerName.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        // A handset with nothing better to say sends the account's own code.
        if (name is null || string.Equals(name, card, StringComparison.OrdinalIgnoreCase))
            return null;

        return await SellingAccountCards.IsSellingAccountAsync(dbContext, card, cancellationToken) ? name : null;
    }

    /// <param name="CustomerName">The shop's name as it stood at the sale, else what the handset called the customer.</param>
    private sealed record Recorded(string? CardCode, string? CustomerName, int? SapDocEntry);
}
