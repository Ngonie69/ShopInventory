using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Validation;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.Quotations.Commands.RepriceQuotation;

public sealed class RepriceQuotationHandler(
    ApplicationDbContext context,
    IQuotationService quotationService,
    ILogger<RepriceQuotationHandler> logger
) : IRequestHandler<RepriceQuotationCommand, ErrorOr<QuotationDto>>
{
    public async Task<ErrorOr<QuotationDto>> Handle(
        RepriceQuotationCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            await ValidateAndNormalizeRequestAsync(command.Request, cancellationToken);

            var quotation = await context.Quotations
                .Include(q => q.Lines.OrderBy(line => line.LineNum))
                .FirstOrDefaultAsync(q => q.Id == command.Id, cancellationToken);

            if (quotation is null)
            {
                return Errors.Quotation.NotFound(command.Id);
            }

            if (!CanReprice(quotation))
            {
                return Errors.Quotation.InvalidOperation("Only open local quotations without a sales order can be repriced.");
            }

            if (!string.Equals(command.Request.CardCode, quotation.CardCode, StringComparison.OrdinalIgnoreCase))
            {
                return Errors.Quotation.InvalidOperation("Changing the quotation customer is not supported in reprice mode.");
            }

            if (!string.Equals(command.Request.Currency, quotation.Currency, StringComparison.OrdinalIgnoreCase))
            {
                return Errors.Quotation.InvalidOperation("Changing the quotation currency is not supported in reprice mode.");
            }

            var existingLines = quotation.Lines.OrderBy(line => line.LineNum).ToList();
            var requestedLines = command.Request.Lines;
            if (existingLines.Count != requestedLines.Count)
            {
                return Errors.Quotation.InvalidOperation("Reprice mode does not allow adding or removing quotation lines.");
            }

            for (var index = 0; index < existingLines.Count; index++)
            {
                if (!string.Equals(existingLines[index].ItemCode, requestedLines[index].ItemCode, StringComparison.OrdinalIgnoreCase))
                {
                    return Errors.Quotation.InvalidOperation("Reprice mode does not allow changing quotation line items.");
                }
            }

            quotation.ValidUntil = command.Request.ValidUntil.HasValue
                ? DateTime.SpecifyKind(command.Request.ValidUntil.Value, DateTimeKind.Utc)
                : quotation.ValidUntil;
            quotation.CustomerRefNo = command.Request.CustomerRefNo;
            quotation.ContactPerson = command.Request.ContactPerson;
            quotation.Comments = command.Request.Comments;
            quotation.TermsAndConditions = command.Request.TermsAndConditions;
            quotation.DiscountPercent = command.Request.DiscountPercent;
            quotation.ShipToAddress = command.Request.ShipToAddress;
            quotation.BillToAddress = command.Request.BillToAddress;
            quotation.UpdatedAt = DateTime.UtcNow;

            decimal subTotal = 0;
            decimal taxAmount = 0;

            for (var index = 0; index < existingLines.Count; index++)
            {
                var existingLine = existingLines[index];
                var requestLine = requestedLines[index];
                var lineTotal = requestLine.Quantity * requestLine.UnitPrice * (1 - requestLine.DiscountPercent / 100m);
                var lineTax = lineTotal * requestLine.TaxPercent / 100m;

                existingLine.Quantity = requestLine.Quantity;
                existingLine.UnitPrice = requestLine.UnitPrice;
                existingLine.DiscountPercent = requestLine.DiscountPercent;
                existingLine.TaxPercent = requestLine.TaxPercent;
                existingLine.LineTotal = lineTotal;

                subTotal += lineTotal;
                taxAmount += lineTax;
            }

            quotation.SubTotal = subTotal;
            quotation.TaxAmount = taxAmount;
            quotation.DiscountAmount = subTotal * quotation.DiscountPercent / 100m;
            quotation.DocTotal = subTotal - quotation.DiscountAmount + taxAmount;

            if (quotation.SAPDocEntry.HasValue)
            {
                quotation.IsSynced = false;
                quotation.SyncError = "Quotation was repriced locally and now requires SAP review.";
            }

            await context.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Repriced quotation {QuotationNumber}; SAP sync state is {IsSynced}",
                quotation.QuotationNumber,
                quotation.IsSynced);

            var updatedQuotation = await quotationService.GetByIdAsync(command.Id, cancellationToken);
            return updatedQuotation is not null ? updatedQuotation : Errors.Quotation.NotFound(command.Id);
        }
        catch (InvalidOperationException ex)
        {
            return Errors.Quotation.InvalidOperation(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error repricing quotation {Id}", command.Id);
            return Errors.Quotation.UpdateFailed(ex.Message);
        }
    }

    private async Task ValidateAndNormalizeRequestAsync(CreateQuotationRequest request, CancellationToken cancellationToken)
    {
        var validationErrors = RecursiveDataAnnotationsValidator.Validate(request);

        if (string.IsNullOrWhiteSpace(request.Currency))
        {
            validationErrors.Add("Currency is required");
        }
        else
        {
            request.Currency = request.Currency.Trim().ToUpperInvariant();
        }

        request.CardCode = request.CardCode?.Trim() ?? string.Empty;
        request.CardName = request.CardName?.Trim();
        request.CustomerRefNo = request.CustomerRefNo?.Trim();
        request.ContactPerson = request.ContactPerson?.Trim();
        request.Comments = request.Comments?.Trim();
        request.TermsAndConditions = request.TermsAndConditions?.Trim();
        request.ShipToAddress = request.ShipToAddress?.Trim();
        request.BillToAddress = request.BillToAddress?.Trim();

        validationErrors.AddRange(await UomQuantityValidation.ValidateAndNormalizeLineQuantitiesAsync(
            context,
            request.Lines,
            line => line.ItemCode,
            line => line.Quantity,
            line => line.UoMCode,
            (line, uomCode) => line.UoMCode = uomCode,
            cancellationToken,
            requireAtLeastOneLine: false));

        if (validationErrors.Count > 0)
        {
            throw new InvalidOperationException($"Quotation validation failed: {string.Join("; ", validationErrors)}");
        }
    }

    private static bool CanReprice(QuotationEntity quotation)
    {
        return !quotation.SalesOrderId.HasValue
            && quotation.Status != QuotationStatus.Accepted
            && quotation.Status != QuotationStatus.Converted
            && quotation.Status != QuotationStatus.Cancelled;
    }
}