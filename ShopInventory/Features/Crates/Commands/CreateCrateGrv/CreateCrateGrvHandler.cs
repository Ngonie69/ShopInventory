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

namespace ShopInventory.Features.Crates.Commands.CreateCrateGrv;

public sealed class CreateCrateGrvHandler(
    ApplicationDbContext context,
    IDocumentService documentService,
    IAuditService auditService,
    ILogger<CreateCrateGrvHandler> logger
) : IRequestHandler<CreateCrateGrvCommand, ErrorOr<CrateGrvDto>>
{
    public async Task<ErrorOr<CrateGrvDto>> Handle(
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