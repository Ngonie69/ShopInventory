namespace ShopInventory.Configuration;

/// <summary>
/// The scheduled pass that files again the SAP invoices whose fiscalisation failed.
/// </summary>
/// <remarks>
/// See <see cref="Features.Invoices.Commands.RetryFailedInvoiceFiscalisations.RetryFailedInvoiceFiscalisationsHandler"/>.
/// An invoice this app posts to SAP is fiscalised once, straight after it is created. Before this pass, one that
/// failed then (ZIMRA unreachable on 2026-10-17 and -18) stayed owed until a person pressed Fiscalise on the
/// fiscalisation console.
/// </remarks>
public sealed class InvoiceFiscalisationRetrySettings
{
    public const string SectionName = "InvoiceFiscalisationRetry";

    /// <summary>Whether the pass is scheduled at all.</summary>
    public bool Enabled { get; set; } = true;

    public int IntervalMinutes { get; set; } = 2;

    /// <summary>
    /// How many days after an invoice first failed the pass keeps trying it. After that it is left to a person.
    /// </summary>
    public int LookbackDays { get; set; } = 7;

    /// <summary>The wait after the first failure. Each failure after it doubles the wait, up to the maximum.</summary>
    public int InitialRetryMinutes { get; set; } = 2;

    /// <summary>
    /// The longest wait between two attempts on one invoice, and how long one the fiscalise route refused is left.
    /// </summary>
    public int MaxRetryMinutes { get; set; } = 60;

    /// <summary>Invoices per pass. Each one is two SAP reads and a fiscal call.</summary>
    public int BatchSize { get; set; } = 25;

    /// <summary>The wait before attempt <paramref name="failures"/> + 1, given that many failures so far.</summary>
    public TimeSpan RetryDelay(int failures)
    {
        var initial = Math.Max(1, InitialRetryMinutes);
        var max = Math.Max(initial, MaxRetryMinutes);
        var doublings = Math.Clamp(failures - 1, 0, 16);

        return TimeSpan.FromMinutes(Math.Min((double)initial * (1 << doublings), max));
    }
}
