namespace ShopInventory.Configuration;

public sealed class CreditNoteSyncSettings
{
    public const string SectionName = "CreditNoteSync";

    /// <summary>
    /// Enables the recurring SAP-to-PostgreSQL projection job.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Makes POD reports read credit-note links from PostgreSQL instead of waiting on SAP.
    /// Kept separate from Enabled so the projection can be populated and compared before cutover.
    /// </summary>
    public bool UseForPodReports { get; set; }

    public int PollIntervalMinutes { get; set; } = 2;

    /// <summary>
    /// A report can confirm that an invoice has no credit note only while the projection is fresh.
    /// </summary>
    public int StaleAfterMinutes { get; set; } = 10;

    /// <summary>
    /// Documents in this rolling window are re-read daily to repair missed external SAP changes.
    /// </summary>
    public int ReconciliationWindowDays { get; set; } = 90;
}
