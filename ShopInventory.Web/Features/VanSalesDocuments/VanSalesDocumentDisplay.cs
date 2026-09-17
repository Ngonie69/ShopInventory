using System.Globalization;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.VanSalesDocuments;

/// <summary>
/// The words and colours both Van Sales document lists use for a state, so the two pages cannot describe
/// one state two ways.
/// </summary>
public static class VanSalesDocumentDisplay
{
    /// <summary>
    /// The state filter, in the order an operator works through it: what needs a person first, what is
    /// still moving next, what is finished last.
    /// </summary>
    public static readonly IReadOnlyList<(string? State, string Label)> Filters =
    [
        (null, "All"),
        (VanSalesDocumentState.NeedsAttention, "Needs attention"),
        (VanSalesDocumentState.AwaitingSap, "Awaiting SAP"),
        (VanSalesDocumentState.NotFiscalised, "Not fiscalised"),
        (VanSalesDocumentState.InProgress, "In progress"),
        (VanSalesDocumentState.Complete, "Complete")
    ];

    public static string Label(string state) => state switch
    {
        VanSalesDocumentState.Complete => "Complete",
        VanSalesDocumentState.AwaitingSap => "Awaiting SAP",
        VanSalesDocumentState.NotFiscalised => "Not fiscalised",
        VanSalesDocumentState.InProgress => "In progress",
        VanSalesDocumentState.NeedsAttention => "Needs attention",
        _ => state
    };

    /// <summary>
    /// Not fiscalised is a warning rather than a fault: a receipt may simply not have been looked up yet, and
    /// the page cannot tell that from one that was never issued.
    /// </summary>
    public static string Family(string state) => state switch
    {
        VanSalesDocumentState.Complete => "vsd-fam-good",
        VanSalesDocumentState.AwaitingSap => "vsd-fam-accent",
        VanSalesDocumentState.NotFiscalised => "vsd-fam-warn",
        VanSalesDocumentState.NeedsAttention => "vsd-fam-bad",
        _ => "vsd-fam-neutral"
    };

    public static int Count(VanSalesStateCounts counts, string? state) => state switch
    {
        null => counts.All,
        VanSalesDocumentState.Complete => counts.Complete,
        VanSalesDocumentState.AwaitingSap => counts.AwaitingSap,
        VanSalesDocumentState.NotFiscalised => counts.NotFiscalised,
        VanSalesDocumentState.InProgress => counts.InProgress,
        VanSalesDocumentState.NeedsAttention => counts.NeedsAttention,
        _ => 0
    };

    public static string Money(string currency, decimal amount) =>
        $"{currency} {amount.ToString("N2", CultureInfo.InvariantCulture)}";

    public static string Amount(decimal amount) => amount.ToString("N2", CultureInfo.InvariantCulture);

    /// <summary>
    /// The states in the order a summary card lays them out: what needs a person first, finished last.
    /// </summary>
    public static readonly IReadOnlyList<string> Lanes =
    [
        VanSalesDocumentState.NeedsAttention,
        VanSalesDocumentState.NotFiscalised,
        VanSalesDocumentState.AwaitingSap,
        VanSalesDocumentState.InProgress,
        VanSalesDocumentState.Complete
    ];

    /// <summary>
    /// Totals in more than one currency, largest share first. Each is stated in its own currency — two
    /// currencies are never added into one figure — so the first is the headline and the rest follow it.
    /// </summary>
    public static string Totals(IReadOnlyList<VanSalesMoneyTotalModel> totals) =>
        totals.Count == 0
            ? "—"
            : string.Join(" + ", totals.Select(total => Money(total.Currency, total.Amount)));

    /// <summary>A period as the summary card heads it: one day, or a range within or across years.</summary>
    public static string Range(DateTime from, DateTime to) =>
        from.Date == to.Date
            ? from.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)
            : from.Year == to.Year
                ? $"{from.ToString("dd MMM", CultureInfo.InvariantCulture)} – {to.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)}"
                : $"{from.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)} – {to.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)}";

    /// <summary>A share of a whole as a CSS width, never below a sliver so a small non-zero part stays visible.</summary>
    public static string Width(decimal part, decimal whole) =>
        whole <= 0 || part <= 0
            ? "0%"
            : $"{Math.Max(2m, Math.Round(part / whole * 100m, 1)).ToString(CultureInfo.InvariantCulture)}%";
}

/// <summary>One step of a drawer's trail.</summary>
/// <param name="Label">What the step is.</param>
/// <param name="Meta">When, or what it produced.</param>
/// <param name="Family">A <c>vsd-fam-*</c> class; <c>vsd-fam-off</c> is a step not reached yet.</param>
/// <param name="IsCurrent">The step the document is sitting on.</param>
public sealed record VanSalesTrailStep(string Label, string Meta, string Family, bool IsCurrent = false)
{
    /// <summary>
    /// Marks where the document stands: the first step that is not done, or the last one when every step is.
    /// </summary>
    public static List<VanSalesTrailStep> MarkCurrent(List<VanSalesTrailStep> steps)
    {
        var current = steps.FindIndex(step => step.Family != "vsd-fam-good");

        if (current < 0)
        {
            current = steps.Count - 1;
        }

        return steps.Select((step, index) => index == current ? step with { IsCurrent = true } : step).ToList();
    }
}
