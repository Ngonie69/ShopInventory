using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Crates;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Mobile;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.Crates.Commands.EnsureInvoiceCrateTransaction;

public sealed class EnsureInvoiceCrateTransactionHandler(
    ApplicationDbContext context,
    ISAPServiceLayerClient sapClient,
    IAuditService auditService,
    ILogger<EnsureInvoiceCrateTransactionHandler> logger
) : IRequestHandler<EnsureInvoiceCrateTransactionCommand, ErrorOr<EnsureInvoiceCrateTransactionResponseDto>>
{
    public async Task<ErrorOr<EnsureInvoiceCrateTransactionResponseDto>> Handle(
        EnsureInvoiceCrateTransactionCommand command,
        CancellationToken cancellationToken)
    {
        if (!command.UserId.HasValue)
        {
            return Errors.Auth.Unauthenticated;
        }

        var currentUser = await context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(user => user.Id == command.UserId.Value, cancellationToken);

        if (currentUser is null || !currentUser.IsActive)
        {
            return Errors.Auth.UserNotFound;
        }

        var invoice = await sapClient.GetInvoiceByDocNumAsync(command.InvoiceDocNum, cancellationToken);
        if (invoice is null)
        {
            return Errors.CrateTracking.InvoiceNotFound(command.InvoiceDocNum);
        }

        if (string.Equals(currentUser.Role, "Driver", StringComparison.OrdinalIgnoreCase))
        {
            var customerCodes = await MobileAssignedCustomerScope.GetEffectiveCustomerCodesAsync(
                context,
                currentUser,
                logger,
                cancellationToken);

            var hasAssignedCustomer = customerCodes.Any(code =>
                string.Equals(code, invoice.CardCode, StringComparison.OrdinalIgnoreCase));

            if (!hasAssignedCustomer)
            {
                return Errors.CrateTracking.AccessDenied($"Invoice #{command.InvoiceDocNum} is outside your assigned customer scope.");
            }
        }

        var resolvedExpectedQuantity = ResolveExpectedQuantity(command.ExpectedQuantity);

        var transaction = await context.CrateTransactions
            .FirstOrDefaultAsync(existing =>
                EF.Functions.ILike(existing.TransactionType, CrateTrackingConstants.TransactionTypeInvoice) &&
                ((existing.InvoiceDocEntry.HasValue && existing.InvoiceDocEntry.Value == invoice.DocEntry) ||
                 (existing.InvoiceDocNum.HasValue && existing.InvoiceDocNum.Value == invoice.DocNum)),
                cancellationToken);

        var created = false;
        if (transaction is null)
        {
            if (resolvedExpectedQuantity <= 0)
            {
                return Errors.CrateTracking.InvalidQuantity("A positive crate quantity is required to initialize crate tracking for this invoice.");
            }

            transaction = new CrateTransactionEntity
            {
                TransactionType = CrateTrackingConstants.TransactionTypeInvoice,
                InvoiceDocEntry = invoice.DocEntry,
                InvoiceDocNum = invoice.DocNum,
                ShopCardCode = invoice.CardCode?.Trim() ?? string.Empty,
                ShopName = invoice.CardName?.Trim(),
                ExpectedQuantity = resolvedExpectedQuantity,
                EffectiveDate = ResolveEffectiveDate(invoice.DocDate),
                Notes = string.IsNullOrWhiteSpace(invoice.Comments) ? null : invoice.Comments.Trim(),
                CreatedAt = DateTime.UtcNow,
                CreatedByUserId = command.UserId
            };

            context.CrateTransactions.Add(transaction);
            created = true;
        }
        else
        {
            transaction.TransactionType = CrateTrackingConstants.TransactionTypeInvoice;
            transaction.InvoiceDocEntry = invoice.DocEntry;
            transaction.InvoiceDocNum = invoice.DocNum;
            transaction.ShopCardCode = string.IsNullOrWhiteSpace(invoice.CardCode)
                ? transaction.ShopCardCode
                : invoice.CardCode.Trim();
            transaction.ShopName = string.IsNullOrWhiteSpace(invoice.CardName)
                ? transaction.ShopName
                : invoice.CardName.Trim();

            if (resolvedExpectedQuantity > 0 && transaction.ExpectedQuantity <= 0)
            {
                transaction.ExpectedQuantity = resolvedExpectedQuantity;
            }

            transaction.EffectiveDate = ResolveEffectiveDate(invoice.DocDate, transaction.EffectiveDate);

            if (!string.IsNullOrWhiteSpace(invoice.Comments) && string.IsNullOrWhiteSpace(transaction.Notes))
            {
                transaction.Notes = invoice.Comments.Trim();
            }

            transaction.UpdatedAt = DateTime.UtcNow;
            transaction.CreatedByUserId ??= command.UserId;
        }

        await context.SaveChangesAsync(cancellationToken);

        if (created)
        {
            try
            {
                await auditService.LogAsync(
                    AuditActions.RegisterInvoiceCrates,
                    "CrateTransaction",
                    transaction.Id.ToString(),
                    $"Initialized missing crate tracking for invoice #{invoice.DocNum} with {transaction.ExpectedQuantity:N2} crates.",
                    true);
            }
            catch
            {
            }
        }

        logger.LogInformation(
            "{Action} invoice crate transaction {TransactionId} for invoice {DocNum} with expected quantity {ExpectedQuantity}",
            created ? "Created" : "Ensured",
            transaction.Id,
            invoice.DocNum,
            transaction.ExpectedQuantity);

        return new EnsureInvoiceCrateTransactionResponseDto
        {
            Id = transaction.Id,
            TransactionType = transaction.TransactionType,
            InvoiceDocEntry = transaction.InvoiceDocEntry,
            InvoiceDocNum = transaction.InvoiceDocNum,
            ShopCardCode = transaction.ShopCardCode,
            ShopName = transaction.ShopName,
            ExpectedQuantity = transaction.ExpectedQuantity
        };
    }

    private static decimal ResolveExpectedQuantity(decimal? fallbackQuantity)
        => fallbackQuantity.GetValueOrDefault();

    private static DateTime ResolveEffectiveDate(string? docDate, DateTime? fallback = null)
    {
        if (DateTime.TryParse(docDate, out var parsedDate))
        {
            return DateTime.SpecifyKind(parsedDate.Date, DateTimeKind.Utc);
        }

        return fallback ?? DateTime.UtcNow.Date;
    }
}