using ShopInventory.Services;

namespace ShopInventory.Common.Fiscalization;

/// <summary>
/// The status a fiscal result is recorded under on its fiscal transaction row.
/// </summary>
/// <remarks>
/// One rule for every path that records a fiscalisation it asked for itself: the manual fiscalise route,
/// the background queue after an invoice is created, and <see cref="Features.CreditNotes.SapCreditNoteFiscaliser"/>.
/// Each used to carry its own copy, and fixing one left the others wrong.
/// </remarks>
internal static class FiscalTransactionStatus
{
    public const string Failed = "Failed";

    public const string Fiscalised = "Fiscalised";

    public const string Success = "Success";

    /// <summary>The status to record <paramref name="result"/> under.</summary>
    /// <remarks>
    /// A failure first. A platform dry run comes back <c>Skipped</c> and not <c>Success</c>, and testing
    /// <c>Skipped</c> first recorded it as "Fiscalised": evidence of a receipt that was never filed. Every list
    /// then showed the document as complete, the fiscalisation console's work queue dropped it, and the
    /// manual fiscalise route refused it as already done, so nothing would ever file it. <c>Skipped</c> with
    /// <c>Success</c> is a document the device already holds, and is fiscalised.
    /// </remarks>
    public static string Of(FiscalizationResult result) =>
        !result.Success
            ? Failed
            : result.Skipped
                ? Fiscalised
                : Success;
}
