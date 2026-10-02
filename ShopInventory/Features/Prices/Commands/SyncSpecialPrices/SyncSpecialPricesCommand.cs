using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Prices.Commands.SyncSpecialPrices;

/// <summary>
/// Copies SAP's current special prices into <c>BusinessPartnerSpecialPrices</c>, apart from the item
/// price catalogue: there are far fewer of them, so they sync in a fraction of the time.
/// </summary>
public sealed record SyncSpecialPricesCommand() : IRequest<ErrorOr<SpecialPriceSyncResult>>;

public sealed record SpecialPriceSyncResult(
    int SpecialPriceCount,
    int RemovedCount,
    DateTime SyncedAt);
