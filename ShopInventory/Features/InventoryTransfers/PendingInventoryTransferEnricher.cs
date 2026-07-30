using Microsoft.EntityFrameworkCore;
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
    ITransferWarehouseAuthorizer warehouseAuthorizer) : IPendingInventoryTransferEnricher
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
            var (request, stages) = await approvalService.GetProgressAsync(
                ApprovalDocumentContext.ForPendingTransfer(pending), cancellationToken);

            dto.ApprovalRequestId = request.Id;
            dto.ApprovalTemplateName = request.TemplateName;
            dto.ApprovalStatus = request.Status;
            dto.ApprovalStages = stages;
            dto.CanCurrentUserDecide =
                pending.Status == PendingInventoryTransferStatuses.AwaitingApproval &&
                viewer is not null &&
                IsInScope(scope, pending.FromWarehouse) &&
                stages.Any(stage => stage.Status == ApprovalRequestStatuses.Pending && IsAuthorizer(viewer, stage));

            results.Add(dto);
        }

        if (includeLines)
            await PendingTransferItemDescriptions.AttachAsync(context, results, cancellationToken);

        return results;
    }

    private static bool IsInScope(IReadOnlyList<string>? scope, string fromWarehouse)
        => scope is null || scope.Contains(fromWarehouse, StringComparer.OrdinalIgnoreCase);

    private static bool IsAuthorizer(User user, ApprovalStageProgressDto stage)
        => string.Equals(user.Role, ApplicationRoles.Admin, StringComparison.OrdinalIgnoreCase) ||
           stage.AuthorizerUserIds.Contains(user.Id) ||
           stage.AuthorizerRoles.Contains(user.Role, StringComparer.OrdinalIgnoreCase);
}
