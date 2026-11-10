using ShopInventory.Services;

namespace ShopInventory.Common.Fiscalization;

/// <summary>
/// A credit note the fiscalisation platform refused for what the document says, which no retry changes.
/// </summary>
/// <remarks>
/// <para>
/// The platform checks a credit note against its original before it sends anything to ZIMRA, and answers a 400
/// whose <c>errorCode</c> is the ZIMRA rule it would break: RCPT035 when the note credits more than is left on the
/// invoice (SAP 62766 and 62752 on 2026-11-09), RCPT036 when it uses taxes the invoice did not. A posted SAP credit
/// memo cannot be edited and a fiscal receipt cannot be withdrawn, so the same memo is refused the same way every
/// time. <see cref="Services.SapCreditNoteFiscalisationSweep"/> used to send it again on each of its attempts and
/// raise the Exception Center incident only on the last (the third, two passes later).
/// </para>
/// <para>
/// Deliberately a closed list. Left out: RCPT032 (the original is not on file), because the original can be filed
/// later and the note then goes through; the platform's own <c>ValidationFailed</c> (an unknown or disabled SAP tax
/// group, say), which is put right in the platform's settings; and every device, day and transport failure.
/// </para>
/// <para>
/// The verdict is recorded on the fiscal transaction row's message (<see cref="Mark"/>), the way
/// <see cref="FiscalOutcomeMessages"/> records an unresolved one, because the row has no column for it.
/// </para>
/// </remarks>
internal static class FiscalDocumentRefusal
{
    /// <summary>Prefixed to the message recorded for such a refusal.</summary>
    public const string Prefix = "Refused by the fiscalisation console: ";

    private static readonly HashSet<string> DocumentRuleCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "RCPT015", // credited invoice data not provided
        "RCPT029", // credit note information on a regular invoice
        "RCPT033", // the credited invoice is more than 12 months old
        "RCPT034", // no note (reason) on the credit note
        "RCPT035", // the credit notes exceed what the invoice can still be credited
        "RCPT036", // taxes the original invoice did not use
        "RCPT042"  // a currency other than the original invoice's
    };

    /// <summary>Whether <paramref name="errorCode"/> names a rule only a different document can satisfy.</summary>
    public static bool IsDocumentRuleCode(string? errorCode) =>
        !string.IsNullOrWhiteSpace(errorCode) && DocumentRuleCodes.Contains(errorCode.Trim());

    /// <summary>Whether <paramref name="result"/> is such a refusal.</summary>
    public static bool Is(FiscalizationResult result) =>
        !result.Success && !result.RequiresReconciliation && IsDocumentRuleCode(result.ErrorCode);

    /// <summary>The message to record for such a refusal, carrying the marker <see cref="IsMarked"/> reads.</summary>
    public static string Mark(string? message)
    {
        var text = string.IsNullOrWhiteSpace(message) ? "the credit note breaks a ZIMRA rule." : message.Trim();
        return IsMarked(text) ? text : Prefix + text;
    }

    /// <summary>Whether a recorded message says the document was refused for itself.</summary>
    public static bool IsMarked(string? message) =>
        message?.StartsWith(Prefix, StringComparison.Ordinal) == true;
}
