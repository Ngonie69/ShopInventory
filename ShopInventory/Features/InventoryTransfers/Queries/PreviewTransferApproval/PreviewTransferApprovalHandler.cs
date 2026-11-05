using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.InventoryTransfers.Queries.PreviewTransferApproval;

/// <remarks>
/// The routing itself stays in the approval engine, so the preview cannot drift from what submitting
/// actually does: it asks the same template selection and the same authorizer rule.
/// </remarks>
public sealed class PreviewTransferApprovalHandler(IInventoryTransferApprovalService approvalService)
    : IRequestHandler<PreviewTransferApprovalQuery, ErrorOr<ApprovalRoutePreviewDto>>
{
    public async Task<ErrorOr<ApprovalRoutePreviewDto>> Handle(
        PreviewTransferApprovalQuery query,
        CancellationToken cancellationToken)
        => await approvalService.PreviewRouteAsync(
            ApprovalDocumentContext.ForPreview(
                ApprovalDocumentTypes.InventoryTransfer,
                Trimmed(query.FromWarehouses),
                Trimmed(query.ToWarehouses),
                query.UserId),
            cancellationToken);

    private static List<string> Trimmed(IReadOnlyList<string> codes)
        => codes.Select(code => code.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
