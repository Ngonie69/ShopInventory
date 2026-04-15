using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Invoices.Queries.GetAvailableBatches;

public sealed record GetAvailableBatchesQuery(
    string ItemCode,
    string WarehouseCode,
    BatchAllocationStrategy Strategy
) : IRequest<ErrorOr<object>>;
