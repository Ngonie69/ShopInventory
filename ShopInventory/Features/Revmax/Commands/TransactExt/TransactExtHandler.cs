using System.Globalization;
using System.Xml.Linq;
using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Models.Revmax;
using ShopInventory.Services;

namespace ShopInventory.Features.Revmax.Commands.TransactExt;

public sealed class TransactExtHandler(
    IRevmaxClient revmaxClient,
    IOptions<RevmaxSettings> settings,
    ILogger<TransactExtHandler> logger
) : IRequestHandler<TransactExtCommand, ErrorOr<TransactMExtResponse>>
{
    private readonly RevmaxSettings _settings = settings.Value;

    public async Task<ErrorOr<TransactMExtResponse>> Handle(
        TransactExtCommand command,
        CancellationToken cancellationToken)
    {
        var request = command.Request;

        // Validate request
        var validationErrors = ValidateRequest(request);
        if (validationErrors.Count > 0)
        {
            return validationErrors;
        }

        try
        {
            // Apply defaults
            request.Currency ??= _settings.DefaultCurrency;
            request.BranchName ??= _settings.DefaultBranchName;
            request.refDeviceId ??= _settings.DefaultRefDeviceId;

            // Process and validate items
            var itemsResult = ProcessAndValidateItems(request);
            if (itemsResult.IsError)
            {
                return itemsResult.Errors;
            }
            request.ItemsXml = itemsResult.Value;

            // Check if this is a credit note
            bool isCreditNote = !string.IsNullOrWhiteSpace(request.OriginalInvoiceNumber);

            if (isCreditNote)
            {
                // Validate original invoice exists and is fiscalized
                var originalInvoice = await revmaxClient.GetInvoiceAsync(request.OriginalInvoiceNumber!, cancellationToken);

                if (originalInvoice == null || !originalInvoice.Success)
                {
                    return Errors.Revmax.InvoiceNotFound(request.OriginalInvoiceNumber!);
                }

                bool hasFiscalEvidence = !string.IsNullOrWhiteSpace(originalInvoice.QRcode) ||
                                         (originalInvoice.Data?.ReceiptGlobalNo > 0);

                if (!hasFiscalEvidence)
                {
                    return Errors.Revmax.TransactionFailed($"Original invoice not fiscalized: {request.OriginalInvoiceNumber}");
                }

                // Copy fiscal reference from original invoice if available
                if (originalInvoice.Data != null)
                {
                    if (int.TryParse(originalInvoice.FiscalDay, out var fiscalDay))
                    {
                        request.refFiscalDayNo ??= fiscalDay;
                    }
                    request.refReceiptGlobalNo ??= originalInvoice.Data.ReceiptGlobalNo;
                }

                // Check for duplicate credit note fiscalization
                var existingInvoice = await revmaxClient.GetInvoiceAsync(request.InvoiceNumber!, cancellationToken);
                if (existingInvoice is { Success: true })
                {
                    bool isDuplicate = !string.IsNullOrWhiteSpace(existingInvoice.QRcode) ||
                                       (existingInvoice.Data?.ReceiptGlobalNo > 0);
                    if (isDuplicate)
                    {
                        return Errors.Revmax.TransactionFailed($"Credit note already fiscalized: {request.InvoiceNumber}");
                    }
                }

                request.Istatus = "02";

                logger.LogInformation("Processing credit note (ext) {CreditNoteNumber} for original invoice {OriginalInvoiceNumber}",
                    request.InvoiceNumber, request.OriginalInvoiceNumber);
            }

            var result = await revmaxClient.TransactMExtAsync(request, cancellationToken);
            if (result is null)
                return Errors.Revmax.DeviceError("No response from device");
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing extended transaction for invoice {InvoiceNumber}", request.InvoiceNumber);
            return Errors.Revmax.TransactionFailed(ex.Message);
        }
    }

    private static List<Error> ValidateRequest(TransactMExtRequest request)
    {
        var errors = new List<Error>();

        if (string.IsNullOrWhiteSpace(request.InvoiceNumber))
            errors.Add(Error.Validation("Revmax.InvalidInvoiceNumber", "Invoice number is required"));

        if (string.IsNullOrWhiteSpace(request.ItemsXml))
            errors.Add(Error.Validation("Revmax.InvalidItems", "Items XML is required"));

        if (request.InvoiceAmount < 0)
            errors.Add(Error.Validation("Revmax.InvalidAmount", "Invoice amount must be >= 0"));

        if (request.InvoiceTaxAmount < 0)
            errors.Add(Error.Validation("Revmax.InvalidTaxAmount", "Invoice tax amount must be >= 0"));

        return errors;
    }

    private ErrorOr<string> ProcessAndValidateItems(TransactMRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ItemsXml))
        {
            return Errors.Revmax.InvalidItems;
        }

        try
        {
            var doc = XDocument.Parse(request.ItemsXml);
            var items = doc.Descendants("item").ToList();

            if (items.Count == 0)
            {
                return Error.Validation("Revmax.NoItems", "At least one item is required in ItemsXml");
            }

            var errorMessages = new List<string>();

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var lineNumber = i + 1;

                var itemCode = item.Element("ITEMCODE")?.Value;
                if (string.IsNullOrWhiteSpace(itemCode))
                    errorMessages.Add($"Line {lineNumber}: ITEMCODE is required");

                var qtyStr = item.Element("QTY")?.Value;
                if (!decimal.TryParse(qtyStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var qty) || qty <= 0)
                    errorMessages.Add($"Line {lineNumber}: QTY must be > 0");

                var priceStr = item.Element("PRICE")?.Value;
                if (!decimal.TryParse(priceStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var price) || price < 0)
                    errorMessages.Add($"Line {lineNumber}: PRICE must be >= 0");

                var calculatedAmt = qty * price;
                var amtElement = item.Element("AMT");
                if (amtElement != null)
                    amtElement.Value = calculatedAmt.ToString("F2", CultureInfo.InvariantCulture);
                else
                    item.Add(new XElement("AMT", calculatedAmt.ToString("F2", CultureInfo.InvariantCulture)));

                var taxrElement = item.Element("TAXR");
                var taxrStr = taxrElement?.Value;

                if (string.IsNullOrWhiteSpace(taxrStr) || !decimal.TryParse(taxrStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var taxr))
                {
                    var taxElement = item.Element("TAX")?.Value;
                    bool isVatExempt = string.Equals(taxElement, "0", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(taxElement, "exempt", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(taxElement, "E", StringComparison.OrdinalIgnoreCase);

                    taxr = isVatExempt ? 0m : _settings.VatRate;

                    if (taxrElement != null)
                        taxrElement.Value = taxr.ToString("F4", CultureInfo.InvariantCulture);
                    else
                        item.Add(new XElement("TAXR", taxr.ToString("F4", CultureInfo.InvariantCulture)));
                }
                else if (taxr > 1)
                {
                    taxr /= 100m;
                    taxrElement!.Value = taxr.ToString("F4", CultureInfo.InvariantCulture);
                }

                if (item.Element("TAX") == null)
                    item.Add(new XElement("TAX", "0"));
            }

            if (errorMessages.Count > 0)
            {
                return Error.Validation("Revmax.InvalidItems", string.Join("; ", errorMessages));
            }

            return doc.ToString(SaveOptions.DisableFormatting);
        }
        catch (System.Xml.XmlException ex)
        {
            return Error.Validation("Revmax.InvalidItemsXml", $"Invalid ItemsXml format: {ex.Message}");
        }
    }
}
