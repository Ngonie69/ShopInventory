namespace ShopInventory.Models.Entities;

/// <summary>
/// The SAP VAT group an item is sold under, kept locally so a sale can be taxed without asking SAP.
/// </summary>
/// <remarks>
/// A till sends no tax code — it has no source for one, and should not be trusted with one anyway:
/// a tax code arriving from a client is a tax code a client can get wrong. So every till line fell to
/// <see cref="Configuration.TaxSettings.VatRate"/>, and the customer was charged 15.5% on zero-rated
/// goods and the receipt declared to ZIMRA said the same.
///
/// <para>
/// Read from the item master rather than resolved per sale, for the reason
/// <see cref="Services.SapItemUomWarmJob"/> gives about UoMs: a SAP read on an interactive path is
/// paid by whoever is standing at the counter, against a concurrency limit shared with everything
/// else the process does. This is the durable copy that read fills, and the sale path only ever
/// touches the local table.
/// </para>
/// </remarks>
public class SapItemTaxGroupEntity
{
    public long Id { get; set; }

    /// <summary>The item, as SAP spells it.</summary>
    public string ItemCode { get; set; } = string.Empty;

    /// <summary>
    /// The item master's <c>VatGroup</c> — a code from OVTG, such as <c>O01</c> or <c>O0</c>.
    /// </summary>
    /// <remarks>
    /// Called a VAT group and not a tax code because that is the field it comes from. SAP returns
    /// <c>TaxCode</c> null on a marketing document line and puts the code here, which is the same
    /// trap <c>RevmaxFiscalizationService.NormalizeTaxCode</c> exists to handle.
    /// </remarks>
    public string VatGroup { get; set; } = string.Empty;

    public DateTime ResolvedAtUtc { get; set; }
}
