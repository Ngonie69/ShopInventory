using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Crates;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Common.Errors;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.Crates.Queries.GetCrateTransactions;

public sealed class GetCrateTransactionsHandler(
    ApplicationDbContext context,
    IDocumentService documentService
) : IRequestHandler<GetCrateTransactionsQuery, ErrorOr<List<CrateTransactionDto>>>
{
    public async Task<ErrorOr<List<CrateTransactionDto>>> Handle(
        GetCrateTransactionsQuery request,
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

        var query = context.CrateTransactions
            .AsNoTracking()
            .AsQueryable();

        if (string.Equals(currentUser.Role, CrateTrackingConstants.SubmissionRoleDriver, StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(t =>
                EF.Functions.ILike(t.TransactionType, CrateTrackingConstants.TransactionTypeInvoice) &&
                (!context.CratePodSubmissions.Any(s =>
                    s.CrateTransactionId == t.Id &&
                    activePodSubmissionIds.Contains(s.Id) &&
                    s.SubmissionRole == CrateTrackingConstants.SubmissionRoleDriver) ||
                 context.CratePodSubmissions.Any(s =>
                    s.CrateTransactionId == t.Id &&
                    activePodSubmissionIds.Contains(s.Id) &&
                    s.SubmissionRole == CrateTrackingConstants.SubmissionRoleDriver &&
                    s.SubmittedByUserId == request.UserId)));
        }

        if (!string.IsNullOrWhiteSpace(request.TransactionType))
        {
            var transactionType = request.TransactionType.Trim();
            query = query.Where(t => t.TransactionType == transactionType);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            var pattern = $"%{term}%";
            query = query.Where(t =>
                EF.Functions.ILike(t.ShopCardCode, pattern) ||
                (t.ShopName != null && EF.Functions.ILike(t.ShopName, pattern)) ||
                (t.InvoiceDocNum != null && t.InvoiceDocNum.ToString()!.Contains(term)));
        }

        var isScopedPodViewer = string.Equals(currentUser.Role, "Operator", StringComparison.OrdinalIgnoreCase);

        if (isScopedPodViewer)
        {
            if (string.IsNullOrWhiteSpace(currentUser.AssignedSection))
            {
                return new List<CrateTransactionDto>();
            }

            var candidateInvoiceDocEntries = await query
                .Where(t =>
                    t.InvoiceDocEntry.HasValue &&
                    EF.Functions.ILike(t.TransactionType, CrateTrackingConstants.TransactionTypeInvoice))
                .Select(t => t.InvoiceDocEntry!.Value)
                .Distinct()
                .ToListAsync(cancellationToken);

            if (candidateInvoiceDocEntries.Count == 0)
            {
                return new List<CrateTransactionDto>();
            }

            var scopedDocEntries = await documentService.GetScopedPodInvoiceDocEntriesAsync(
                candidateInvoiceDocEntries,
                currentUser.AssignedSection,
                cancellationToken);

            if (scopedDocEntries.Count == 0)
            {
                return new List<CrateTransactionDto>();
            }

            query = query.Where(t =>
                t.InvoiceDocEntry.HasValue &&
                EF.Functions.ILike(t.TransactionType, CrateTrackingConstants.TransactionTypeInvoice) &&
                scopedDocEntries.Contains(t.InvoiceDocEntry.Value));
        }

        query = query
            .Include(t => t.CreatedByUser)
            .Include(t => t.Grv);

        var transactions = await query
            .OrderByDescending(t => t.EffectiveDate)
            .ThenByDescending(t => t.CreatedAt)
            .ToListAsync(cancellationToken);

        var transactionIds = transactions.Select(t => t.Id).ToList();
        var activePodSubmissions = await context.CratePodSubmissions
            .AsNoTracking()
            .Include(s => s.SubmittedByUser)
            .Where(s => transactionIds.Contains(s.CrateTransactionId) && activePodSubmissionIds.Contains(s.Id))
            .ToListAsync(cancellationToken);

        var activePodSubmissionsByTransactionId = activePodSubmissions
            .GroupBy(s => s.CrateTransactionId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var podSubmissionIds = activePodSubmissions.Select(s => s.Id).ToList();

        var transactionAttachmentCounts = await context.DocumentAttachments
            .AsNoTracking()
            .Where(a => a.EntityType == CrateTrackingConstants.AttachmentEntityTypeCrateTransaction && transactionIds.Contains(a.EntityId))
            .GroupBy(a => a.EntityId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, cancellationToken);

        var podAttachmentCounts = await context.DocumentAttachments
            .AsNoTracking()
            .Where(a => a.EntityType == CrateTrackingConstants.AttachmentEntityTypeCratePodSubmission && podSubmissionIds.Contains(a.EntityId))
            .GroupBy(a => a.EntityId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, cancellationToken);

        var items = transactions
            .Select(transaction =>
            {
                List<CratePodSubmissionEntity> submissionsForTransaction = activePodSubmissionsByTransactionId.GetValueOrDefault(transaction.Id) ?? [];

                var driverSubmission = submissionsForTransaction
                    .FirstOrDefault(s => string.Equals(s.SubmissionRole, CrateTrackingConstants.SubmissionRoleDriver, StringComparison.OrdinalIgnoreCase));
                var merchandiserSubmission = submissionsForTransaction
                    .FirstOrDefault(s => string.Equals(s.SubmissionRole, CrateTrackingConstants.SubmissionRoleMerchandiser, StringComparison.OrdinalIgnoreCase));

                return CrateDtoMapping.MapTransaction(
                    transaction,
                    driverSubmission,
                    merchandiserSubmission,
                    transactionAttachmentCounts.GetValueOrDefault(transaction.Id),
                    driverSubmission is null ? 0 : podAttachmentCounts.GetValueOrDefault(driverSubmission.Id),
                    merchandiserSubmission is null ? 0 : podAttachmentCounts.GetValueOrDefault(merchandiserSubmission.Id));
            })
            .ToList();

        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            items = items
                .Where(item => string.Equals(item.Status, request.Status.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return items;
    }
}