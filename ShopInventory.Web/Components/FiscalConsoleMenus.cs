using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components;

/// <summary>
/// The fiscalisation console's two filter menus, and the colour vocabulary they share with its tables.
/// </summary>
/// <remarks>
/// Out of the page, and out of literals, so that one function decides what colour a state is. Written
/// inline the menus were a second switch over the same statuses, and the lifecycle one had already
/// drifted: it painted an open day <c>info</c> and a closed one <c>accent</c> while the table under it
/// drew both grey. On a page where the swatch is the summary someone acts on, filtering by a colour and
/// then reading a different one is not a cosmetic problem.
///
/// Public so the pairing can be tested. A private static array inside a Razor component cannot be, which
/// is how the drift lasted.
/// </remarks>
public static class FiscalConsoleMenus
{
    /// <summary>What is outstanding, worst last so the menu reads as an escalation.</summary>
    public static readonly IReadOnlyList<NocturneSelectOption<string>> WorkQueueStatus =
    [
        new(string.Empty, "Everything outstanding", "neutral") { RuleAfter = true, IsUnset = true },
        QueueFilter(FiscalWorkQueueFilter.AwaitingFiscalisation, "Awaiting fiscalisation"),
        QueueFilter(FiscalWorkQueueFilter.FiscalisationFailed, "Fiscalisation failed"),
        QueueFilter(FiscalWorkQueueFilter.HandoverFailed, "Hand-over failed"),
        QueueFilter(FiscalWorkQueueFilter.Unstamped, "Never stamped"),
        QueueFilter(FiscalWorkQueueFilter.NeedsReconciliation, "Unresolved — reconcile"),
        QueueFilter(FiscalWorkQueueFilter.ChainBroken, "Chain broken"),
        QueueFilter(FiscalWorkQueueFilter.Unsignable, "Unsignable")
    ];

    /// <summary>The lifecycle in order, so the menu reads as the journey it is.</summary>
    public static readonly IReadOnlyList<NocturneSelectOption<string>> FiscalDayStatus =
    [
        new(string.Empty, "Every day", "neutral") { RuleAfter = true, IsUnset = true },

        // The union of the two stopped statuses, so it takes the worse of their two families.
        new(FiscalDayStatusFilter.NeedsAttention, "Needs a person", DaySeverity("NeedsReconciliation"))
        {
            RuleAfter = true
        },

        .. FiscalDayStatusFilter.Lifecycle.Select(DayOption)
    ];

    /// <summary>
    /// The one place a lifecycle step's colour is decided — for the row's dot and the menu's swatch alike.
    /// </summary>
    public static string DaySeverity(string status) => status switch
    {
        "Submitted" => "good",
        "NeedsReconciliation" => "bad",
        "Failed" => "warn",

        // The steps between closed and submitted: work in progress rather than a problem, but not
        // finished either — a day sitting on one of them overnight is what that section is for.
        "Closed" or "FileGenerated" => "accent",
        "Open" or "Drained" => "info",
        _ => "neutral"
    };

    /// <summary>The wording for a lifecycle step, which is not always its enum name.</summary>
    public static string DayLabel(string status) => status switch
    {
        "FileGenerated" => "File built",
        "NeedsReconciliation" => "Unresolved",
        _ => status
    };

    private static NocturneSelectOption<string> QueueFilter(string filter, string label) =>
        new(filter, label, FiscalWorkQueueFilter.SeverityOf(filter));

    private static NocturneSelectOption<string> DayOption(string status) =>
        new(status, DayLabel(status), DaySeverity(status));
}
