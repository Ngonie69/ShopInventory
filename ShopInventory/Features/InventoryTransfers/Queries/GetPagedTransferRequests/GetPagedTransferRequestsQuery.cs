using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.InventoryTransfers.Queries.GetPagedTransferRequests;

/// <summary>
/// <paramref name="Status"/> filters on the SAP document status: "open", "closed", or "all"
/// (null and empty mean all). The SAP literals bost_Open and bost_Close are accepted too.
/// <paramref name="FromWarehouse"/> and <paramref name="ToWarehouse"/> keep only requests whose
/// header names that warehouse at that end, so a warehouse's requests are found in SAP rather
/// than only among the newest page.
/// </summary>
public sealed record GetPagedTransferRequestsQuery(
    int Page,
    int PageSize,
    string? Status = null,
    string? FromWarehouse = null,
    string? ToWarehouse = null)
    : IRequest<ErrorOr<TransferRequestListResponseDto>>;
