using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.Quotations.Commands.ApplyStandardVat;

public sealed class ApplyStandardVatHandler(
    ApplicationDbContext context,
    ILogger<ApplyStandardVatHandler> logger
) : IRequestHandler<ApplyStandardVatCommand, ErrorOr<QuotationDto>>
{
    private const decimal StandardVatPercent = 15.5m;

    public async Task<ErrorOr<QuotationDto>> Handle(
        ApplyStandardVatCommand command,
        CancellationToken cancellationToken)
    {
        var quotation = await context.Quotations
            .Include(q => q.Lines)
            .Include(q => q.CreatedByUser)
            .Include(q => q.ApprovedByUser)
            .FirstOrDefaultAsync(q => q.Id == command.Id, cancellationToken);

        if (quotation is null)
        {
            return Errors.Quotation.NotFound(command.Id);
        }

        if (quotation.Status == QuotationStatus.Converted || quotation.Status == QuotationStatus.Cancelled || quotation.SalesOrderId.HasValue)
        {
            return Errors.Quotation.InvalidOperation("Only open local quotations can have VAT applied.");
        }

        if (quotation.Lines.Count == 0)
        {
            return Errors.Quotation.InvalidOperation("Quotation has no lines to update.");
        }

        var updatedLineCount = 0;
        foreach (var line in quotation.Lines)
        {
            if (line.TaxPercent <= 0)
            {
                line.TaxPercent = StandardVatPercent;
                updatedLineCount++;
            }
        }

        if (updatedLineCount == 0)
        {
            return Errors.Quotation.InvalidOperation("All quotation lines already have VAT.");
        }

        quotation.TaxAmount = quotation.Lines.Sum(line => line.LineTotal * line.TaxPercent / 100m);
        quotation.DocTotal = quotation.SubTotal - quotation.DiscountAmount + quotation.TaxAmount;
        quotation.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Applied standard VAT to quotation {QuotationNumber}; updated {UpdatedLineCount} line(s)",
            quotation.QuotationNumber,
            updatedLineCount);

        return MapToDto(quotation);
    }

    private static QuotationDto MapToDto(QuotationEntity entity)
    {
        return new QuotationDto
        {
            Id = entity.Id,
            SAPDocEntry = entity.SAPDocEntry,
            SAPDocNum = entity.SAPDocNum,
            QuotationNumber = entity.QuotationNumber,
            QuotationDate = entity.QuotationDate,
            ValidUntil = entity.ValidUntil,
            CardCode = entity.CardCode,
            CardName = entity.CardName,
            CustomerRefNo = entity.CustomerRefNo,
            ContactPerson = entity.ContactPerson,
            Status = entity.Status,
            Comments = entity.Comments,
            TermsAndConditions = entity.TermsAndConditions,
            SalesPersonCode = entity.SalesPersonCode,
            SalesPersonName = entity.SalesPersonName,
            Currency = entity.Currency,
            ExchangeRate = entity.ExchangeRate,
            SubTotal = entity.SubTotal,
            TaxAmount = entity.TaxAmount,
            DiscountPercent = entity.DiscountPercent,
            DiscountAmount = entity.DiscountAmount,
            DocTotal = entity.DocTotal,
            ShipToAddress = entity.ShipToAddress,
            BillToAddress = entity.BillToAddress,
            WarehouseCode = entity.WarehouseCode,
            ClientRequestId = entity.ClientRequestId,
            CreatedByUserId = entity.CreatedByUserId,
            CreatedByUserName = ResolveDisplayName(entity.CreatedByUser?.FirstName, entity.CreatedByUser?.LastName, entity.CreatedByUser?.Username),
            ApprovedByUserId = entity.ApprovedByUserId,
            ApprovedByUserName = ResolveDisplayName(entity.ApprovedByUser?.FirstName, entity.ApprovedByUser?.LastName, entity.ApprovedByUser?.Username),
            ApprovedDate = entity.ApprovedDate,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt,
            SalesOrderId = entity.SalesOrderId,
            IsSynced = entity.IsSynced,
            Lines = entity.Lines
                .OrderBy(line => line.LineNum)
                .Select(line => new QuotationLineDto
                {
                    Id = line.Id,
                    LineNum = line.LineNum,
                    ItemCode = line.ItemCode,
                    ItemDescription = line.ItemDescription,
                    Quantity = line.Quantity,
                    UnitPrice = line.UnitPrice,
                    DiscountPercent = line.DiscountPercent,
                    TaxPercent = line.TaxPercent,
                    LineTotal = line.LineTotal,
                    WarehouseCode = line.WarehouseCode,
                    UoMCode = line.UoMCode
                })
                .ToList()
        };
    }

    private static string? ResolveDisplayName(string? firstName, string? lastName, string? username)
    {
        var combined = string.Join(" ", new[] { firstName, lastName }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
        return !string.IsNullOrWhiteSpace(combined) ? combined : username;
    }
}