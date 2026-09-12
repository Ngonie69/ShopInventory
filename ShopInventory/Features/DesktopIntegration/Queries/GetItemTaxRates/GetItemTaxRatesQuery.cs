using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetItemTaxRates;

/// <summary>
/// The VAT rate every sellable item is charged at, for a till that has to price a basket itself.
/// </summary>
/// <remarks>
/// A till shows a price with VAT on it, prints a receipt with VAT on it, and reports a day's takings
/// with VAT on it — all before the platform has seen the sale. It therefore needs the same answer
/// <c>CreateDesktopSaleHandler</c> will reach when the sale arrives, and it needs it offline.
///
/// <para>
/// Until this existed the till carried its own list of exempt item codes, hard-coded in
/// <c>TaxHelper</c>. It named LAC002, SUP001, SUP002 and everything beginning FRM, none of which is
/// what SAP holds: LAC002 and SUP001 are standard-rated in the item master, and the zero-rated items
/// it did not name were charged 15.5% at the counter. Every sale of one of those was a gap between
/// the receipt the customer took away and the invoice SAP raised.
/// </para>
/// </remarks>
public sealed record GetItemTaxRatesQuery : IRequest<ErrorOr<ItemTaxRatesResult>>;

/// <summary>Every item's rate, and what an item this does not name is charged.</summary>
/// <param name="DefaultRate">
/// The rate for an item with no VAT group stored — <c>Tax:VatRate</c>, the standard rate. The same
/// fallback the sale path applies, so a till that has not heard of an item charges what the platform
/// will charge for it.
/// </param>
/// <param name="ResolvedAtUtc">
/// When the newest row in this answer was last read from SAP, or null when there are none. The till
/// shows this as the age of its tax table; a table older than a night means the warm job has stopped.
/// </param>
/// <param name="Items">One row per item the item master gave a VAT group for.</param>
public sealed record ItemTaxRatesResult(
    decimal DefaultRate,
    DateTime? ResolvedAtUtc,
    List<ItemTaxRateDto> Items
);

/// <summary>What one item is taxed at.</summary>
/// <param name="ItemCode">The item, as SAP spells it.</param>
/// <param name="VatGroup">
/// The item master's VAT group (OVTG.Code), carried so a receipt can say which group it charged under
/// and a mismatch can be read rather than guessed at.
/// </param>
/// <param name="Rate">
/// The rate as a decimal (15.5% is 0.155), resolved through <c>Tax:RatesByTaxCode</c> — the same
/// table the sale is charged from and the fiscal receipt is declared under.
/// </param>
public sealed record ItemTaxRateDto(
    string ItemCode,
    string VatGroup,
    decimal Rate
);
