namespace ShopInventory.Features.CustomerDocuments.Documents;

/// <summary>What the fiscal link check concluded.</summary>
public enum FiscalLinkVerdictKind
{
    /// <summary>A receipt was found and it belongs to this invoice.</summary>
    Verified = 0,

    /// <summary>No receipt yet, or the device could not be asked. Check again later.</summary>
    NotYet = 1,

    /// <summary>A receipt was found under this invoice's number, but it belongs to some other document.</summary>
    Mismatch = 2
}
