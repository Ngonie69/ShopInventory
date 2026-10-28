using System.Globalization;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Common.Stock;

/// <summary>
/// The units a credit note already in SAP put back on the shelf, ready to hand back to the stock
/// ledger.
/// </summary>
/// <remarks>
/// For credit notes this system did not post itself, such as an approved draft added from the
/// approvals page. SAP has already moved the stock, so the document says what came back. A credit note
/// raised here is returned by <c>CreditNoteService</c> from its own request instead.
/// </remarks>
public static class SapCreditNoteStockReturn
{
    /// <summary>
    /// Keyed on DocEntry, the one number SAP never reuses. DocNum repeats across numbering series, and
    /// a repeated key would make the ledger skip the second credit note as already returned.
    /// </summary>
    public const string KeyPrefix = "credit-note-entry:";

    public static string DocumentKey(int docEntry) => KeyPrefix + docEntry.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// One line for each document line that brought stock back. A line that only credits money
    /// (<c>WithoutInventoryMovement</c>), a service line with no item, a line with no warehouse, or a
    /// quantity that is not positive brings nothing back and is left out.
    /// </summary>
    public static IReadOnlyList<StockLedgerLine> LinesOf(SAPCreditNote creditNote) =>
        (creditNote.DocumentLines ?? [])
            .Where(line => !string.Equals(line.WithoutInventoryMovement, SapYesNo.Yes, StringComparison.OrdinalIgnoreCase))
            .Select(line => new StockLedgerLine(
                line.ItemCode?.Trim() ?? string.Empty,
                line.WarehouseCode?.Trim() ?? string.Empty,
                line.StockQuantity))
            .Where(line => line.ItemCode.Length > 0
                        && line.WarehouseCode.Length > 0
                        && line.Quantity > 0)
            .ToList();
}
