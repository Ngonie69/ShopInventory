namespace ShopInventory.Configuration;

/// <summary>
/// The scheduled pass that fiscalises SAP credit memos nothing else has filed.
/// </summary>
/// <remarks>
/// See <see cref="Services.SapCreditNoteFiscalisationSweep"/>. A memo keyed straight into SAP B1 reaches
/// ZIMRA only if a clerk prints it (the platform's B1 bridge hooks the print) — before the platform
/// cut-over, REVMax's own SAP add-on filed it. This is what files the rest.
/// </remarks>
public sealed class CreditNoteFiscalisationSettings
{
    public const string SectionName = "CreditNoteFiscalisation";

    /// <summary>Whether the pass is scheduled at all.</summary>
    public bool Enabled { get; set; } = true;

    public int IntervalMinutes { get; set; } = 10;

    /// <summary>How many days of memo dates each pass looks back over, today included.</summary>
    public int LookbackDays { get; set; } = 14;

    /// <summary>
    /// How long a memo is left after it last changed in SAP before the pass takes it.
    /// </summary>
    /// <remarks>
    /// Room for whoever raised it to fiscalise it first — the approval add files its own memo in the same
    /// request, and a clerk may print one in B1. Both would be safe to race (the platform refuses a second
    /// receipt for one number, and REVMax is asked before it is filed), but not racing is cheaper.
    /// </remarks>
    public int GraceMinutes { get; set; } = 15;

    /// <summary>Memos per pass. Each one is a SAP read or two and a fiscal call.</summary>
    public int BatchSize { get; set; } = 25;

    /// <summary>
    /// Attempts per memo before the pass leaves it to a person. The last one raises an Exception Center
    /// incident; the earlier ones do not, or one refusal would raise one incident per pass.
    /// </summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>
    /// Comma-separated business partner code prefixes whose memos this pass leaves alone. Exports are
    /// fiscalised by a route of their own.
    /// </summary>
    /// <remarks>
    /// A string, not a list: the configuration binder appends to a list that already has items, so a
    /// default list here would be doubled rather than replaced by appsettings.
    /// </remarks>
    public string ExcludedCardCodePrefixes { get; set; } = "EXP";

    public IReadOnlyList<string> ExcludedPrefixes() =>
        ExcludedCardCodePrefixes
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
}
