using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Crates;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Crates.Commands.UpdateCrateOpeningBalance;

public sealed class UpdateCrateOpeningBalanceHandler(
    ApplicationDbContext context,
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> sapSettings,
    IDocumentService documentService,
    IAuditService auditService,
    ILogger<UpdateCrateOpeningBalanceHandler> logger
) : IRequestHandler<UpdateCrateOpeningBalanceCommand, ErrorOr<CrateTransactionDto>>
{
    public async Task<ErrorOr<CrateTransactionDto>> Handle(
        UpdateCrateOpeningBalanceCommand command,
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
            return Errors.CrateTracking.AccessDenied("Only administrators can edit opening balances.");
        }

        if (command.Quantity <= 0)
        {
            return Errors.CrateTracking.InvalidQuantity("Opening balance quantity must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(command.ShopCardCode))
        {
            return Errors.Invoice.CustomerCodeRequired;
        }

        if (!sapSettings.Value.Enabled)
        {
            return Errors.BusinessPartner.SapDisabled;
        }

        var transaction = await context.CrateTransactions
            .Include(t => t.CreatedByUser)
            .FirstOrDefaultAsync(t => t.Id == command.CrateTransactionId, cancellationToken);

        if (transaction is null)
        {
            return Errors.CrateTracking.TransactionNotFound(command.CrateTransactionId);
        }

        if (!string.Equals(transaction.TransactionType, CrateTrackingConstants.TransactionTypeOpeningBalance, StringComparison.OrdinalIgnoreCase))
        {
            return Errors.CrateTracking.InvalidTransactionType("Only opening balance crate transactions can be edited from this screen.");
        }

        var normalizedShopCardCode = command.ShopCardCode.Trim();

        BusinessPartnerDto? sapBusinessPartner;
        try
        {
            sapBusinessPartner = await sapClient.GetBusinessPartnerByCodeAsync(normalizedShopCardCode, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error resolving SAP business partner {ShopCardCode} for crate opening balance update", normalizedShopCardCode);
            return Errors.BusinessPartner.SapError(ex.Message);
        }

        if (sapBusinessPartner is null)
        {
            return Errors.BusinessPartner.NotFound(normalizedShopCardCode);
        }

        if (!string.IsNullOrWhiteSpace(sapBusinessPartner.CardType) &&
            !string.Equals(sapBusinessPartner.CardType, "cCustomer", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(sapBusinessPartner.CardType, "C", StringComparison.OrdinalIgnoreCase))
        {
            return Errors.CrateTracking.InvalidShop("Opening balances must use a customer business partner from SAP.");
        }

        transaction.ShopCardCode = normalizedShopCardCode;
        transaction.ShopName = string.IsNullOrWhiteSpace(sapBusinessPartner.CardName) ? null : sapBusinessPartner.CardName.Trim();
        transaction.ExpectedQuantity = command.Quantity;
        transaction.EffectiveDate = DateTime.SpecifyKind(command.EffectiveDate.Date, DateTimeKind.Utc);
        transaction.Notes = string.IsNullOrWhiteSpace(command.Notes) ? null : command.Notes.Trim();
        transaction.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync(cancellationToken);

        if (command.FileStream is not null &&
            !string.IsNullOrWhiteSpace(command.FileName) &&
            !string.IsNullOrWhiteSpace(command.ContentType))
        {
            await documentService.UploadAttachmentAsync(
                new UploadAttachmentRequest
                {
                    EntityType = CrateTrackingConstants.AttachmentEntityTypeCrateTransaction,
                    EntityId = transaction.Id,
                    Description = "Crate opening balance supporting document",
                    IncludeInEmail = false
                },
                command.FileStream,
                command.FileName,
                command.ContentType,
                command.UserId,
                cancellationToken);
        }

        try
        {
            await auditService.LogAsync(
                AuditActions.UpdateCrateOpeningBalance,
                "CrateTransaction",
                transaction.Id.ToString(),
                $"Updated opening balance #{transaction.Id} for {transaction.ShopCardCode}",
                true);
        }
        catch
        {
        }

        logger.LogInformation(
            "Updated crate opening balance transaction {TransactionId} for {ShopCardCode}",
            transaction.Id,
            transaction.ShopCardCode);

        var supportingDocumentCount = await context.DocumentAttachments
            .AsNoTracking()
            .CountAsync(
                a => a.EntityType == CrateTrackingConstants.AttachmentEntityTypeCrateTransaction && a.EntityId == transaction.Id,
                cancellationToken);

        return CrateDtoMapping.MapTransaction(
            transaction,
            null,
            null,
            supportingDocumentCount,
            0,
            0);
    }
}