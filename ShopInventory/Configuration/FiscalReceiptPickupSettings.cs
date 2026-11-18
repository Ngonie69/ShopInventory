namespace ShopInventory.Configuration;

/// <summary>
/// The scheduled pass that reads back from the fiscal device the receipts of SAP invoices and credit notes whose
/// fiscalisation failed, and records the ones it finds.
/// </summary>
/// <remarks>
/// See <see cref="Features.FiscalReceipts.Commands.PickUpFailedFiscalReceipts.PickUpFailedFiscalReceiptsHandler"/>.
/// It only asks; it never files. Before it, a document whose attempt failed but whose receipt the platform went on
/// to hold read "Not fiscalised" until a person pressed Fiscalise and the platform answered with that receipt.
/// </remarks>
public sealed class FiscalReceiptPickupSettings
{
    public const string SectionName = "FiscalReceiptPickup";

    /// <summary>Whether the pass is scheduled at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Whether it asks about credit notes as well as invoices.</summary>
    public bool IncludeCreditNotes { get; set; } = true;

    /// <summary>
    /// A CAT date. An invoice whose first row in the fiscal log is earlier is never asked about; null asks about
    /// every invoice inside <see cref="LookbackDays"/>.
    /// </summary>
    /// <remarks>
    /// The first row stands in for the invoice's date because the log holds no SAP date, and reading one would
    /// cost a SAP read per invoice. For an invoice this app posted, that row is the posting's own attempt; for one
    /// raised in SAP, it is the status backfill's read, made when the invoice list first showed it, normally the
    /// same day. So every invoice from this date on is taken, and an older one first opened on or after it is
    /// taken too. That costs only a look-up: the pass never files.
    /// </remarks>
    public DateTime? InvoicesFrom { get; set; }

    public int IntervalMinutes { get; set; } = 5;

    /// <summary>How many days after a document's latest failed attempt the pass keeps asking about it.</summary>
    public int LookbackDays { get; set; } = 14;

    /// <summary>
    /// The wait after a failed attempt before the first look-up. Each "not there yet" after it doubles the wait,
    /// up to the maximum.
    /// </summary>
    public int InitialLookupMinutes { get; set; } = 5;

    /// <summary>The longest wait between two look-ups of one document.</summary>
    public int MaxLookupMinutes { get; set; } = 60;

    /// <summary>
    /// Documents per kind per pass. Each one is a single read against the fiscal device and no SAP read.
    /// </summary>
    public int BatchSize { get; set; } = 25;

    /// <summary>
    /// How much earlier than a document's first recorded attempt a receipt may be dated and still be taken as its
    /// own. Allows for the device's clock; anything older belongs to an earlier document with the same number.
    /// </summary>
    public int ReceiptDateSlackHours { get; set; } = 24;

    /// <summary>The wait before look-up <paramref name="misses"/> + 1, given that many "not there yet" answers.</summary>
    public TimeSpan LookupDelay(int misses)
    {
        var initial = Math.Max(1, InitialLookupMinutes);
        var max = Math.Max(initial, MaxLookupMinutes);
        var doublings = Math.Clamp(misses, 0, 16);

        return TimeSpan.FromMinutes(Math.Min((double)initial * (1 << doublings), max));
    }
}
