using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Crates;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Crates.Queries.GetCratePods;

public sealed class GetCratePodsHandler(
    ApplicationDbContext context,
    IDocumentService documentService
) : IRequestHandler<GetCratePodsQuery, ErrorOr<List<CratePodSubmissionDto>>>
{
    public async Task<ErrorOr<List<CratePodSubmissionDto>>> Handle(
        GetCratePodsQuery request,
        CancellationToken cancellationToken)
    {
        var currentUser = await context.Users
            .AsNoTracking()
            .Where(u => u.Id == request.UserId)
            .Select(u => new { u.Role, u.AssignedSection, u.IsActive })
            .FirstOrDefaultAsync(cancellationToken);

        if (currentUser is null || !currentUser.IsActive)
        {
            return Errors.Auth.UserNotFound;
        }

        var activePodSubmissionIds = context.DocumentAttachments
            .AsNoTracking()
            .Where(a => a.EntityType == CrateTrackingConstants.AttachmentEntityTypeCratePodSubmission)
            .Select(a => a.EntityId)
            .Distinct();

        var query = context.CratePodSubmissions
            .AsNoTracking()
            .Where(s => activePodSubmissionIds.Contains(s.Id))
            .Include(s => s.CrateTransaction)
            .Include(s => s.SubmittedByUser)
            .AsQueryable();

        if (string.Equals(currentUser.Role, CrateTrackingConstants.SubmissionRoleDriver, StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(s => s.SubmittedByUserId == request.UserId);
        }

        if (!string.IsNullOrWhiteSpace(request.SubmissionRole))
        {
            var role = request.SubmissionRole.Trim();
            query = query.Where(s => s.SubmissionRole == role);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            var pattern = $"%{term}%";
            query = query.Where(s =>
                EF.Functions.ILike(s.CrateTransaction.ShopCardCode, pattern) ||
                (s.CrateTransaction.ShopName != null && EF.Functions.ILike(s.CrateTransaction.ShopName, pattern)) ||
                (s.CrateTransaction.InvoiceDocNum != null && s.CrateTransaction.InvoiceDocNum.ToString()!.Contains(term)) ||
                (s.SubmittedByUser != null && s.SubmittedByUser.Username != null && EF.Functions.ILike(s.SubmittedByUser.Username, pattern)));
        }

        var isScopedPodViewer = string.Equals(currentUser.Role, "Operator", StringComparison.OrdinalIgnoreCase);

        if (isScopedPodViewer)
        {
            if (string.IsNullOrWhiteSpace(currentUser.AssignedSection))
            {
                return new List<CratePodSubmissionDto>();
            }

            var candidateInvoiceDocEntries = await query
                .Where(s => s.CrateTransaction.InvoiceDocEntry.HasValue)
                .Select(s => s.CrateTransaction.InvoiceDocEntry!.Value)
                .Distinct()
                .ToListAsync(cancellationToken);

            if (candidateInvoiceDocEntries.Count == 0)
            {
                return new List<CratePodSubmissionDto>();
            }

            var scopedDocEntries = await documentService.GetScopedPodInvoiceDocEntriesAsync(
                candidateInvoiceDocEntries,
                currentUser.AssignedSection,
                cancellationToken);

            if (scopedDocEntries.Count == 0)
            {
                return new List<CratePodSubmissionDto>();
            }

            query = query.Where(s =>
                s.CrateTransaction.InvoiceDocEntry.HasValue &&
                scopedDocEntries.Contains(s.CrateTransaction.InvoiceDocEntry.Value));
        }

        var submissions = await query
            .OrderByDescending(s => s.SubmittedAt)
            .ToListAsync(cancellationToken);

        var submissionIds = submissions.Select(s => s.Id).ToList();
        var attachments = await context.DocumentAttachments
            .AsNoTracking()
            .Include(a => a.UploadedByUser)
            .Where(a => a.EntityType == CrateTrackingConstants.AttachmentEntityTypeCratePodSubmission && submissionIds.Contains(a.EntityId))
            .OrderByDescending(a => a.UploadedAt)
            .ToListAsync(cancellationToken);

        var attachmentsBySubmission = attachments
            .GroupBy(a => a.EntityId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(CrateDtoMapping.MapAttachment).ToList());

        return submissions
            .Select(submission => CrateDtoMapping.MapPodSubmission(
                submission,
                attachmentsBySubmission.GetValueOrDefault(submission.Id) ?? []))
            .ToList();
    }
}