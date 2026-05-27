using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Crates;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.Crates.Queries.ValidateBulkCratePods;

public sealed class ValidateBulkCratePodsHandler(
    ApplicationDbContext context
) : IRequestHandler<ValidateBulkCratePodsQuery, ErrorOr<BulkCratePodValidationResponseDto>>
{
    public async Task<ErrorOr<BulkCratePodValidationResponseDto>> Handle(
        ValidateBulkCratePodsQuery request,
        CancellationToken cancellationToken)
    {
        var currentUser = await context.Users
            .AsNoTracking()
            .Where(user => user.Id == request.UserId)
            .Select(user => new { user.Role, user.IsActive })
            .FirstOrDefaultAsync(cancellationToken);

        if (currentUser is null || !currentUser.IsActive)
        {
            return Errors.Auth.UserNotFound;
        }

        var submissionRole = ResolveSubmissionRole(request.SubmissionRole, currentUser.Role);
        if (submissionRole is null)
        {
            return Errors.CrateTracking.InvalidSubmissionRole;
        }

        var requestedDocNums = request.InvoiceDocNums
            .Where(invoiceDocNum => invoiceDocNum > 0)
            .Distinct()
            .ToList();

        if (requestedDocNums.Count == 0)
        {
            return new BulkCratePodValidationResponseDto();
        }

        var transactions = await context.CrateTransactions
            .AsNoTracking()
            .Include(transaction => transaction.PodSubmissions)
                .ThenInclude(submission => submission.SubmittedByUser)
            .Include(transaction => transaction.Grv)
            .Where(transaction =>
                string.Equals(transaction.TransactionType, CrateTrackingConstants.TransactionTypeInvoice, StringComparison.OrdinalIgnoreCase) &&
                transaction.InvoiceDocNum.HasValue &&
                requestedDocNums.Contains(transaction.InvoiceDocNum.Value))
            .OrderByDescending(transaction => transaction.EffectiveDate)
            .ThenByDescending(transaction => transaction.CreatedAt)
            .ToListAsync(cancellationToken);

        var latestTransactionsByDocNum = transactions
            .GroupBy(transaction => transaction.InvoiceDocNum!.Value)
            .ToDictionary(group => group.Key, group => group.First());

        var selectedRoleSubmissionIds = latestTransactionsByDocNum.Values
            .Select(transaction => GetSubmissionForRole(transaction, submissionRole)?.Id)
            .OfType<int>()
            .Distinct()
            .ToList();

        var attachmentCountsBySubmissionId = await context.DocumentAttachments
            .AsNoTracking()
            .Where(attachment =>
                attachment.EntityType == CrateTrackingConstants.AttachmentEntityTypeCratePodSubmission &&
                selectedRoleSubmissionIds.Contains(attachment.EntityId))
            .GroupBy(attachment => attachment.EntityId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, cancellationToken);

        var results = requestedDocNums
            .Select(invoiceDocNum => BuildResult(
                invoiceDocNum,
                latestTransactionsByDocNum.GetValueOrDefault(invoiceDocNum),
                currentUser.Role,
                request.UserId,
                submissionRole,
                attachmentCountsBySubmissionId))
            .ToList();

        return new BulkCratePodValidationResponseDto
        {
            Results = results
        };
    }

    private static BulkCratePodValidationResultDto BuildResult(
        int invoiceDocNum,
        CrateTransactionEntity? transaction,
        string currentUserRole,
        Guid currentUserId,
        string submissionRole,
        IReadOnlyDictionary<int, int> attachmentCountsBySubmissionId)
    {
        if (transaction is null)
        {
            return new BulkCratePodValidationResultDto
            {
                InvoiceDocNum = invoiceDocNum,
                Found = false,
                CanUpload = false,
                ErrorMessage = $"Invoice #{invoiceDocNum} has no crate transaction to upload against."
            };
        }

        var driverSubmission = GetSubmissionForRole(transaction, CrateTrackingConstants.SubmissionRoleDriver);
        var merchandiserSubmission = GetSubmissionForRole(transaction, CrateTrackingConstants.SubmissionRoleMerchandiser);
        var selectedRoleSubmission = GetSubmissionForRole(transaction, submissionRole);
        var canUpload = CanUpload(currentUserRole, submissionRole, selectedRoleSubmission, currentUserId);

        return new BulkCratePodValidationResultDto
        {
            InvoiceDocNum = invoiceDocNum,
            CrateTransactionId = transaction.Id,
            ShopCardCode = transaction.ShopCardCode,
            ShopName = transaction.ShopName,
            ExpectedQuantity = transaction.ExpectedQuantity,
            ExistingQuantity = selectedRoleSubmission?.Quantity,
            ExistingAttachmentCount = selectedRoleSubmission is null
                ? 0
                : attachmentCountsBySubmissionId.GetValueOrDefault(selectedRoleSubmission.Id),
            HasExistingSubmission = selectedRoleSubmission is not null,
            Status = CrateDtoMapping.DetermineStatus(transaction, driverSubmission, merchandiserSubmission),
            Found = true,
            CanUpload = canUpload,
            ErrorMessage = canUpload
                ? null
                : BuildAccessDeniedMessage(currentUserRole, submissionRole, selectedRoleSubmission)
        };
    }

    private static CratePodSubmissionEntity? GetSubmissionForRole(CrateTransactionEntity transaction, string submissionRole)
    {
        return transaction.PodSubmissions.FirstOrDefault(submission =>
            string.Equals(submission.SubmissionRole, submissionRole, StringComparison.OrdinalIgnoreCase));
    }

    private static bool CanUpload(
        string currentUserRole,
        string submissionRole,
        CratePodSubmissionEntity? existingSubmission,
        Guid currentUserId)
    {
        if (string.Equals(currentUserRole, "Admin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(currentUserRole, "Manager", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(currentUserRole, CrateTrackingConstants.SubmissionRoleMerchandiser, StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(submissionRole, CrateTrackingConstants.SubmissionRoleMerchandiser, StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(currentUserRole, CrateTrackingConstants.SubmissionRoleDriver, StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(submissionRole, CrateTrackingConstants.SubmissionRoleDriver, StringComparison.OrdinalIgnoreCase) &&
                   (existingSubmission is null || existingSubmission.SubmittedByUserId == currentUserId);
        }

        return false;
    }

    private static string? BuildAccessDeniedMessage(
        string currentUserRole,
        string submissionRole,
        CratePodSubmissionEntity? existingSubmission)
    {
        if (string.Equals(currentUserRole, CrateTrackingConstants.SubmissionRoleDriver, StringComparison.OrdinalIgnoreCase) &&
            existingSubmission is not null &&
            string.Equals(submissionRole, CrateTrackingConstants.SubmissionRoleDriver, StringComparison.OrdinalIgnoreCase))
        {
            return "The driver crate POD for this invoice was already uploaded by another driver.";
        }

        if (string.Equals(currentUserRole, CrateTrackingConstants.SubmissionRoleDriver, StringComparison.OrdinalIgnoreCase))
        {
            return "Drivers can only bulk upload driver crate PODs.";
        }

        if (string.Equals(currentUserRole, CrateTrackingConstants.SubmissionRoleMerchandiser, StringComparison.OrdinalIgnoreCase))
        {
            return "Merchandisers can only bulk upload merchandiser crate PODs.";
        }

        return "You do not have permission to upload this crate POD.";
    }

    private static string? ResolveSubmissionRole(string? requestedRole, string currentRole)
    {
        if (string.IsNullOrWhiteSpace(requestedRole))
        {
            if (string.Equals(currentRole, CrateTrackingConstants.SubmissionRoleDriver, StringComparison.OrdinalIgnoreCase))
            {
                return CrateTrackingConstants.SubmissionRoleDriver;
            }

            if (string.Equals(currentRole, CrateTrackingConstants.SubmissionRoleMerchandiser, StringComparison.OrdinalIgnoreCase))
            {
                return CrateTrackingConstants.SubmissionRoleMerchandiser;
            }

            if (string.Equals(currentRole, "Admin", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(currentRole, "Manager", StringComparison.OrdinalIgnoreCase))
            {
                return CrateTrackingConstants.SubmissionRoleDriver;
            }

            return null;
        }

        if (string.Equals(requestedRole, CrateTrackingConstants.SubmissionRoleDriver, StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(currentRole, CrateTrackingConstants.SubmissionRoleMerchandiser, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return CrateTrackingConstants.SubmissionRoleDriver;
        }

        if (string.Equals(requestedRole, CrateTrackingConstants.SubmissionRoleMerchandiser, StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(currentRole, CrateTrackingConstants.SubmissionRoleDriver, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return CrateTrackingConstants.SubmissionRoleMerchandiser;
        }

        return null;
    }
}