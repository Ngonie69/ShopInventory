using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Crates;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Crates.Commands.DeleteCrateOpeningBalance;

public sealed class DeleteCrateOpeningBalanceHandler(
    ApplicationDbContext context,
    IDocumentService documentService,
    IAuditService auditService,
    ILogger<DeleteCrateOpeningBalanceHandler> logger
) : IRequestHandler<DeleteCrateOpeningBalanceCommand, ErrorOr<bool>>
{
    public async Task<ErrorOr<bool>> Handle(
        DeleteCrateOpeningBalanceCommand command,
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

        if (!string.Equals(currentUser.Role, "Admin", StringComparison.OrdinalIgnoreCase))
        {
            return Errors.CrateTracking.AccessDenied("Only administrators can delete opening balances.");
        }

        var transaction = await context.CrateTransactions
            .Include(t => t.PodSubmissions)
            .Include(t => t.Grv)
            .FirstOrDefaultAsync(t => t.Id == command.CrateTransactionId, cancellationToken);

        if (transaction is null)
        {
            return Errors.CrateTracking.TransactionNotFound(command.CrateTransactionId);
        }

        if (!string.Equals(transaction.TransactionType, CrateTrackingConstants.TransactionTypeOpeningBalance, StringComparison.OrdinalIgnoreCase))
        {
            return Errors.CrateTracking.InvalidTransactionType("Only opening balance crate transactions can be deleted from this screen.");
        }

        if (transaction.PodSubmissions.Count > 0 || transaction.Grv is not null)
        {
            return Errors.CrateTracking.InvalidTransactionType("Opening balances with downstream crate activity cannot be deleted.");
        }

        var attachments = await context.DocumentAttachments
            .AsNoTracking()
            .Where(a => a.EntityType == CrateTrackingConstants.AttachmentEntityTypeCrateTransaction && a.EntityId == transaction.Id)
            .Select(a => new { a.Id, a.FileName })
            .ToListAsync(cancellationToken);

        foreach (var attachment in attachments)
        {
            await documentService.DeleteAttachmentAsync(attachment.Id, cancellationToken);
        }

        context.CrateTransactions.Remove(transaction);
        await context.SaveChangesAsync(cancellationToken);

        try
        {
            await auditService.LogAsync(
                AuditActions.DeleteCrateOpeningBalance,
                "CrateTransaction",
                transaction.Id.ToString(),
                $"Deleted opening balance #{transaction.Id} for {transaction.ShopCardCode}",
                true);
        }
        catch
        {
        }

        logger.LogInformation(
            "Deleted crate opening balance transaction {TransactionId} for {ShopCardCode}",
            transaction.Id,
            transaction.ShopCardCode);

        return true;
    }
}