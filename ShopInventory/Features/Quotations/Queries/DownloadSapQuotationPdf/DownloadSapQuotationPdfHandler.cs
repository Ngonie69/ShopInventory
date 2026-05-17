using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Features.Quotations.Queries.GetQuotationFromSAPByDocEntry;
using ShopInventory.Services;

namespace ShopInventory.Features.Quotations.Queries.DownloadSapQuotationPdf;

public sealed class DownloadSapQuotationPdfHandler(
    ISender sender,
    IQuotationPdfService quotationPdfService,
    ISAPServiceLayerClient sapClient,
    ILogger<DownloadSapQuotationPdfHandler> logger
) : IRequestHandler<DownloadSapQuotationPdfQuery, ErrorOr<(byte[] PdfBytes, string FileName)>>
{
    public async Task<ErrorOr<(byte[] PdfBytes, string FileName)>> Handle(
        DownloadSapQuotationPdfQuery request,
        CancellationToken cancellationToken)
    {
        var quotationResult = await sender.Send(new GetQuotationFromSAPByDocEntryQuery(request.DocEntry), cancellationToken);
        if (quotationResult.IsError)
        {
            return quotationResult.Errors;
        }

        var quotation = quotationResult.Value;

        string? customerVatNo = null;
        string? customerTinNumber = null;
        string? customerPhone = null;
        string? customerEmail = null;

        if (!string.IsNullOrWhiteSpace(quotation.CardCode))
        {
            try
            {
                var businessPartner = await sapClient.GetBusinessPartnerByCodeAsync(quotation.CardCode, cancellationToken);
                if (businessPartner is not null)
                {
                    customerVatNo = businessPartner.VatRegNo;
                    customerTinNumber = businessPartner.TinNumber;
                    customerPhone = businessPartner.Phone1;
                    customerEmail = businessPartner.Email;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not enrich SAP quotation {QuotationNumber} PDF with business partner details", quotation.QuotationNumber);
            }
        }

        try
        {
            var pdfBytes = await quotationPdfService.GenerateQuotationPdfAsync(
                quotation,
                customerVatNo,
                customerTinNumber,
                customerPhone,
                customerEmail);

            var fileName = $"Quotation_{quotation.QuotationNumber}_{DateTime.UtcNow:yyyyMMdd}.pdf";
            return (pdfBytes, fileName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating PDF for SAP quotation {DocEntry}", request.DocEntry);
            return Errors.Quotation.CreationFailed("Failed to generate SAP quotation PDF.");
        }
    }
}