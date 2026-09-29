using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;

namespace ShopInventory.Common.Stock;

/// <summary>
/// What the tills have sold out of a warehouse that SAP has not been told about yet, asked by anything
/// about to take stock out of that warehouse in SAP.
/// </summary>
/// <remarks>
/// <para><b>Why a transfer has to ask.</b> A till sale is checked against SAP at the counter, fiscalised,
/// and reaches SAP as an invoice up to a minute later — or, if SAP refuses it, not until someone fixes
/// it. Until then SAP still shows its units. A transfer that reads SAP in that window sees them as free
/// and takes them, and the sale, already on a ZIMRA receipt, is then refused for stock that left on a
/// van. KEF-FAC-20260928-C35E53C1E13E sold 8 YOG145 at KEFGRC at 13:28; transfer 89450 to VAN002 took
/// all 685 SAP held at 13:31, those 8 included, and the sale could not be invoiced.</para>
///
/// <para><b>An item-level figure is enough.</b> The sale's own post allocates FEFO across whatever
/// batches are left, so it needs the units to remain in the warehouse, not any particular batch. A
/// transfer that leaves at least this much of each item behind cannot strand it.</para>
///
/// <para><b>Credits are not added back</b>, for the reason <c>CounterSapStockCheck</c> gives: SAP only
/// gets returned units when the credit memo posts, which may be after this transfer, or never.</para>
/// </remarks>
public interface IUnpostedTillClaims
{
    /// <summary>
    /// Units sold and not yet in SAP, per item, for <paramref name="warehouseCode"/>. Only items with
    /// something outstanding appear; a warehouse no till sells from answers empty.
    /// </summary>
    Task<IReadOnlyDictionary<string, decimal>> ForWarehouseAsync(
        string warehouseCode,
        CancellationToken cancellationToken);
}

public sealed class UnpostedTillClaims(
    ApplicationDbContext context,
    IOptions<DailyStockSettings> dailyStock,
    ILogger<UnpostedTillClaims> logger) : IUnpostedTillClaims
{
    public async Task<IReadOnlyDictionary<string, decimal>> ForWarehouseAsync(
        string warehouseCode,
        CancellationToken cancellationToken)
    {
        var settings = dailyStock.Value;

        var outstanding = await UnpostedTillSales.OutstandingAsync(
            context,
            warehouseCode,
            StockLedgerDay.Today(settings.StockFetchTimeCAT),
            settings.StockFetchTimeCAT,
            settings.UnpostedSaleLookbackDays,
            logger,
            cancellationToken,
            netReturnedCredits: false);

        return outstanding
            .Where(claim => claim.Value > 0)
            .ToDictionary(claim => claim.Key, claim => claim.Value, StringComparer.OrdinalIgnoreCase);
    }
}
