using System.Globalization;

namespace ShopInventory.Common.Sap;

/// <summary>
/// Maps <c>OJDT.TransType</c> to the abbreviation and document name SAP itself prints.
/// </summary>
/// <remarks>
/// The abbreviations are deliberately SAP's rather than ours: both the customer statement and the
/// G/L ledger exist to be read against SAP's own Account Balance window, and a column that says
/// something different for the same row makes that comparison harder rather than easier. "PS" for
/// an outgoing payment is the example — "PY" reads more naturally and is wrong.
/// </remarks>
public static class SapJournalOrigin
{
    public static (string OriginCode, string DocumentType) Map(int transType) => transType switch
    {
        -2 => ("OB", "Opening Balance"),
        13 => ("IN", "A/R Invoice"),
        14 => ("CN", "A/R Credit Memo"),
        15 => ("DN", "Delivery"),
        16 => ("RT", "Return"),
        18 => ("PU", "A/P Invoice"),
        19 => ("PC", "A/P Credit Memo"),
        20 => ("GR", "Goods Receipt PO"),
        21 => ("GS", "Goods Return"),
        24 => ("RC", "Incoming Payment"),
        30 => ("JE", "Journal Entry"),
        // "PS", not "PY" — this is the abbreviation SAP's Account Balance window prints.
        46 => ("PS", "Outgoing Payment"),
        58 => ("SN", "Stock Posting"),
        59 => ("GI", "Goods Receipt"),
        60 => ("GO", "Goods Issue"),
        67 => ("TR", "Inventory Transfer"),
        68 => ("WO", "Work Order"),
        69 => ("LC", "Landed Costs"),
        162 => ("MR", "Material Revaluation"),
        _ => (transType.ToString(CultureInfo.InvariantCulture), $"Transaction {transType}")
    };
}
