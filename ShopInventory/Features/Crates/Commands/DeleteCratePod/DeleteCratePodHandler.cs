using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Crates;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Crates.Commands.DeleteCratePod;

public sealed class DeleteCratePodHandler(
    ApplicationDbContext context,
    IDocumentService documentService,
    IAuditService auditService,
    ILogger<DeleteCratePodHandler> logger
) : IRequestHandler<DeleteCratePodCommand, ErrorOr<bool>>
{
    public async Task<ErrorOr<bool>> Handle(
        DeleteCratePodCommand command,
        CancellationToken cancellationToken)
    {
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

        var submission = await context.CratePodSubmissions
            .AsTracking()
            .Include(s => s.CrateTransaction)
                .ThenInclude(t => t.Grv)
            .FirstOrDefaultAsync(s => s.Id == command.CratePodSubmissionId, cancellationToken);

        if (submission is null)
        {
            return Errors.CrateTracking.SubmissionNotFound(command.CratePodSubmissionId);
        }

        if (submission.CrateTransaction.Grv is not null)
        {
            return Errors.CrateTracking.DeleteBlocked("Crate POD submissions cannot be deleted after a crate GRV has been created for the transaction.");
        }

        if (string.Equals(currentUser.Role, CrateTrackingConstants.SubmissionRoleDriver, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(submission.SubmissionRole, CrateTrackingConstants.SubmissionRoleDriver, StringComparison.OrdinalIgnoreCase))
            {
                return Errors.CrateTracking.AccessDenied("Drivers can only delete driver crate POD submissions.");
            }

            if (submission.SubmittedByUserId != command.UserId.Value)
            {
                return Errors.CrateTracking.AccessDenied("This driver crate POD was uploaded by another driver.");
            }
        }
        else if (string.Equals(currentUser.Role, CrateTrackingConstants.SubmissionRoleMerchandiser, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(submission.SubmissionRole, CrateTrackingConstants.SubmissionRoleMerchandiser, StringComparison.OrdinalIgnoreCase))
            {
                return Errors.CrateTracking.AccessDenied("Merchandisers can only delete merchandiser crate POD submissions.");
            }

            if (submission.SubmittedByUserId != command.UserId.Value)
            {
                return Errors.CrateTracking.AccessDenied("This merchandiser crate POD was uploaded by another user.");
            }
        }
        else if (!string.Equals(currentUser.Role, "Admin", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(currentUser.Role, "Manager", StringComparison.OrdinalIgnoreCase))
        {
            return Errors.CrateTracking.AccessDenied("You do not have permission to delete crate POD submissions.");
        }

        var attachments = await context.DocumentAttachments
            .AsNoTracking()
            .Where(a => a.EntityType == CrateTrackingConstants.AttachmentEntityTypeCratePodSubmission && a.EntityId == submission.Id)
            .Select(a => a.Id)
            .ToListAsync(cancellationToken);

        foreach (var attachmentId in attachments)
        {
            await documentService.DeleteAttachmentAsync(attachmentId, cancellationToken);
        }

        context.CratePodSubmissions.Remove(submission);
        await context.SaveChangesAsync(cancellationToken);

        try
        {
            await auditService.LogAsync(
                AuditActions.DeleteCratePod,
                "CratePodSubmission",
                submission.Id.ToString(),
                $"Deleted {submission.SubmissionRole} crate POD for transaction {submission.CrateTransactionId}",
                true);
        }
        catch
        {
        }

        logger.LogInformation(
            "Deleted crate POD submission {SubmissionId} for transaction {TransactionId}",
            submission.Id,
            submission.CrateTransactionId);

        return true;
    }
}