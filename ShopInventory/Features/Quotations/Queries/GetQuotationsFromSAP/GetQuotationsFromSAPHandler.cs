using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.Quotations.Queries.GetQuotationsFromSAP;

public sealed class GetQuotationsFromSAPHandler(
    ISAPServiceLayerClient sapClient,
    ILogger<GetQuotationsFromSAPHandler> logger
) : IRequestHandler<GetQuotationsFromSAPQuery, ErrorOr<QuotationListResponseDto>>
{
    public async Task<ErrorOr<QuotationListResponseDto>> Handle(
        GetQuotationsFromSAPQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            List<SAPQuotation> sapQuotations;

            if (request.FromDate.HasValue && request.ToDate.HasValue)
            {
                sapQuotations = await sapClient.GetQuotationsByDateRangeAsync(request.FromDate.Value, request.ToDate.Value, cancellationToken);
            }
            else if (!string.IsNullOrEmpty(request.CardCode))
            {
                sapQuotations = await sapClient.GetQuotationsByCustomerAsync(request.CardCode, cancellationToken);
            }
            else
            {
                sapQuotations = await sapClient.GetPagedQuotationsFromSAPAsync(request.Page, request.PageSize, cancellationToken);
            }

            if (!string.IsNullOrEmpty(request.CardCode) && request.FromDate.HasValue)
            {
                sapQuotations = sapQuotations.Where(q => q.CardCode == request.CardCode).ToList();
            }

            var totalCount = sapQuotations.Count;

            if (request.FromDate.HasValue || !string.IsNullOrEmpty(request.CardCode))
            {
                sapQuotations = sapQuotations
                    .Skip((request.Page - 1) * request.PageSize)
                    .Take(request.PageSize)
                    .ToList();
            }

            var quotations = sapQuotations.Select(MapSAPToQuotationDto).ToList();

            return new QuotationListResponseDto
            {
                Page = request.Page,
                PageSize = request.PageSize,
                TotalCount = totalCount,
                TotalPages = (int)Math.Ceiling(totalCount / (double)request.PageSize),
                Quotations = quotations
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching quotations from SAP");
            return Errors.Quotation.CreationFailed($"Failed to fetch quotations from SAP: {ex.Message}");
        }
    }

    private static QuotationDto MapSAPToQuotationDto(SAPQuotation sap)
    {
        var isCancelled = sap.Cancelled == "tYES";
        var isClosed = sap.DocumentStatus == "bost_Close";

        QuotationStatus status;
        if (isCancelled)
            status = QuotationStatus.Cancelled;
        else if (isClosed)
            status = QuotationStatus.Converted;
        else
            status = QuotationStatus.Approved;

        DateTime.TryParse(sap.DocDate, out var quotationDate);
        DateTime.TryParse(sap.DocDueDate, out var validUntil);

        var lines = sap.DocumentLines?.Select((l, idx) => new QuotationLineDto
        {
            Id = idx,
            LineNum = l.LineNum,
            ItemCode = l.ItemCode ?? "",
            ItemDescription = l.ItemDescription ?? "",
            Quantity = l.Quantity ?? 0,
            UnitPrice = l.UnitPrice ?? 0,
            LineTotal = l.LineTotal ?? 0,
            WarehouseCode = l.WarehouseCode,
            DiscountPercent = l.DiscountPercent ?? 0,
            UoMCode = l.UoMCode
        }).ToList() ?? new List<QuotationLineDto>();

        return new QuotationDto
        {
            Id = sap.DocEntry,
            SAPDocEntry = sap.DocEntry,
            SAPDocNum = sap.DocNum,
            QuotationNumber = $"SAP-{sap.DocNum}",
            QuotationDate = quotationDate,
            ValidUntil = validUntil == default ? null : validUntil,
            CardCode = sap.CardCode ?? "",
            CardName = sap.CardName,
            CustomerRefNo = sap.NumAtCard,
            Status = status,
            Currency = sap.DocCurrency ?? "USD",
            SubTotal = (sap.DocTotal ?? 0) - (sap.VatSum ?? 0),
            TaxAmount = sap.VatSum ?? 0,
            DiscountPercent = sap.DiscountPercent ?? 0,
            DiscountAmount = sap.TotalDiscount ?? 0,
            DocTotal = sap.DocTotal ?? 0,
            Comments = sap.Comments,
            ShipToAddress = sap.Address,
            BillToAddress = sap.Address2,
            Lines = lines,
            CreatedByUserName = "SAP",
            IsSynced = true
        };
    }
}
