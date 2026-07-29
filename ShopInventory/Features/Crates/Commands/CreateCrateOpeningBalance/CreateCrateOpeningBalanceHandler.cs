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
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.Crates.Commands.CreateCrateOpeningBalance;

public sealed class CreateCrateOpeningBalanceHandler(
    ApplicationDbContext context,
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> sapSettings,
    IDocumentService documentService,
    IAuditService auditService,
    ILogger<CreateCrateOpeningBalanceHandler> logger
) : IRequestHandler<CreateCrateOpeningBalanceCommand, ErrorOr<CrateTransactionDto>>
{
    public async Task<ErrorOr<CrateTransactionDto>> Handle(
        CreateCrateOpeningBalanceCommand command,
        CancellationToken cancellationToken)
    {
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

        var normalizedShopCardCode = command.ShopCardCode.Trim();

        BusinessPartnerDto? sapBusinessPartner;
        try
        {
            sapBusinessPartner = await sapClient.GetBusinessPartnerByCodeAsync(normalizedShopCardCode, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error resolving SAP business partner {ShopCardCode} for crate opening balance", normalizedShopCardCode);
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

        var createdByUser = command.UserId.HasValue
            ? await context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == command.UserId.Value, cancellationToken)
            : null;

        var transaction = new CrateTransactionEntity
        {
            TransactionType = CrateTrackingConstants.TransactionTypeOpeningBalance,
            ShopCardCode = normalizedShopCardCode,
            ShopName = string.IsNullOrWhiteSpace(sapBusinessPartner.CardName) ? null : sapBusinessPartner.CardName.Trim(),
            ExpectedQuantity = command.Quantity,
            EffectiveDate = DateTime.SpecifyKind(command.EffectiveDate.Date, DateTimeKind.Utc),
            Notes = string.IsNullOrWhiteSpace(command.Notes) ? null : command.Notes.Trim(),
            CreatedByUserId = command.UserId,
            CreatedAt = DateTime.UtcNow
        };

        context.CrateTransactions.Add(transaction);
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
                AuditActions.CreateCrateOpeningBalance,
                "CrateTransaction",
                transaction.Id.ToString(),
                $"Opening balance of {command.Quantity:N2} crates uploaded for {transaction.ShopCardCode}",
                true);
        }
        catch
        {
        }

        logger.LogInformation(
            "Created crate opening balance transaction {TransactionId} for {ShopCardCode}",
            transaction.Id,
            transaction.ShopCardCode);

        transaction.CreatedByUser = createdByUser;

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