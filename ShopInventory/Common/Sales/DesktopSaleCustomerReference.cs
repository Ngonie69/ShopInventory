namespace ShopInventory.Common.Sales;

/// <summary>
/// What a system-posted sale's SAP invoice carries in <c>NumAtCard</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Who bought, not which record.</b> <c>CardCode</c> cannot answer that question on either of these
/// routes: a van invoices its own business partner, so every sale it makes carries the same code, and a
/// vending sale is billed to the depot rather than to the cart. The shop or the vendor is held on the
/// sale in <see cref="Models.Entities.DesktopSaleEntity.RouteCustomerCode"/> and
/// <see cref="Models.Entities.DesktopSaleEntity.RouteCustomerName"/> — and until now went no further,
/// so a person reading the invoice in SAP had no way to see it at all.
/// </para>
///
/// <para>
/// <b>A name for a van, a code for vending.</b> Not an inconsistency: they are what each side's people
/// actually use. A route's customers are shops known by name, and the code is internal to this system.
/// A vending vendor <i>is</i> its code — <c>VMB001</c> — issued under
/// <see cref="Features.Vending.VendorCodeConvention"/>, unique company-wide and printed on the cart.
/// </para>
///
/// <para>
/// <b>A till keeps its reference.</b> A shop till sells over a counter to whoever is standing there;
/// there is no counterparty to name, and the reference is what support searches SAP for. Changing it
/// would cost something and buy nothing.
/// </para>
///
/// <para>
/// <b>Safe to change at all only because nothing keys on it.</b> An invoice's duplicate guard is
/// <c>U_Van_saleorder</c> — see <c>SAPServiceLayerClient.GetInvoiceByVanSaleOrderAsync</c> — with the
/// mobile invoice-number UDF beside it, and both still carry the sale's reference, as do the Remarks.
/// A <i>credit memo</i> is the opposite case and must not be changed the same way: its own
/// <c>NumAtCard</c> is the only key <c>GetCreditNoteByReferenceAsync</c> can probe on after a lost
/// reply, and a memo that cannot be found again is a memo that gets raised twice.
/// </para>
/// </remarks>
public static class DesktopSaleCustomerReference
{
    /// <summary>
    /// The width the reference is trimmed to, matching
    /// <see cref="Models.Entities.DesktopSaleEntity.NumAtCard"/>'s own column.
    /// </summary>
    /// <remarks>
    /// A customer name is free text and a business partner's name can run long, so this is a real cap
    /// rather than a formality: SAP refuses the whole document over an oversized field, which on this
    /// route would mean a fiscalised sale that can never be invoiced. Truncation loses nothing that is
    /// not also in the Remarks, which name the party again in full where they fit.
    /// </remarks>
    public const int MaxLength = 100;

    /// <summary>
    /// The reference for a sale on this table.
    /// </summary>
    /// <remarks>
    /// Falls back rather than returning nothing: <c>CreateInvoiceRequest.NumAtCard</c> is required, and
    /// an empty one would refuse the post. The rungs are in order of how much they tell the reader —
    /// the name, then the account's name, then the code, then the reference every route can always
    /// supply.
    /// </remarks>
    public static string For(Models.Entities.DesktopSaleEntity sale)
    {
        ArgumentNullException.ThrowIfNull(sale);

        return For(
            sale.SourceSystem,
            sale.RouteCustomerCode,
            sale.RouteCustomerName,
            sale.CardName,
            sale.ExternalReferenceId);
    }

    /// <summary>
    /// The same rule off loose values, for the online van route — which posts its invoice from a
    /// <c>StockReservation</c> rather than from a sale row, and carries the same three fields on it.
    /// </summary>
    public static string For(
        string? sourceSystem,
        string? routeCustomerCode,
        string? routeCustomerName,
        string? cardName,
        string? externalReference)
    {
        var fallback = Clean(externalReference) ?? string.Empty;

        if (SaleSourceSystems.IsVanSale(sourceSystem))
        {
            return Cap(Clean(routeCustomerName) ?? Clean(cardName) ?? Clean(routeCustomerCode) ?? fallback);
        }

        if (string.Equals(sourceSystem?.Trim(), SaleSourceSystems.Vending, StringComparison.OrdinalIgnoreCase))
        {
            return Cap(Clean(routeCustomerCode) ?? Clean(routeCustomerName) ?? fallback);
        }

        // A shop till, the legacy desktop source, and anything unrecognised: the reference, as before.
        return Cap(fallback);
    }

    /// <summary>
    /// Trimmed, with any inner run of whitespace collapsed to one space.
    /// </summary>
    /// <remarks>
    /// A customer name is typed by a person and arrives with double spaces and the occasional newline in
    /// it. Those reach an OData string literal on the way out and a SAP list column on the way in, and
    /// neither is improved by them.
    /// </remarks>
    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var collapsed = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return collapsed.Length == 0 ? null : collapsed;
    }

    private static string Cap(string value) =>
        value.Length <= MaxLength ? value : value[..MaxLength].TrimEnd();
}
