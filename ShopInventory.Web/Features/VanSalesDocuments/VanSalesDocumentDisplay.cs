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
}
