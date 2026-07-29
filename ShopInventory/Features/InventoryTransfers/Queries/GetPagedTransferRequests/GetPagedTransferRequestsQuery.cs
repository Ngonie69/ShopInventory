using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.InventoryTransfers.Queries.GetPagedTransferRequests;

/// <summary>
/// <paramref name="Status"/> filters on the SAP document status: "open", "closed", or "all"
/// (null and empty mean all). The SAP literals bost_Open and bost_Close are accepted too.
/// </summary>
public sealed record GetPagedTransferRequestsQuery(int Page, int PageSize, string? Status = null)
    : IRequest<ErrorOr<TransferRequestListResponseDto>>;
