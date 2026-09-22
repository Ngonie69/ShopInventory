namespace ShopInventory.Features.Maintenance;

/// <summary>
/// How much of an app is taken away while maintenance runs.
/// </summary>
public enum MobileMaintenanceScope
{
    /// <summary>
    /// Transactions only: the phones can still read, but nothing they send changes anything.
    /// </summary>
    /// <remarks>
    /// The default, and the one to reach for. A driver halfway through a round can still open a
    /// customer, look up a price and read back what they already captured; they simply cannot post
    /// while the database or SAP is being worked on. Taking the whole app away instead turns a
    /// maintenance window into a field outage.
    /// </remarks>
    Transactions = 0,

    /// <summary>
    /// Everything: reads are refused too, leaving only signing in and the status check.
    /// </summary>
    /// <remarks>
    /// For maintenance the reads themselves cannot survive — a database restore, or a schema
    /// migration that will make the API answer wrongly rather than not at all. Stale prices on a
    /// phone are worse than no prices.
    /// </remarks>
    All = 1
}
