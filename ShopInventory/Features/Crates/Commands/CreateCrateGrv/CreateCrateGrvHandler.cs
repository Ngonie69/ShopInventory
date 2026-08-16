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

namespace ShopInventory.Features.Crates.Commands.CreateCrateGrv;

public sealed class CreateCrateGrvHandler(
    ApplicationDbContext context,
    IDocumentService documentService,
    IAuditService auditService,
    IIdempotencyRequestStore idempotencyRequestStore,
    ILogger<CreateCrateGrvHandler> logger
) : IRequestHandler<CreateCrateGrvCommand, ErrorOr<CrateGrvDto>>
{
    private const string IdempotencyScope = "crates.grvs.create";

    /// <summary>
    /// The one-to-one FK on CrateTransactionId already stops a retry creating a second GRV, but it
    /// turns the retry into a "GRV already exists" refusal — so a merchandiser whose first attempt
    /// timed out is told it failed and has no way to reach the GRV that was raised. A durable
    /// idempotency key replays the original GRV instead of refusing.
    /// </summary>
    public async Task<ErrorOr<CrateGrvDto>> Handle(
        CreateCrateGrvCommand command,
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
                var acquireResult = await idempotencyRequestStore.TryAcquireAsync<CrateGrvDto>(
                    IdempotencyScope,
                    clientRequestId,
                    // Hashed instead of the command itself: the command holds the upload Stream,
                    // which cannot be serialized and would differ on every attempt anyway.
                    BuildIdempotencyFingerprint(command),
                    cancellationToken);

                switch (acquireResult.Outcome)
                {
                    case IdempotencyAcquireOutcome.ReplayAvailable when acquireResult.Response is not null:
                        logger.LogWarning("Replaying crate GRV creation for idempotency key {Key}", clientRequestId);
                        return acquireResult.Response;
                    case IdempotencyAcquireOutcome.InProgress:
                        return Errors.Idempotency.RequestInProgress("crate GRV creation");
                    case IdempotencyAcquireOutcome.RequestMismatch:
                        return Errors.Idempotency.RequestMismatch("crate GRV creation");
                    case IdempotencyAcquireOutcome.Acquired:
                        idempotencyRequestId = acquireResult.RequestId;
                        releaseIdempotencyRequest = true;
                        break;
                }
            }

            var result = await HandleCoreAsync(command, cancellationToken);

            // Only a GRV that actually exists may be replayed. A refusal — no variance, missing
            // merchandiser POD — has to stay retryable once the underlying state is fixed.
            if (!result.IsError && idempotencyRequestId.HasValue)
            {
                try
                {
                    await idempotencyRequestStore.CompleteAsync(idempotencyRequestId.Value, result.Value, cancellationToken);
                    releaseIdempotencyRequest = false;
                }
                catch (Exception completeException)
                {
                    logger.LogWarning(completeException, "Failed to persist crate GRV idempotency completion for request {RequestId}", idempotencyRequestId.Value);
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
                    await idempotencyRequestStore.ReleaseAsync(idempotencyRequestId.Value, CancellationToken.None);
                }
                catch (Exception releaseException)
                {
                    logger.LogWarning(releaseException, "Failed to release crate GRV idempotency request {RequestId}", idempotencyRequestId.Value);
                }
            }
        }
    }

    private static object BuildIdempotencyFingerprint(CreateCrateGrvCommand command) => new
    {
        command.CrateTransactionId,
        command.Reason,
        command.FileName,
        command.UserId
    };

    private async Task<ErrorOr<CrateGrvDto>> HandleCoreAsync(
        CreateCrateGrvCommand command,
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

        var transaction = await context.CrateTransactions
            .Include(t => t.PodSubmissions)
            .Include(t => t.Grv)
            .FirstOrDefaultAsync(t => t.Id == command.CrateTransactionId, cancellationToken);

        if (transaction is null)
        {
            return Errors.CrateTracking.TransactionNotFound(command.CrateTransactionId);
        }

        if (transaction.Grv is not null)
        {
            return Errors.CrateTracking.GrvAlreadyExists(transaction.Id);
        }

        var merchandiserSubmission = transaction.PodSubmissions
            .FirstOrDefault(s => string.Equals(s.SubmissionRole, CrateTrackingConstants.SubmissionRoleMerchandiser, StringComparison.OrdinalIgnoreCase));

        if (merchandiserSubmission is null)
        {
            return Errors.CrateTracking.MerchandiserPodRequired;
        }

        if (merchandiserSubmission.Quantity == transaction.ExpectedQuantity)
        {
            return Errors.CrateTracking.NoVarianceForGrv;
        }

        var variance = merchandiserSubmission.Quantity - transaction.ExpectedQuantity;
        var grv = new CrateGrvEntity
        {
            CrateTransactionId = transaction.Id,
            ExpectedQuantity = transaction.ExpectedQuantity,
            ActualQuantity = merchandiserSubmission.Quantity,
            VarianceQuantity = variance,
            Direction = variance > 0 ? "Over" : "Under",
            Reason = command.Reason.Trim(),
            Status = "Open",
            CreatedAt = DateTime.UtcNow,
            CreatedByUserId = command.UserId.Value
        };

        context.CrateGrvs.Add(grv);
        await context.SaveChangesAsync(cancellationToken);

        grv.GrvNumber = $"CR-GRV-{grv.Id:D6}";
        await context.SaveChangesAsync(cancellationToken);

        await documentService.UploadAttachmentAsync(
            new UploadAttachmentRequest
            {
                EntityType = CrateTrackingConstants.AttachmentEntityTypeCrateGrv,
                EntityId = grv.Id,
                Description = $"Crate GRV - {grv.GrvNumber}",
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
                AuditActions.CreateCrateGrv,
                "CrateGrv",
                grv.Id.ToString(),
                $"Crate GRV {grv.GrvNumber} created for transaction {transaction.Id}",
                true);
        }
        catch
        {
        }

        logger.LogInformation(
            "Created crate GRV {GrvNumber} for transaction {TransactionId}",
            grv.GrvNumber,
            transaction.Id);

        var attachments = await context.DocumentAttachments
            .AsNoTracking()
            .Include(a => a.UploadedByUser)
            .Where(a => a.EntityType == CrateTrackingConstants.AttachmentEntityTypeCrateGrv && a.EntityId == grv.Id)
            .OrderByDescending(a => a.UploadedAt)
            .ToListAsync(cancellationToken);

        grv.CrateTransaction = transaction;
        grv.CreatedByUser = currentUser;

        return CrateDtoMapping.MapGrv(
            grv,
            attachments.Select(CrateDtoMapping.MapAttachment).ToList());
    }
}