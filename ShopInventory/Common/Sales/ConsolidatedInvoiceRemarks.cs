namespace ShopInventory.Common.Sales;

/// <summary>
/// Builds the Remarks the daily consolidated desktop sale invoice carries in SAP.
/// </summary>
/// <remarks>
/// The remark lists the references of the sales the invoice consolidates, and one business partner can
/// run to hundreds of sales in a day, so the list outgrew the 254-character column. It now lists as
/// many whole references as fit and says how many it left out. A reference cut in half would look
/// like a real one and match nothing; the sale count stays exact, and the full set is still linked to
/// the consolidation row through <c>DesktopSaleEntity.ConsolidationId</c>.
/// </remarks>
public static class ConsolidatedInvoiceRemarks
{
    public static string Build(int saleCount, IEnumerable<string?> saleReferences)
    {
        var prefix = $"Consolidated {saleCount} desktop sale(s): ";
        var references = saleReferences
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Select(reference => reference!.Trim())
            .ToList();

        // The largest count of leading references that fits alongside the "+N more" it needs. Not a
        // simple first-overflow stop: listing the last reference also drops the suffix, so the whole
        // list can fit where all but one does not.
        var fitting = 0;
        var joinedLength = 0;
        for (var count = 1; count <= references.Count; count++)
        {
            joinedLength += references[count - 1].Length + (count > 1 ? 1 : 0);
            if (prefix.Length + joinedLength > DesktopSaleInvoiceRemarks.MaxLength)
            {
                break;
            }

            if (prefix.Length + joinedLength + Omitted(references.Count - count).Length
                <= DesktopSaleInvoiceRemarks.MaxLength)
            {
                fitting = count;
            }
        }

        var listed = fitting == 0 ? prefix.TrimEnd() : prefix + string.Join(",", references.Take(fitting));

        return listed + Omitted(references.Count - fitting);
    }

    private static string Omitted(int count) => count == 0 ? "" : $" (+{count} more)";
}
