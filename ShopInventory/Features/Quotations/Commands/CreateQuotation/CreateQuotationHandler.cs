using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Validation;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.Quotations.Commands.CreateQuotation;

public sealed class CreateQuotationHandler(
    ApplicationDbContext context,
    ISAPServiceLayerClient sapClient,
    ILogger<CreateQuotationHandler> logger
) : IRequestHandler<CreateQuotationCommand, ErrorOr<QuotationDto>>
{
    public async Task<ErrorOr<QuotationDto>> Handle(
        CreateQuotationCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            await ValidateAndNormalizeQuotationRequestAsync(command.Request, cancellationToken);

            if (!string.IsNullOrWhiteSpace(command.Request.ClientRequestId))
            {
                var existingQuotationByRequestId = await context.Quotations
                    .AsNoTracking()
                    .Include(item => item.Lines)
                    .Include(item => item.CreatedByUser)
                    .Include(item => item.ApprovedByUser)
                    .FirstOrDefaultAsync(item => item.ClientRequestId == command.Request.ClientRequestId, cancellationToken);

                if (existingQuotationByRequestId != null)
                {
                    return MapToDto(existingQuotationByRequestId);
                }
            }

            var quotationNumber = await GenerateQuotationNumberAsync(cancellationToken);
            var createdAtUtc = DateTime.UtcNow;
            var creator = await context.Users
                .AsNoTracking()
                .Where(user => user.Id == command.UserId)
                .Select(user => new { user.FirstName, user.LastName, user.Username })
                .FirstOrDefaultAsync(cancellationToken);

            var creatorName = ResolveCreatorName(creator?.FirstName, creator?.LastName, creator?.Username);
            var quotation = BuildQuotationEntity(command.Request, command.UserId, quotationNumber, createdAtUtc, creatorName);
            var externalOrderNumber = string.IsNullOrWhiteSpace(command.Request.ClientRequestId)
                ? quotationNumber
                : command.Request.ClientRequestId.Trim();

            var sapQuotation = await sapClient.CreateQuotationAsync(quotation, externalOrderNumber, cancellationToken);

            var existingQuotation = await context.Quotations
                .Include(item => item.Lines)
                .Include(item => item.CreatedByUser)
                .Include(item => item.ApprovedByUser)
                .FirstOrDefaultAsync(item => item.SAPDocEntry == sapQuotation.DocEntry, cancellationToken);

            if (existingQuotation != null)
            {
                return MapToDto(existingQuotation);
            }

            quotation.SAPDocEntry = sapQuotation.DocEntry;
            quotation.SAPDocNum = sapQuotation.DocNum;
            quotation.IsSynced = true;
            quotation.SyncError = null;
            quotation.Status = QuotationStatus.Approved;
            quotation.ApprovedByUserId = command.UserId;
            quotation.ApprovedDate = createdAtUtc;
            quotation.UpdatedAt = createdAtUtc;

            context.Quotations.Add(quotation);
            await context.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Created quotation {QuotationNumber} in SAP with DocEntry {DocEntry} and DocNum {DocNum}",
                quotation.QuotationNumber,
                quotation.SAPDocEntry,
                quotation.SAPDocNum);

            return MapToDto(quotation);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating quotation");
            var message = ex.InnerException?.Message ?? ex.Message;
            return Errors.Quotation.CreationFailed(message);
        }
    }

    private async Task<string> GenerateQuotationNumberAsync(CancellationToken cancellationToken)
    {
        var today = DateTime.UtcNow.ToString("yyyyMMdd");
        var prefix = $"QT-{today}-";

        var lastQuotation = await context.Quotations
            .AsNoTracking()
            .Where(item => item.QuotationNumber.StartsWith(prefix))
            .OrderByDescending(item => item.QuotationNumber.Length)
            .ThenByDescending(item => item.QuotationNumber)
            .FirstOrDefaultAsync(cancellationToken);

        var sequence = 1L;
        if (lastQuotation != null)
        {
            var lastSequence = lastQuotation.QuotationNumber.Replace(prefix, string.Empty, StringComparison.Ordinal);
            if (long.TryParse(lastSequence, out var parsed))
            {
                sequence = parsed + 1;
            }
        }

        return $"{prefix}{sequence:D4}";
    }

    private async Task ValidateAndNormalizeQuotationRequestAsync(CreateQuotationRequest request, CancellationToken cancellationToken)
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

        request.ClientRequestId = string.IsNullOrWhiteSpace(request.ClientRequestId)
            ? null
            : request.ClientRequestId.Trim();

        request.Source = string.IsNullOrWhiteSpace(request.Source)
            ? "Web"
            : request.Source.Trim();

        validationErrors.AddRange(await UomQuantityValidation.ValidateAndNormalizeLineQuantitiesAsync(
            context,
            request.Lines,
            line => line.ItemCode,
            line => line.Quantity,
            line => line.UoMCode,
            (line, uomCode) => line.UoMCode = uomCode,
            cancellationToken,
            requireAtLeastOneLine: false));

        if (validationErrors.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException($"Quotation validation failed: {string.Join("; ", validationErrors)}");
    }

    private static QuotationEntity BuildQuotationEntity(
        CreateQuotationRequest request,
        Guid userId,
        string quotationNumber,
        DateTime createdAtUtc,
        string creatorName)
    {
        var quotation = new QuotationEntity
        {
            QuotationNumber = quotationNumber,
            QuotationDate = createdAtUtc,
            ValidUntil = request.ValidUntil.HasValue
                ? DateTime.SpecifyKind(request.ValidUntil.Value, DateTimeKind.Utc)
                : createdAtUtc.AddDays(30),
            CardCode = request.CardCode,
            CardName = request.CardName,
            CustomerRefNo = request.CustomerRefNo,
            ContactPerson = request.ContactPerson,
            Comments = BuildSapComments(request.Comments, quotationNumber, creatorName, request.Source, createdAtUtc),
            TermsAndConditions = request.TermsAndConditions,
            SalesPersonCode = request.SalesPersonCode,
            SalesPersonName = request.SalesPersonName,
            Currency = request.Currency,
            DiscountPercent = request.DiscountPercent,
            ShipToAddress = request.ShipToAddress,
            BillToAddress = request.BillToAddress,
            WarehouseCode = request.WarehouseCode,
            ClientRequestId = request.ClientRequestId,
            CreatedByUserId = userId,
            CreatedAt = createdAtUtc,
            UpdatedAt = createdAtUtc,
            Status = QuotationStatus.Approved,
            IsSynced = false,
            SyncError = null
        };

        decimal subTotal = 0;
        decimal taxAmount = 0;
        var lineNumber = 0;

        foreach (var lineRequest in request.Lines)
        {
            var lineTotal = lineRequest.Quantity * lineRequest.UnitPrice * (1 - lineRequest.DiscountPercent / 100);
            var lineTax = lineTotal * lineRequest.TaxPercent / 100;

            quotation.Lines.Add(new QuotationLineEntity
            {
                LineNum = lineNumber++,
                ItemCode = lineRequest.ItemCode,
                ItemDescription = lineRequest.ItemDescription,
                Quantity = lineRequest.Quantity,
                UnitPrice = lineRequest.UnitPrice,
                DiscountPercent = lineRequest.DiscountPercent,
                TaxPercent = lineRequest.TaxPercent,
                LineTotal = lineTotal,
                WarehouseCode = lineRequest.WarehouseCode ?? request.WarehouseCode,
                UoMCode = lineRequest.UoMCode
            });

            subTotal += lineTotal;
            taxAmount += lineTax;
        }

        quotation.SubTotal = subTotal;
        quotation.TaxAmount = taxAmount;
        quotation.DiscountAmount = subTotal * request.DiscountPercent / 100;
        quotation.DocTotal = subTotal - quotation.DiscountAmount + taxAmount;

        return quotation;
    }

    private static string? BuildSapComments(
        string? originalComments,
        string quotationNumber,
        string creatorName,
        string? source,
        DateTime createdAtUtc)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(originalComments))
        {
            parts.Add(originalComments.Trim());
        }

        var normalizedSource = string.IsNullOrWhiteSpace(source)
            ? "Web"
            : source.Trim();
        var createdAtCat = AuditService.ToCAT(createdAtUtc);

        parts.Add($"Origin: {normalizedSource} | Ref: {quotationNumber} | Created by: {creatorName} | Created at: {createdAtCat:yyyy-MM-dd HH:mm} CAT");

        var combined = string.Join(Environment.NewLine, parts);
        return combined.Length <= 2000
            ? combined
            : $"{combined[..1997]}...";
    }

    private static string ResolveCreatorName(string? firstName, string? lastName, string? username)
    {
        var fullName = $"{firstName} {lastName}".Trim();
        if (!string.IsNullOrWhiteSpace(fullName))
        {
            return fullName;
        }

        return !string.IsNullOrWhiteSpace(username) ? username.Trim() : "Unknown";
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
            CreatedByUserName = entity.CreatedByUser?.Username,
            ApprovedByUserId = entity.ApprovedByUserId,
            ApprovedByUserName = entity.ApprovedByUser?.Username,
            ApprovedDate = entity.ApprovedDate,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt,
            SalesOrderId = entity.SalesOrderId,
            IsSynced = entity.IsSynced,
            Lines = entity.Lines.Select(line => new QuotationLineDto
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
            }).ToList()
        };
    }
}
