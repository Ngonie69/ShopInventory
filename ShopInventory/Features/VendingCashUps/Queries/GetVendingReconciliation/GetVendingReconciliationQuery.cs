using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VendingCashUps.Queries.GetVendingReconciliation;

/// <summary>
/// Every cashier-day from <see cref="FromDate"/> to <see cref="ToDate"/> (both inclusive) that had
/// vending sales or a cash-up, optionally at one warehouse.
/// </summary>
public sealed record GetVendingReconciliationQuery(
    DateTime FromDate,
    DateTime ToDate,
    string? WarehouseCode
) : IRequest<ErrorOr<VendingReconciliationDto>>;
