using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Features.InventoryTransfers.SapApproval;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.InventoryTransfers.Queries.PreviewTransferApproval;

/// <remarks>
/// The routing itself stays in the approval engine, so the preview cannot drift from what submitting
/// actually does: it asks the same template selection and the same authorizer rule. A factory transfer
/// has no stages there; SAP's own templates decide it, and they are read here through the same rule a
/// submission uses (<see cref="ISapTransferApprovalRoutes"/>, on the header pair, as SAP's templates do).
/// </remarks>
public sealed class PreviewTransferApprovalHandler(
    IInventoryTransferApprovalService approvalService,
    ISapTransferApprovalRoutes sapRoutes)
    : IRequestHandler<PreviewTransferApprovalQuery, ErrorOr<ApprovalRoutePreviewDto>>
{
    public async Task<ErrorOr<ApprovalRoutePreviewDto>> Handle(
        PreviewTransferApprovalQuery query,
        CancellationToken cancellationToken)
    {
        var from = Trimmed(query.FromWarehouses);
        var to = Trimmed(query.ToWarehouses);
        var preview = await approvalService.PreviewRouteAsync(
            ApprovalDocumentContext.ForPreview(ApprovalDocumentTypes.InventoryTransfer, from, to, query.UserId),
            cancellationToken);

        if (!TransferTypes.IsFactory(preview.TransferType))
            return preview;

        var route = await sapRoutes.TryForRouteAsync(
            from.FirstOrDefault(), to.FirstOrDefault(), from.Concat(to), cancellationToken);
        preview.SapApprovalKnown = route is not null;
        preview.SapApprovalTemplates = route is { IsCovered: true }
            ? route.Templates.Select(template => template.Name).ToList()
            : [];
        return preview;
    }

    private static List<string> Trimmed(IReadOnlyList<string> codes)
        => codes.Select(code => code.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
