using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.DesktopCreditNotes;

/// <summary>
/// Where a credit stands, in words rather than by status name.
/// </summary>
/// <remarks>
/// <para>
/// Shared by the credit-note dialog and by every list that shows a sale's credits beside it. They were
/// one page's private helpers while there was one place to show a credit; there are now three, and a
/// second copy of these is a page that quietly disagrees with the dialog about what "Deferred" means.
/// </para>
/// <para>
/// The statuses are the API's and are matched case-blind and by name, so a value this build has not
/// heard of shows as itself instead of being reported as something it is not.
/// </para>
/// </remarks>
public static class DesktopCreditLabels
{
    /// <summary>Where the credit stands with ZIMRA.</summary>
    public static string Fiscal(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "fiscalised" => "With ZIMRA",
        "prepared" => "Saved, not yet submitted",
        "submitting" => "Submission in progress",
        "rejected" => "Refused by the device",
        "reconciliationrequired" => "Outcome unknown — check it",
        _ => string.IsNullOrWhiteSpace(status) ? "—" : status
    };

    /// <summary>The Nocturne tone class for that state.</summary>
    public static string Family(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "fiscalised" => "ops-fam-good",
        "rejected" => "ops-fam-bad",
        "reconciliationrequired" => "ops-fam-warn",
        // Saved and submitting are both on their way; neither is a fault.
        _ => "ops-fam-accent"
    };

    /// <summary>
    /// Where the back-office half of a credit stands, said in words rather than by status name.
    /// </summary>
    /// <remarks>
    /// The SAP memo is raised on its own — at once when the sale is already in SAP, otherwise when it
    /// posts — so most of these describe something happening rather than something owed. The two that
    /// do not (a refusal, and a consolidated sale) are the ones an operator has to act on.
    /// </remarks>
    public static string Sap(DesktopCreditNoteResult note) => Sap(note.SapStatus, note.SapDocNum);

    /// <summary>The same, for a row that carries the two fields rather than a whole result.</summary>
    public static string Sap(string? sapStatus, int? sapDocNum) => sapDocNum.HasValue
        ? $"credit memo #{sapDocNum}"
        : sapStatus switch
        {
            DesktopCreditSapStatuses.Deferred => "follows once this sale posts",
            DesktopCreditSapStatuses.NotRequired => "nothing owed — this sale is not posted to SAP",
            DesktopCreditSapStatuses.ManualInSap => "raise by hand against the consolidated invoice",
            DesktopCreditSapStatuses.FiscalOnly => "not raised — fiscalised only, no stock returned",
            DesktopCreditSapStatuses.Failed => "failed — will be retried",
            // A status this build predates. Shown rather than swallowed.
            _ => sapStatus ?? "—"
        };

    /// <summary>
    /// The tone for the SAP half. Only a refusal is bad and only a hand job is a warning; waiting for
    /// the sale to post is the ordinary case at a till, not a fault.
    /// </summary>
    public static string SapFamily(string? sapStatus) => sapStatus switch
    {
        DesktopCreditSapStatuses.Posted => "ops-fam-good",
        DesktopCreditSapStatuses.Failed => "ops-fam-bad",
        DesktopCreditSapStatuses.ManualInSap => "ops-fam-warn",
        DesktopCreditSapStatuses.Deferred => "ops-fam-accent",
        _ => "ops-fam-neutral"
    };

    /// <summary>Where the credited sale was rung up.</summary>
    public static string Source(string? sourceSystem) => sourceSystem?.Trim() switch
    {
        "KefalosShopTill" => "Till",
        "KefalosVending" => "Vending",
        "KefalosVanSales" or "KefalosVanSalesOnline" => "Van",
        null or "" => "—",
        var other => other
    };

    /// <summary>The headline over a finished credit's outcome.</summary>
    public static string Outcome(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "fiscalised" => "Fiscal credit note issued",
        "rejected" => "The device refused this credit",
        _ => "The fiscal outcome needs checking"
    };
}
