using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.InventoryTransfers.Queries.PreviewTransferApproval;

/// <summary>
/// Who a direct transfer with these warehouses would wait on, asked before it is submitted.
/// </summary>
/// <param name="FromWarehouses">Every warehouse the transfer's lines draw from.</param>
/// <param name="ToWarehouses">Every warehouse the transfer's lines deliver to.</param>
/// <param name="UserId">Who is raising it; they pick the template and cannot approve it themselves.</param>
public sealed record PreviewTransferApprovalQuery(
    IReadOnlyList<string> FromWarehouses,
    IReadOnlyList<string> ToWarehouses,
    Guid UserId) : IRequest<ErrorOr<ApprovalRoutePreviewDto>>;
