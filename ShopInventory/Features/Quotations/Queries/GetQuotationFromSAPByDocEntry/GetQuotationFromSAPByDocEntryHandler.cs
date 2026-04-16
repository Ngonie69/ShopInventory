using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.Quotations.Queries.GetQuotationFromSAPByDocEntry;

public sealed class GetQuotationFromSAPByDocEntryHandler(
    ISAPServiceLayerClient sapClient,
    ILogger<GetQuotationFromSAPByDocEntryHandler> logger
) : IRequestHandler<GetQuotationFromSAPByDocEntryQuery, ErrorOr<QuotationDto>>
{
    public async Task<ErrorOr<QuotationDto>> Handle(
        GetQuotationFromSAPByDocEntryQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var sapQuotation = await sapClient.GetQuotationByDocEntryAsync(request.DocEntry, cancellationToken);
            if (sapQuotation == null)
                return Errors.Quotation.NotFoundByDocEntry(request.DocEntry);

            return MapSAPToQuotationDto(sapQuotation);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching quotation {DocEntry} from SAP", request.DocEntry);
            return Errors.Quotation.CreationFailed($"Failed to fetch quotation from SAP: {ex.Message}");
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
