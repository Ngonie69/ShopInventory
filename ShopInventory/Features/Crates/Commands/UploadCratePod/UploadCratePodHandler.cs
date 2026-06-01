using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Crates;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.Crates.Commands.UploadCratePod;

public sealed class UploadCratePodHandler(
    ApplicationDbContext context,
    IDocumentService documentService,
    IAuditService auditService,
    ILogger<UploadCratePodHandler> logger
) : IRequestHandler<UploadCratePodCommand, ErrorOr<CratePodSubmissionDto>>
{
    public async Task<ErrorOr<CratePodSubmissionDto>> Handle(
        UploadCratePodCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Quantity < 0)
        {
            return Errors.CrateTracking.InvalidQuantity("Crate quantity cannot be negative.");
        }

        if (!command.UserId.HasValue)
        {
            return Errors.Auth.Unauthenticated;
        }

        var currentUser = await context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == command.UserId.Value, cancellationToken);

        if (currentUser is null || !currentUser.IsActive)
        {
            return Errors.Auth.UserNotFound;
        }

        var submissionRole = ResolveSubmissionRole(command.SubmissionRole, currentUser.Role);
        if (submissionRole is null)
        {
            return Errors.CrateTracking.InvalidSubmissionRole;
        }

        var transaction = await context.CrateTransactions
            .Include(t => t.PodSubmissions)
            .Include(t => t.Grv)
            .FirstOrDefaultAsync(t => t.Id == command.CrateTransactionId, cancellationToken);

        if (transaction is null)
        {
            return Errors.CrateTracking.TransactionNotFound(command.CrateTransactionId);
        }

        if (!string.Equals(transaction.TransactionType, CrateTrackingConstants.TransactionTypeInvoice, StringComparison.OrdinalIgnoreCase))
        {
            return Errors.CrateTracking.InvalidTransactionType("Crate POD uploads are only allowed for invoice crate transactions.");
        }

        if (string.Equals(currentUser.Role, CrateTrackingConstants.SubmissionRoleDriver, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(submissionRole, CrateTrackingConstants.SubmissionRoleDriver, StringComparison.OrdinalIgnoreCase))
        {
            return Errors.CrateTracking.AccessDenied("Drivers can only upload the driver crate POD.");
        }

        if (string.Equals(currentUser.Role, CrateTrackingConstants.SubmissionRoleMerchandiser, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(submissionRole, CrateTrackingConstants.SubmissionRoleMerchandiser, StringComparison.OrdinalIgnoreCase))
        {
            return Errors.CrateTracking.AccessDenied("Merchandisers can only upload the merchandiser crate POD.");
        }

        var existingSubmission = transaction.PodSubmissions
            .FirstOrDefault(s => string.Equals(s.SubmissionRole, submissionRole, StringComparison.OrdinalIgnoreCase));

        var existingSubmissionHasDocument = existingSubmission is not null && await context.DocumentAttachments
            .AsNoTracking()
            .AnyAsync(
                a => a.EntityType == CrateTrackingConstants.AttachmentEntityTypeCratePodSubmission &&
                     a.EntityId == existingSubmission.Id,
                cancellationToken);

        if (existingSubmission is not null &&
            existingSubmissionHasDocument &&
            string.Equals(currentUser.Role, CrateTrackingConstants.SubmissionRoleDriver, StringComparison.OrdinalIgnoreCase) &&
            existingSubmission.SubmittedByUserId != command.UserId)
        {
            return Errors.CrateTracking.AccessDenied("This driver POD has already been uploaded by another driver.");
        }

        var submission = existingSubmission ?? new CratePodSubmissionEntity
        {
            CrateTransactionId = transaction.Id,
            SubmissionRole = submissionRole,
            SubmittedByUserId = command.UserId.Value,
            SubmittedAt = DateTime.UtcNow
        };

        submission.Quantity = command.Quantity;
        submission.Notes = string.IsNullOrWhiteSpace(command.Notes) ? null : command.Notes.Trim();
        submission.SubmittedAt = DateTime.UtcNow;
        submission.SubmittedByUserId = command.UserId.Value;

        if (existingSubmission is null)
        {
            context.CratePodSubmissions.Add(submission);
        }

        await context.SaveChangesAsync(cancellationToken);

        await documentService.UploadAttachmentAsync(
            new UploadAttachmentRequest
            {
                EntityType = CrateTrackingConstants.AttachmentEntityTypeCratePodSubmission,
                EntityId = submission.Id,
                Description = $"Crate POD - {submissionRole}",
                IncludeInEmail = false
            },
            command.FileStream,
            command.FileName,
            command.ContentType,
            command.UserId,
            cancellationToken);

        try
        {
            await auditService.LogAsync(
                AuditActions.UploadCratePod,
                "CratePodSubmission",
                submission.Id.ToString(),
                $"{submissionRole} crate POD uploaded for transaction {transaction.Id}",
                true);
        }
        catch
        {
        }

        logger.LogInformation(
            "Uploaded {Role} crate POD for transaction {TransactionId}",
            submissionRole,
            transaction.Id);

        var attachments = await context.DocumentAttachments
            .AsNoTracking()
            .Include(a => a.UploadedByUser)
            .Where(a => a.EntityType == CrateTrackingConstants.AttachmentEntityTypeCratePodSubmission && a.EntityId == submission.Id)
            .OrderByDescending(a => a.UploadedAt)
            .ToListAsync(cancellationToken);

        submission.CrateTransaction = transaction;
        submission.SubmittedByUser = currentUser;

        return CrateDtoMapping.MapPodSubmission(
            submission,
            attachments.Select(CrateDtoMapping.MapAttachment).ToList());
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

            return null;
        }

        if (string.Equals(requestedRole, CrateTrackingConstants.SubmissionRoleDriver, StringComparison.OrdinalIgnoreCase))
        {
            return CrateTrackingConstants.SubmissionRoleDriver;
        }

        if (string.Equals(requestedRole, CrateTrackingConstants.SubmissionRoleMerchandiser, StringComparison.OrdinalIgnoreCase))
        {
            return CrateTrackingConstants.SubmissionRoleMerchandiser;
        }

        return null;
    }
}