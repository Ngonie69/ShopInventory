namespace ShopInventory.Models;

/// <summary>
/// One allowed value of SAP's <c>U_Reasons</c> user field — the reason a credit note line exists.
/// </summary>
/// <remarks>
/// SAP keeps this list as the valid values of a line-level UDF on <c>RIN1</c> (and its siblings on
/// every other marketing document line table), maintained by whoever administers the company
/// database. It is read from SAP rather than mirrored here because the two are not the same list:
/// production carries the <c>Re-Invoice …</c> and <c>Cancellation …</c> reasons that the test
/// database has never had. A value the running company database does not define is rejected by
/// SAP when the document is posted, so the picker and the payload must both come from here.
/// </remarks>
/// <param name="Value">The stored value. This is what goes into <c>U_Reasons</c>.</param>
/// <param name="Description">The wording a person picking the reason reads.</param>
public sealed record SapDocumentLineReason(string Value, string Description);
