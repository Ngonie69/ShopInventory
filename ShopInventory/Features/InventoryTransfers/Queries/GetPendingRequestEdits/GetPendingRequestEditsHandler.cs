using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.InventoryTransfers.Queries.GetPendingRequestEdits;

/// <summary>
/// Attaches approval progress to held changes and works out whether the caller may decide.
/// </summary>
public interface IPendingTransferRequestEditEnricher
{
    Task<List<PendingTransferRequestEditDto>> EnrichAsync(
        IReadOnlyList<PendingTransferRequestEditEntity> edits,
        Guid viewerUserId,
        CancellationToken cancellationToken);
}

public sealed class PendingTransferRequestEditEnricher(
    ApplicationDbContext context,
    IInventoryTransferApprovalService approvalService) : IPendingTransferRequestEditEnricher
{
    public async Task<List<PendingTransferRequestEditDto>> EnrichAsync(
        IReadOnlyList<PendingTransferRequestEditEntity> edits,
        Guid viewerUserId,
        CancellationToken cancellationToken)
    {
        var results = new List<PendingTransferRequestEditDto>(edits.Count);
        if (edits.Count == 0)
            return results;

        var viewer = await context.Users.AsNoTracking()
            .FirstOrDefaultAsync(user => user.Id == viewerUserId && user.IsActive, cancellationToken);

        foreach (var edit in edits)
        {
            var dto = TransferRequestEditMapper.ToDto(edit);
            var (request, stages) = await approvalService.GetProgressAsync(
                ApprovalDocumentContext.ForRequestEdit(edit), cancellationToken);

            dto.ApprovalRequestId = request.Id;
            dto.ApprovalTemplateName = request.TemplateName;
            dto.ApprovalStatus = request.Status;
            dto.ApprovalStages = stages;
            dto.CanCurrentUserDecide =
                edit.Status == PendingTransferRequestEditStatuses.AwaitingApproval &&
                viewer is not null &&
                // The proposer is the one who was not allowed to make the change unilaterally.
                edit.CreatedByUserId != viewer.Id &&
                stages.Any(stage => stage.Status == ApprovalRequestStatuses.Pending && IsAuthorizer(viewer, stage));

            results.Add(dto);
        }

        return results;
    }

    private static bool IsAuthorizer(User user, ApprovalStageProgressDto stage)
        => string.Equals(user.Role, ApplicationRoles.Admin, StringComparison.OrdinalIgnoreCase) ||
           stage.AuthorizerUserIds.Contains(user.Id) ||
           stage.AuthorizerRoles.Contains(user.Role, StringComparer.OrdinalIgnoreCase);
}

public sealed class GetPendingRequestEditsHandler(
    ApplicationDbContext context,
    IPendingTransferRequestEditEnricher enricher)
    : IRequestHandler<GetPendingRequestEditsQuery, ErrorOr<PendingTransferRequestEditListResponseDto>>
{
    public async Task<ErrorOr<PendingTransferRequestEditListResponseDto>> Handle(
        GetPendingRequestEditsQuery query,
        CancellationToken cancellationToken)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);

        var records = context.PendingTransferRequestEdits.AsNoTracking().AsQueryable();

        // "all" lifts the default filter so callers can review decided changes too.
        if (!string.Equals(query.Status, "all", StringComparison.OrdinalIgnoreCase))
        {
            var status = string.IsNullOrWhiteSpace(query.Status)
                ? PendingTransferRequestEditStatuses.AwaitingApproval
                : query.Status.Trim();
            records = records.Where(item => item.Status == status);
        }

        if (query.RequestDocEntry.HasValue)
            records = records.Where(item => item.RequestDocEntry == query.RequestDocEntry.Value);

        var totalCount = await records.CountAsync(cancellationToken);
        var pageRecords = await records
            .OrderByDescending(item => item.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PendingTransferRequestEditListResponseDto
        {
            Items = await enricher.EnrichAsync(pageRecords, query.UserId, cancellationToken),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }
}

public sealed class GetPendingRequestEditByIdHandler(
    ApplicationDbContext context,
    IPendingTransferRequestEditEnricher enricher)
    : IRequestHandler<GetPendingRequestEditByIdQuery, ErrorOr<PendingTransferRequestEditDto>>
{
    public async Task<ErrorOr<PendingTransferRequestEditDto>> Handle(
        GetPendingRequestEditByIdQuery query,
        CancellationToken cancellationToken)
    {
        var edit = await context.PendingTransferRequestEdits.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == query.Id, cancellationToken);
        if (edit is null)
            return Errors.InventoryTransfer.PendingEditNotFound(query.Id);

        var enriched = await enricher.EnrichAsync([edit], query.UserId, cancellationToken);
        return enriched[0];
    }
}
