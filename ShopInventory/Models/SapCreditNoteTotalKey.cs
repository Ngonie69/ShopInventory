namespace ShopInventory.Models;

/// <summary>
/// One customer's credit notes of one total, entered on or after a day — what the approvals page asks
/// SAP for to tell whether a held draft has already been credited some other way.
/// </summary>
/// <param name="CardCode">The customer.</param>
/// <param name="DocTotal">The credit note total, compared exactly.</param>
/// <param name="EnteredOnOrAfter">The first day a match may have been entered; the time of day is ignored.</param>
public sealed record SapCreditNoteTotalKey(string CardCode, decimal DocTotal, DateTime EnteredOnOrAfter);
