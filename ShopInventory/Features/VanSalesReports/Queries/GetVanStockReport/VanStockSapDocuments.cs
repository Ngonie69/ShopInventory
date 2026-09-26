using System.Globalization;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesReports.Queries.GetVanStockReport;

/// <summary>What a SAP document did to one item on one van.</summary>
/// <param name="Quantity">Signed, in the inventory unit: positive onto the van, negative off it.</param>
/// <param name="CreatedAt">When SAP created the document, on the CAT clock.</param>
/// <param name="CreatedTimeKnown">
/// False for a stock transfer, which SAP stamps with a creation date and no time. The reader then
/// places it at midday — see <see cref="SapVanStockDocuments"/>.
/// </param>
public sealed record VanStockSapMovement(
    string WarehouseCode,
    VanStockDocumentKind Kind,
    int DocEntry,
    int DocNum,
    DateTime DocDate,
    DateTime CreatedAt,
    bool CreatedTimeKnown,
    string? Comments,
    string ItemCode,
    string? ItemDescription,
    decimal Quantity);

public enum VanStockDocumentKind
{
    Invoice,
    CreditNote,
    TransferIn,
    TransferOut
}

/// <summary>The SAP half of the report, or the reason there is none.</summary>
public sealed record VanStockSapRead(bool Available, string? Problem, List<VanStockSapMovement> Movements)
{
    public static VanStockSapRead Unavailable(string problem) => new(false, problem, []);
}

/// <summary>
/// Reads the SAP documents that moved stock onto and off each van, by the date SAP created them.
/// </summary>
public interface IVanStockSapDocuments
{
    /// <param name="accountsByVan">Each van warehouse and the business partners its sales invoice to.</param>
    Task<VanStockSapRead> LoadAsync(
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> accountsByVan,
        DateTime createdFrom,
        DateTime createdTo,
        CancellationToken cancellationToken);
}

/// <remarks>
/// <para>Why creation date and not document date. The morning stock count is SAP's own
/// <c>OnHand</c>, so it moves when SAP gets a document, not on the day the document is dated. On
/// 2026-09-25 VAN001's sales for the 23rd (invoice 779350) were created at 09:22 on the 25th, and a
/// load keyed as "21/09" (transfer 88892) was created on the 24th. Placing either by its printed date
/// charges the van with missing stock one morning and found stock the next.</para>
///
/// <para>Invoices and credit notes carry <c>DocTime</c>; stock transfers carry no time at all. A
/// transfer is placed at midday on its creation date — during the working day, after the morning
/// count — unless TransferEventListener recorded the moment it saw it, which the handler prefers.</para>
///
/// <para>Goods issues and receipts are not read. The Service Layer cannot filter them by warehouse
/// (the warehouse is on the lines, and lambda filters are refused), and reading the company's ~100 a
/// day unfiltered costs minutes. They are what is left when a morning's count is compared with the
/// documents read here, and the report says so rather than naming them.</para>
/// </remarks>
public sealed class SapVanStockDocuments(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<SapVanStockDocuments> logger
) : IVanStockSapDocuments
{
    private static readonly TimeSpan UnknownTransferTime = TimeSpan.FromHours(12);

    public async Task<VanStockSapRead> LoadAsync(
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> accountsByVan,
        DateTime createdFrom,
        DateTime createdTo,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
        {
            return VanStockSapRead.Unavailable("SAP is switched off for this API, so no SAP document was read.");
        }

        var vans = new HashSet<string>(accountsByVan.Keys, StringComparer.OrdinalIgnoreCase);
        var movements = new List<VanStockSapMovement>();

        try
        {
            // An account can serve two vans, and a van can post to two accounts (one per currency),
            // so each account is read once and its lines are matched to whichever van they came off.
            var accounts = accountsByVan.Values
                .SelectMany(codes => codes)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Sequential on purpose: the Service Layer has six slots shared with every till and
            // handset, and a report that fans out can starve them.
            foreach (var account in accounts)
            {
                foreach (var kind in new[] { SapStockDocumentKind.Invoice, SapStockDocumentKind.CreditNote })
                {
                    var documents = await sapClient.GetSalesDocumentsCreatedAsync(
                        kind, account, createdFrom, createdTo, cancellationToken);

                    movements.AddRange(documents.SelectMany(document => SalesMovements(document, vans)));
                }
            }

            var transfers = await sapClient.GetStockTransfersCreatedAsync(
                vans.ToList(), createdFrom, createdTo, cancellationToken);

            movements.AddRange(transfers.SelectMany(document => TransferMovements(document, vans)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Van stock report could not read SAP documents");
            return VanStockSapRead.Unavailable($"SAP documents could not be read: {ex.Message}");
        }

        return new VanStockSapRead(true, null, movements);
    }

    private static IEnumerable<VanStockSapMovement> SalesMovements(SapStockMovementDocument document, HashSet<string> vans)
    {
        var (docDate, createdAt, timeKnown) = Dates(document);
        var kind = document.Kind == SapStockDocumentKind.Invoice ? VanStockDocumentKind.Invoice : VanStockDocumentKind.CreditNote;
        var sign = kind == VanStockDocumentKind.Invoice ? -1m : 1m;

        foreach (var line in document.Lines)
        {
            if (line.ItemCode is null || line.WarehouseCode is null || !vans.Contains(line.WarehouseCode))
            {
                continue;
            }

            yield return new VanStockSapMovement(
                line.WarehouseCode.Trim().ToUpperInvariant(), kind, document.DocEntry, document.DocNum,
                docDate, createdAt, timeKnown, document.Comments,
                line.ItemCode.ToUpperInvariant(), line.ItemDescription,
                sign * line.StockQuantity);
        }
    }

    private static IEnumerable<VanStockSapMovement> TransferMovements(SapStockMovementDocument document, HashSet<string> vans)
    {
        var (docDate, createdAt, timeKnown) = Dates(document);

        foreach (var line in document.Lines)
        {
            if (line.ItemCode is null)
            {
                continue;
            }

            // A line may name its own warehouses; the header's are the default. A cancellation is the
            // same transfer with negative quantities, so the sign carries it without special casing.
            var from = line.FromWarehouseCode ?? document.FromWarehouse;
            var to = line.WarehouseCode ?? document.ToWarehouse;
            var itemCode = line.ItemCode.ToUpperInvariant();

            if (from is not null && vans.Contains(from))
            {
                yield return new VanStockSapMovement(
                    from.Trim().ToUpperInvariant(), VanStockDocumentKind.TransferOut, document.DocEntry, document.DocNum,
                    docDate, createdAt, timeKnown, document.Comments, itemCode, line.ItemDescription,
                    -line.StockQuantity);
            }

            if (to is not null && vans.Contains(to))
            {
                yield return new VanStockSapMovement(
                    to.Trim().ToUpperInvariant(), VanStockDocumentKind.TransferIn, document.DocEntry, document.DocNum,
                    docDate, createdAt, timeKnown, document.Comments, itemCode, line.ItemDescription,
                    line.StockQuantity);
            }
        }
    }

    internal static (DateTime DocDate, DateTime CreatedAt, bool TimeKnown) Dates(SapStockMovementDocument document)
    {
        var docDate = ParseDate(document.DocDate) ?? DateTime.MinValue;
        var created = ParseDate(document.CreationDate) ?? docDate;

        if (TimeSpan.TryParse(document.DocTime, CultureInfo.InvariantCulture, out var time))
        {
            return (docDate, created + time, true);
        }

        return (docDate, created + UnknownTransferTime, false);
    }

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? DateTime.SpecifyKind(parsed.Date, DateTimeKind.Unspecified)
            : null;
}
