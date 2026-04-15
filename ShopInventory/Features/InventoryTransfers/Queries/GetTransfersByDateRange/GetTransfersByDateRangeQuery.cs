using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.InventoryTransfers.Queries.GetTransfersByDateRange;

public sealed record GetTransfersByDateRangeQuery(
    string WarehouseCode,
    string FromDate,
    string ToDate,
    int? Page,
    int? PageSize
) : IRequest<ErrorOr<InventoryTransferDateResponseDto>>;
