using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.CustomerDocuments.Delivery;

/// <summary>The shop a van invoice was really for, and the sale row that says so.</summary>
/// <param name="RouteCustomerId">The route customer — the shop.</param>
/// <param name="RouteCustomerCode">Its code, as the sale recorded it.</param>
/// <param name="RouteCustomerName">Its name, as the sale recorded it.</param>
/// <param name="DesktopSaleId">The van sale row, when the link came from one; null for a reservation.</param>
public sealed record RouteCustomerInvoiceLink(
    int RouteCustomerId,
    string? RouteCustomerCode,
    string? RouteCustomerName,
    int? DesktopSaleId);

/// <summary>
/// Finds the shop behind SAP invoices a van posted to its own card.
/// </summary>
/// <remarks>
/// <para>
/// A van invoices every shop to its own selling account, so SAP cannot say who bought. The sale
/// reference can: SAP keeps it in <c>U_Van_saleorder</c>, and the van sale row
/// (<c>DesktopSales.ExternalReferenceId</c>, the online and offline van sources) or, for an online sale
/// with no sale row, the confirmed stock reservation, holds the same reference beside the route
/// customer it was for. Vending and till sales are not van sources and never match.
/// </para>
/// <para>
/// A match is believed only when it bills the same card, and when the row either has no SAP number
/// yet or records this invoice's DocEntry. SAP reissued DocEntries after the September 2026 update and
/// some rows still hold the old ones, so a row naming another document is a stale link, not this
/// invoice's shop — sending it would put one shop's invoice in another's chat.
/// </para>
/// </remarks>
public static class RouteCustomerInvoiceResolver
{
    public static async Task<Dictionary<int, RouteCustomerInvoiceLink>> ResolveAsync(
        ApplicationDbContext context,
        IReadOnlyCollection<Invoice> invoices,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var links = new Dictionary<int, RouteCustomerInvoiceLink>();
        var byReference = invoices
            .Where(invoice => !string.IsNullOrWhiteSpace(invoice.U_Van_saleorder))
            .GroupBy(invoice => invoice.U_Van_saleorder!.Trim(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        if (byReference.Count == 0)
            return links;

        var references = byReference.Keys.ToList();

        var sales = await context.DesktopSales
            .AsNoTracking()
            .Where(sale => references.Contains(sale.ExternalReferenceId)
                && SaleSourceSystems.VanSaleSources.Contains(sale.SourceSystem)
                && sale.RouteCustomerId != null)
            .Select(sale => new Candidate(
                sale.ExternalReferenceId,
                sale.CardCode,
                sale.RouteCustomerId!.Value,
                sale.RouteCustomerCode,
                sale.RouteCustomerName,
                sale.SapDocEntry,
                sale.Id))
            .ToListAsync(cancellationToken);

        var reservations = await context.StockReservations
            .AsNoTracking()
            .Where(reservation => references.Contains(reservation.ExternalReferenceId)
                && reservation.Status == ReservationStatus.Confirmed
                && reservation.RouteCustomerId != null)
            .Select(reservation => new Candidate(
                reservation.ExternalReferenceId,
                reservation.CardCode,
                reservation.RouteCustomerId!.Value,
                reservation.RouteCustomerCode,
                reservation.RouteCustomerName,
                reservation.SAPDocEntry,
                null))
            .ToListAsync(cancellationToken);

        // The sale row first: it is what the receipt was signed against. A stale row rules the invoice
        // out rather than letting a reservation answer instead.
        var stale = new HashSet<int>();
        foreach (var candidate in sales.Concat(reservations))
        {
            if (!byReference.TryGetValue(candidate.Reference.Trim(), out var matched))
                continue;

            foreach (var invoice in matched)
            {
                if (links.ContainsKey(invoice.DocEntry) || stale.Contains(invoice.DocEntry))
                    continue;

                if (!string.Equals(candidate.CardCode?.Trim(), invoice.CardCode?.Trim(), StringComparison.OrdinalIgnoreCase))
                    continue;

                if (candidate.SapDocEntry is { } recorded && recorded != invoice.DocEntry)
                {
                    logger.LogWarning(
                        "Van sale {Reference} records SAP invoice {Recorded}, not {DocEntry}; a stale link, so invoice {DocNum} is not sent to its shop",
                        candidate.Reference, recorded, invoice.DocEntry, invoice.DocNum);
                    stale.Add(invoice.DocEntry);
                    continue;
                }

                links[invoice.DocEntry] = new RouteCustomerInvoiceLink(
                    candidate.RouteCustomerId,
                    candidate.RouteCustomerCode,
                    candidate.RouteCustomerName,
                    candidate.DesktopSaleId);
            }
        }

        return links;
    }

    private sealed record Candidate(
        string Reference,
        string CardCode,
        int RouteCustomerId,
        string? RouteCustomerCode,
        string? RouteCustomerName,
        int? SapDocEntry,
        int? DesktopSaleId);
}
