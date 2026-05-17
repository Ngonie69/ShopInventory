using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Services;

namespace ShopInventory.Features.Quotations.Queries.DownloadQuotationPdf;

public sealed class DownloadQuotationPdfHandler(
    IQuotationService quotationService,
    IQuotationPdfService quotationPdfService,
    ISAPServiceLayerClient sapClient,
    ILogger<DownloadQuotationPdfHandler> logger
) : IRequestHandler<DownloadQuotationPdfQuery, ErrorOr<(byte[] PdfBytes, string FileName)>>
{
    public async Task<ErrorOr<(byte[] PdfBytes, string FileName)>> Handle(
        DownloadQuotationPdfQuery request,
        CancellationToken cancellationToken)
    {
        var quotation = await quotationService.GetByIdAsync(request.Id, cancellationToken);
        if (quotation is null)
        {
            return Errors.Quotation.NotFound(request.Id);
        }

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
                logger.LogWarning(ex, "Could not enrich quotation {QuotationNumber} PDF with business partner details", quotation.QuotationNumber);
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
            logger.LogError(ex, "Error generating PDF for quotation {QuotationNumber}", quotation.QuotationNumber);
            return Errors.Quotation.CreationFailed("Failed to generate quotation PDF.");
        }
    }
}