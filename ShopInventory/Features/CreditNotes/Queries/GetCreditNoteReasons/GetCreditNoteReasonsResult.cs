namespace ShopInventory.Features.CreditNotes.Queries.GetCreditNoteReasons;

/// <param name="Value">What is stored on the credit note line. Send this back when creating one.</param>
/// <param name="Description">The wording to show whoever is picking a reason.</param>
public sealed record CreditNoteReasonOption(string Value, string Description);

/// <param name="Reasons">
/// SAP's list, in SAP's order. Empty when the company database defines no reason field, which is a
/// configuration state rather than a failure: a credit note can still be raised without a reason.
/// </param>
public sealed record GetCreditNoteReasonsResult(IReadOnlyList<CreditNoteReasonOption> Reasons);
