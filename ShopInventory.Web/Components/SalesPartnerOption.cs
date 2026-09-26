namespace ShopInventory.Web.Components;

/// <summary>
/// One business partner in a <see cref="SalesPartnerPicker"/>: who, and what they bought in the period.
/// </summary>
/// <remarks>
/// A partner with no sales in the period carries zeros and is offered only under "All partners" — it is a
/// valid filter (the page then says it sold nothing), but it is not an answer to "who traded".
/// </remarks>
/// <param name="Code">The CardCode, which is the bound value and what disambiguates two identical names.</param>
/// <param name="Name">What the partner's sales called it, else its SAP name.</param>
/// <param name="TotalAmount">Takings in the period, in the currency on screen.</param>
/// <param name="SalesCount">Sales in the period.</param>
/// <param name="SharePercent">Its share of the period's takings, as the API rounded it.</param>
public sealed record SalesPartnerOption(
    string Code,
    string Name,
    decimal TotalAmount = 0m,
    int SalesCount = 0,
    decimal SharePercent = 0m)
{
    public bool Traded => SalesCount > 0;
}
