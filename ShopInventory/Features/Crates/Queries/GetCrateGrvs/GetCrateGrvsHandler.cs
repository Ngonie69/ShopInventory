using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Crates;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Crates.Queries.GetCrateGrvs;

public sealed class GetCrateGrvsHandler(
    ApplicationDbContext context
) : IRequestHandler<GetCrateGrvsQuery, ErrorOr<List<CrateGrvDto>>>
{
    public async Task<ErrorOr<List<CrateGrvDto>>> Handle(
        GetCrateGrvsQuery request,
        CancellationToken cancellationToken)
    {
        var currentUser = await context.Users
            .AsNoTracking()
            .Where(u => u.Id == request.UserId)
            .Select(u => new { u.Role, u.IsActive })
            .FirstOrDefaultAsync(cancellationToken);

        if (currentUser is null || !currentUser.IsActive)
        {
            return Errors.Auth.UserNotFound;
        }

        var query = context.CrateGrvs
            .AsNoTracking()
            .Include(g => g.CrateTransaction)
                .ThenInclude(t => t.PodSubmissions)
            .Include(g => g.CreatedByUser)
            .AsQueryable();

        if (string.Equals(currentUser.Role, CrateTrackingConstants.SubmissionRoleDriver, StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(g => g.CrateTransaction.PodSubmissions.Any(s =>
                s.SubmissionRole == CrateTrackingConstants.SubmissionRoleDriver &&
                s.SubmittedByUserId == request.UserId));
        }

        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            var status = request.Status.Trim();
            query = query.Where(g => g.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            var pattern = $"%{term}%";
            query = query.Where(g =>
                (g.GrvNumber != null && EF.Functions.ILike(g.GrvNumber, pattern)) ||
                EF.Functions.ILike(g.CrateTransaction.ShopCardCode, pattern) ||
                (g.CrateTransaction.ShopName != null && EF.Functions.ILike(g.CrateTransaction.ShopName, pattern)) ||
                (g.CrateTransaction.InvoiceDocNum != null && g.CrateTransaction.InvoiceDocNum.ToString()!.Contains(term)));
        }

        var grvs = await query
            .OrderByDescending(g => g.CreatedAt)
            .ToListAsync(cancellationToken);

        var grvIds = grvs.Select(g => g.Id).ToList();
        var attachments = await context.DocumentAttachments
            .AsNoTracking()
            .Include(a => a.UploadedByUser)
            .Where(a => a.EntityType == CrateTrackingConstants.AttachmentEntityTypeCrateGrv && grvIds.Contains(a.EntityId))
            .OrderByDescending(a => a.UploadedAt)
            .ToListAsync(cancellationToken);

        var attachmentsByGrv = attachments
            .GroupBy(a => a.EntityId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(CrateDtoMapping.MapAttachment).ToList());

        return grvs
            .Select(grv => CrateDtoMapping.MapGrv(grv, attachmentsByGrv.GetValueOrDefault(grv.Id) ?? []))
            .ToList();
    }
}