using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.InventoryTransfers;

/// <summary>
/// Attaches approval progress to held transfers and works out whether the caller may decide on them.
/// </summary>
public interface IPendingInventoryTransferEnricher
{
    Task<List<PendingInventoryTransferDto>> EnrichAsync(
        IReadOnlyList<PendingInventoryTransferEntity> pendingTransfers,
        Guid viewerUserId,
        bool includeLines,
        CancellationToken cancellationToken);
}

public sealed class PendingInventoryTransferEnricher(
    ApplicationDbContext context,
    IInventoryTransferApprovalService approvalService,
    ITransferWarehouseAuthorizer warehouseAuthorizer,
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> sapSettings) : IPendingInventoryTransferEnricher
{
    public async Task<List<PendingInventoryTransferDto>> EnrichAsync(
        IReadOnlyList<PendingInventoryTransferEntity> pendingTransfers,
        Guid viewerUserId,
        bool includeLines,
        CancellationToken cancellationToken)
    {
        var results = new List<PendingInventoryTransferDto>(pendingTransfers.Count);
        if (pendingTransfers.Count == 0)
            return results;

        var viewer = await context.Users.AsNoTracking()
            .FirstOrDefaultAsync(user => user.Id == viewerUserId && user.IsActive, cancellationToken);
        var scope = await warehouseAuthorizer.GetSourceScopeAsync(viewerUserId, cancellationToken);

        foreach (var pending in pendingTransfers)
        {
            var dto = PendingInventoryTransferMapper.ToDto(pending, includeLines);
            var document = ApprovalDocumentContext.ForPendingTransfer(pending);
            var (request, stages) = await approvalService.GetProgressAsync(document, cancellationToken);

            dto.ApprovalRequestId = request.Id;
            dto.ApprovalTemplateName = request.TemplateName;
            dto.ApprovalStatus = request.Status;
            dto.ApprovalStages = stages;
            dto.CanCurrentUserDecide =
                pending.Status == PendingInventoryTransferStatuses.AwaitingApproval &&
                viewer is not null &&
                // Whoever raised the transfer, or changed it after approval, withdraws it instead.
                InventoryTransferApprovalService.MayApprove(request, viewer) &&
                // Mirrors DecidePendingTransferHandler: either end, the stage decides which.
                IsInScope(scope, document) &&
                stages.Any(stage => stage.Status == ApprovalRequestStatuses.Pending &&
                                    InventoryTransferApprovalService.CanAdvance(viewer, stage));

            results.Add(dto);
        }

        // Only the detail path pays for the item-name lookup: the approval queue is served without
        // lines, so it stays a single query with no SAP call behind it.
        if (includeLines)
            await PendingTransferItemDescriptions.AttachAsync(
                sapClient, sapSettings.Value, results, cancellationToken);

        return results;
    }

    private static bool IsInScope(IReadOnlyList<string>? scope, ApprovalDocumentContext document)
        => scope is null || document.SourceWarehouses.Concat(document.DestinationWarehouses)
            .Any(code => scope.Contains(code, StringComparer.OrdinalIgnoreCase));
}
