using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Commands.PostDesktopSaleToSap;

/// <summary>
/// Posts one already-fiscalised sale to SAP now, instead of waiting for the pass that would.
/// </summary>
/// <remarks>
/// <para>
/// Keyed on the sale's own external reference rather than its row id, because that is the reference
/// the console shows, the one SAP holds in <c>U_Van_saleorder</c>, and the one the post is made
/// idempotent on. A caller reading a fiscal number off a receipt can act on it without a lookup.
/// </para>
/// <para>
/// <c>CallerUserId</c> is required and leading for the same reason it is on
/// <c>GetDesktopSalesQuery</c>: it is what the warehouse scope is resolved from. This one writes, so
/// it matters more here than there — without it a shop-scoped account could post another shop's
/// takings to SAP.
/// </para>
/// <para>
/// <c>WithoutReceipt</c> is the Admin's fiscal outage override: it lets a till or vending sale that is
/// still Pending or Failed reach SAP, with the receipt left to the sweep. See
/// <c>DesktopSalePostEligibility.RefusalWithoutReceipt</c>.
/// </para>
/// </remarks>
public sealed record PostDesktopSaleToSapCommand(
    Guid CallerUserId,
    string ExternalReferenceId,
    bool WithoutReceipt = false
) : IRequest<ErrorOr<DesktopSalePostResult>>;
