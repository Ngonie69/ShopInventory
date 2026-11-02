namespace ShopInventory.Common.Sales;

/// <summary>
/// The currency code SAP knows a document currency by.
/// </summary>
/// <remarks>
/// SAP's code for Zimbabwe Gold is <c>ZiG</c>, with a lower-case i, and the Service Layer matches a
/// document's <c>DocCurrency</c> against its currency table case-sensitively. The rest of this system
/// writes it <c>ZIG</c> (or the ISO <c>ZWG</c>), and SAP refuses either one with
/// "[ORDR.DocCur] , 'Linked value ZIG does not exist'" (SO-20261102-0040, TMP156, 2026-11-02).
///
/// <para>
/// The production currency table, read 2026-11-02: <c>ZiG</c> "Zimbabwe Gold" (documents code ZWG),
/// <c>ZW$</c> "Zimbabwe RTGS", <c>ZWL</c> "Zimbabwe Dollar", <c>RTG</c>, <c>USD</c> and other foreign
/// codes, all of them upper-case. <c>ZW$</c> is the retired RTGS dollar, not ZiG: no invoice, order or
/// credit note in production has ever carried it.
/// </para>
/// </remarks>
public static class SapCurrencyCodes
{
    /// <summary>SAP's code for Zimbabwe Gold.</summary>
    public const string Zig = "ZiG";

    /// <summary>
    /// <paramref name="currency"/> as SAP's code: <c>ZiG</c> for any spelling of ZiG or ZWG, and every
    /// other code trimmed and upper-cased.
    /// </summary>
    public static string ToSapCode(string currency)
    {
        var upper = currency.Trim().ToUpperInvariant();
        return upper is "ZIG" or "ZWG" ? Zig : upper;
    }
}
