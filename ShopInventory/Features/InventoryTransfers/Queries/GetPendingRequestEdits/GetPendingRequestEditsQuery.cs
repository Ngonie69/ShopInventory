using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.InventoryTransfers.Queries.GetPendingRequestEdits;

public sealed record GetPendingRequestEditsQuery(
    Guid UserId,
    string? Status = null,
    int? RequestDocEntry = null,
    int Page = 1,
    int PageSize = 20) : IRequest<ErrorOr<PendingTransferRequestEditListResponseDto>>;

public sealed record GetPendingRequestEditByIdQuery(
    Guid Id,
    Guid UserId) : IRequest<ErrorOr<PendingTransferRequestEditDto>>;
