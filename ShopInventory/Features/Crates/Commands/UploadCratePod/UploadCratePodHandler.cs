using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Crates;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Idempotency;
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
    IIdempotencyRequestStore idempotencyRequestStore,
    ILogger<UploadCratePodHandler> logger
) : IRequestHandler<UploadCratePodCommand, ErrorOr<CratePodSubmissionDto>>
{
    private const string IdempotencyScope = "crates.pods.upload";

    /// <summary>
    /// The unique index on (CrateTransactionId, SubmissionRole) already stops a retry creating a
    /// second submission — the row is reused. What it does not stop is the attachment upload below
    /// running again, which files the same photo against the POD once per retry and inflates the
    /// evidence trail. A durable idempotency key replays the original result instead.
    /// </summary>
    public async Task<ErrorOr<CratePodSubmissionDto>> Handle(
        UploadCratePodCommand command,
        CancellationToken cancellationToken)
    {
        var clientRequestId = string.IsNullOrWhiteSpace(command.ClientRequestId)
            ? null
            : command.ClientRequestId.Trim();

        long? idempotencyRequestId = null;
        var releaseIdempotencyRequest = false;

        try
        {
            if (clientRequestId is not null)
            {
                var acquireResult = await idempotencyRequestStore.TryAcquireAsync<CratePodSubmissionDto>(
                    IdempotencyScope,
                    clientRequestId,
                    // Hashed instead of the command itself: the command holds the upload Stream,
                    // which cannot be serialized and would differ on every attempt anyway.
                    BuildIdempotencyFingerprint(command),
                    cancellationToken);

                switch (acquireResult.Outcome)
                {
                    case IdempotencyAcquireOutcome.ReplayAvailable when acquireResult.Response is not null:
                        logger.LogWarning("Replaying crate POD upload for idempotency key {Key}", clientRequestId);
                        return acquireResult.Response;
                    case IdempotencyAcquireOutcome.InProgress:
                        return Errors.Idempotency.RequestInProgress("crate POD upload");
                    case IdempotencyAcquireOutcome.RequestMismatch:
                        return Errors.Idempotency.RequestMismatch("crate POD upload");
                    case IdempotencyAcquireOutcome.Acquired:
                        idempotencyRequestId = acquireResult.RequestId;
                        releaseIdempotencyRequest = true;
                        break;
                }
            }

            var result = await HandleCoreAsync(command, cancellationToken);

            // Only a POD that actually exists may be replayed. A validation refusal has to stay
            // retryable, or correcting the quantity and resubmitting would return the refusal.
            if (!result.IsError && idempotencyRequestId.HasValue)
            {
                try
                {
                    await idempotencyRequestStore.CompleteAsync(idempotencyRequestId.Value, result.Value, cancellationToken);
                    releaseIdempotencyRequest = false;
                }
                catch (Exception completeException)
                {
                    logger.LogWarning(completeException, "Failed to persist crate POD idempotency completion for request {RequestId}", idempotencyRequestId.Value);
                }
            }

            return result;
        }
        finally
        {
            if (releaseIdempotencyRequest && idempotencyRequestId.HasValue)
            {
                try
                {
                    await idempotencyRequestStore.ReleaseAsync(idempotencyRequestId.Value, cancellationToken);
                }
                catch (Exception releaseException)
                {
                    logger.LogWarning(releaseException, "Failed to release crate POD idempotency request {RequestId}", idempotencyRequestId.Value);
                }
            }
        }
    }

    private static object BuildIdempotencyFingerprint(UploadCratePodCommand command) => new
    {
        command.CrateTransactionId,
        command.SubmissionRole,
        command.Quantity,
        command.Notes,
        command.FileName,
        command.UserId
    };

    private async Task<ErrorOr<CratePodSubmissionDto>> HandleCoreAsync(
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

        if (string.Equals(currentUser.Role, "Operator", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(currentUser.AssignedSection))
            {
                return Errors.CrateTracking.AccessDenied("Operators must have an assigned POD section to upload crate PODs.");
            }

            if (transaction.InvoiceDocEntry is not int invoiceDocEntry)
            {
                return Errors.CrateTracking.AccessDenied("This crate transaction is not linked to an invoice in your assigned POD section.");
            }

            var scopedDocEntries = await documentService.GetScopedPodInvoiceDocEntriesAsync(
                [invoiceDocEntry],
                currentUser.AssignedSection,
                cancellationToken);

            if (!scopedDocEntries.Contains(invoiceDocEntry))
            {
                return Errors.CrateTracking.AccessDenied("This crate transaction is outside your assigned POD section.");
            }
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

            if (string.Equals(currentRole, "Admin", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(currentRole, "Manager", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(currentRole, "PodOperator", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(currentRole, "Operator", StringComparison.OrdinalIgnoreCase))
            {
                return CrateTrackingConstants.SubmissionRoleDriver;
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