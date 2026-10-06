namespace ShopInventory.Common.Fiscalization;

/// <summary>
/// Reads the verdict a fiscal transaction row's message carries when its status cannot.
/// </summary>
/// <remarks>
/// Shared by the fiscalisation console's work queue, which must not offer a Fiscalise button on an
/// unresolved document, and by <see cref="Services.SapCreditNoteFiscalisationSweep"/>, which must not
/// resend one. The two asking the same question in two places is how one of them comes to answer it
/// differently, and the cost of that is a second fiscal receipt nobody can withdraw.
/// </remarks>
internal static class FiscalOutcomeMessages
{
    /// <summary>
    /// Prefixed to a message that says the outcome is unknown in words <see cref="IsUnresolved"/> would
    /// not otherwise recognise — REVMax's "returned an empty body", for one.
    /// </summary>
    public const string UnresolvedPrefix = "Unresolved: ";

    /// <summary>
    /// Whether the message recorded against a SAP document says the outcome was never established.
    /// </summary>
    /// <remarks>
    /// Read out of prose, which is not where a verdict this expensive belongs, and it is worth saying why
    /// it is here. A sale records the verdict properly, in
    /// <c>DesktopSaleEntity.FiscalizationRequiresReconciliation</c>, and the queue reads that column. A
    /// SAP document has no equivalent column, and the manual fiscalise path collapses a reconciliation
    /// result to <c>Status = "Failed"</c> before writing it — so after a reload the row is
    /// indistinguishable from a plain refusal and is offered a Fiscalise button again. The console's
    /// in-session lock-out closes that window only until someone presses F5.
    ///
    /// Until the transaction row can carry the verdict itself, the wording the fiscalisation service
    /// writes alongside it is the only surviving trace. The markers are matched as a family and
    /// case-folded, and the set is deliberately generous: reading an ambiguous outcome as an ordinary
    /// failure invites the retry that signs one sale twice, while reading an ordinary failure as
    /// ambiguous costs a look-up.
    /// </remarks>
    public static bool IsUnresolved(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var folded = message.ToLowerInvariant();

        return UnresolvedMarkers.Any(marker => folded.Contains(marker, StringComparison.Ordinal));
    }

    /// <summary>
    /// The message to record for a result that requires reconciliation, carrying a marker
    /// <see cref="IsUnresolved"/> recognises whatever the provider's own wording was.
    /// </summary>
    public static string MarkUnresolved(string? message)
    {
        var text = string.IsNullOrWhiteSpace(message)
            ? "The fiscal outcome was not established. Look the document up before resubmitting."
            : message;

        return IsUnresolved(text) ? text : UnresolvedPrefix + text;
    }

    /// <summary>Lower-cased, because <see cref="IsUnresolved"/> folds the message before matching.</summary>
    private static readonly string[] UnresolvedMarkers =
    [
        // "The fiscal outcome is unresolved. Check the receipt on the fiscalisation console before any
        // resubmission — it may already exist." — FiscalizationService, on RequiresReconciliation.
        "unresolved",
        "reconcil",
        "indeterminate",
        "idempotency_",
        "chainbreak"
    ];
}
