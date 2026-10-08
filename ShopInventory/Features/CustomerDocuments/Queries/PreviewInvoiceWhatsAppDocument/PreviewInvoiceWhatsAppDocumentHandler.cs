using System.Globalization;
using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Features.CustomerDocuments.Documents;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.CustomerDocuments.Queries.PreviewInvoiceWhatsAppDocument;

/// <summary>
/// Runs the same preparation the delivery job runs — fiscal check, render, caption — without sending
/// or saving anything, so the person about to send sees the very document the customer would get.
/// </summary>
/// <remarks>
/// The device is asked at once rather than after the job's delay: a person is waiting on the answer.
/// </remarks>
public sealed class PreviewInvoiceWhatsAppDocumentHandler(
    ISAPServiceLayerClient sapClient,
    ISapInvoiceDocumentComposer composer,
    IOptions<SAPSettings> sapSettings,
    IOptions<CustomerDocumentDeliverySettings> options,
    ILogger<PreviewInvoiceWhatsAppDocumentHandler> logger)
    : IRequestHandler<PreviewInvoiceWhatsAppDocumentQuery, ErrorOr<InvoiceWhatsAppPreviewDto>>
{
    public async Task<ErrorOr<InvoiceWhatsAppPreviewDto>> Handle(
        PreviewInvoiceWhatsAppDocumentQuery query,
        CancellationToken cancellationToken)
    {
        if (!sapSettings.Value.Enabled)
            return Errors.CustomerDocuments.DocumentUnavailable("SAP is switched off, so no invoice can be read.");

        Invoice? invoice;
        try
        {
            invoice = (await sapClient.GetInvoiceDeliveryHeadersAsync([query.DocEntry], cancellationToken))
                .FirstOrDefault(header => header.DocEntry == query.DocEntry);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read invoice {DocEntry} from SAP for a WhatsApp preview", query.DocEntry);
            return Errors.CustomerDocuments.GatewayUnavailable("SAP could not be asked about the invoice. Try again in a moment.");
        }

        if (invoice is null)
            return Errors.CustomerDocuments.InvoiceNotFound(query.DocEntry);

        var now = DateTime.UtcNow;
        var probe = new CustomerDocumentDeliveryEntity
        {
            DocumentType = CustomerDocumentType.SapInvoice,
            SapDocEntry = invoice.DocEntry,
            SapDocNum = invoice.DocNum,
            DocumentNumber = invoice.DocNum.ToString(CultureInfo.InvariantCulture),
            SaleReference = string.IsNullOrWhiteSpace(invoice.U_Van_saleorder) ? null : invoice.U_Van_saleorder.Trim(),
            DocumentDate = InvoiceDeliveryRules.ParseDocDate(invoice.DocDate),
            DocumentTotal = invoice.DocTotal,
            DocumentTotalFc = InvoiceDeliveryRules.ForeignTotal(invoice),
            Currency = invoice.DocCurrency,
            CardCode = invoice.CardCode,
            CardName = invoice.CardName,
            RecipientE164 = string.Empty,
            // Old enough that the device may be asked straight away.
            CreatedAtUtc = now.AddMinutes(-Math.Max(0, options.Value.DeviceLookupAfterMinutes) - 1)
        };

        var composition = await composer.ComposeAsync(probe, now, cancellationToken);

        if (composition.Kind != DocumentCompositionKind.Ready || composition.Document is null)
        {
            return new InvoiceWhatsAppPreviewDto
            {
                Ready = false,
                Reason = composition.Reason
            };
        }

        return new InvoiceWhatsAppPreviewDto
        {
            Ready = true,
            FileName = composition.Document.FileName,
            Caption = composition.Document.Caption,
            FiscalEvidenceSource = composition.Document.FiscalEvidenceSource,
            PdfBase64 = Convert.ToBase64String(composition.Document.Bytes)
        };
    }
}
