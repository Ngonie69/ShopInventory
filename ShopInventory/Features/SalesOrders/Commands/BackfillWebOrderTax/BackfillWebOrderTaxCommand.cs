using ErrorOr;
using MediatR;

namespace ShopInventory.Features.SalesOrders.Commands.BackfillWebOrderTax;

/// <summary>
/// Repairs the tax mirror of web-created sales orders that were persisted before the create form
/// sent a tax rate, so they carry a zero tax amount and a document total equal to their subtotal.
/// </summary>
/// <param name="DryRun">
/// Reports what would change without writing. This is also how the affected population is counted:
/// the orders live in the deployed database, not in any environment a query can be run against
/// safely by hand.
/// </param>
/// <param name="MaxPostedOrders">
/// Caps how many posted orders are read back from SAP in one run, because each one costs a Service
/// Layer document read. Unposted orders are repaired locally and are not capped.
/// </param>
public sealed record BackfillWebOrderTaxCommand(
    bool DryRun = false,
    int MaxPostedOrders = 200
) : IRequest<ErrorOr<BackfillWebOrderTaxResult>>;
