using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Data;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.Revmax.Commands.FiscalizeCrossDeviceCreditNote;

public sealed class FiscalizeCrossDeviceCreditNoteHandler(
    HttpClient httpClient,
    IAuditService auditService,
    ILogger<FiscalizeCrossDeviceCreditNoteHandler> logger
) : IRequestHandler<FiscalizeCrossDeviceCreditNoteCommand, ErrorOr<FiscalizeCrossDeviceCreditNoteResult>>
{
    private const decimal DefaultVatRate = 0.155m;

    public async Task<ErrorOr<FiscalizeCrossDeviceCreditNoteResult>> Handle(
        FiscalizeCrossDeviceCreditNoteCommand request,
        CancellationToken cancellationToken)
    {
        var creditNoteNumber = request.CreditNoteNumber.Trim();
        var originalInvoiceNumber = request.OriginalInvoiceNumber.Trim();

        try
        {
            var (creditNote, creditNoteError) = await TryGetAsync<CreditNoteDto>(
                $"api/creditnote/number/{Uri.EscapeDataString(creditNoteNumber)}",
                "credit note",
                cancellationToken);

            if (creditNote is null)
            {
                var failure = creditNoteError ?? $"Credit note {creditNoteNumber} was not found.";
                await TryAuditAsync(creditNoteNumber, failure, false, failure);
                return Errors.Revmax.CreditNoteNotFound(creditNoteNumber);
            }

            if (creditNote.Lines.Count == 0)
            {
                const string failure = "The selected credit note does not have any lines to fiscalize.";
                await TryAuditAsync(creditNoteNumber, failure, false, failure);
                return Errors.Revmax.FiscalizationFailed(failure);
            }

            var originalInvoice = await LoadOriginalInvoiceAsync(creditNote, cancellationToken);
            var fiscalInvoiceNumber = GetFiscalInvoiceNumber(creditNote);
            var payload = new RevmaxTransactExtApiRequest
            {
                InvoiceNumber = fiscalInvoiceNumber,
                OriginalInvoiceNumber = originalInvoiceNumber,
                Currency = NormalizeOrNull(request.Currency) ?? NormalizeOrNull(creditNote.Currency),
                BranchName = NormalizeOrNull(request.BranchName),
                CustomerName = FirstNonEmpty(request.CustomerName, originalInvoice?.CardName, creditNote.CardName),
                CustomerVatNumber = NormalizeOrNull(request.CustomerVatNumber),
                CustomerAddress = NormalizeOrNull(request.CustomerAddress),
                CustomerTelephone = NormalizeOrNull(request.CustomerTelephone),
                CustomerEmail = NormalizeOrNull(request.CustomerEmail),
                CustomerBPN = NormalizeOrNull(request.CustomerBPN),
                InvoiceAmount = Math.Abs(creditNote.DocTotal),
                InvoiceTaxAmount = Math.Abs(creditNote.TaxAmount),
                Istatus = "02",
                Cashier = "Web",
                InvoiceComment = NormalizeOrNull(request.InvoiceComment) ?? FirstNonEmpty(creditNote.Comments, creditNote.Reason),
                ItemsXml = BuildItemsXml(creditNote),
                CurrenciesXml = BuildCurrenciesXml(creditNote),
                refDeviceId = request.RefDeviceId,
                refReceiptGlobalNo = request.RefReceiptGlobalNo,
                refFiscalDayNo = request.RefFiscalDayNo
            };

            using var response = await httpClient.PostAsJsonAsync(
                "api/revmax/transact-ext",
                payload,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                var failure = ExtractFailureMessage(response.StatusCode, responseBody);
                logger.LogWarning(
                    "Cross-device fiscalization failed for credit note {CreditNoteNumber}. Status: {StatusCode}. Body: {Body}",
                    creditNoteNumber,
                    (int)response.StatusCode,
                    responseBody);

                await TryAuditAsync(fiscalInvoiceNumber, failure, false, failure);
                return Errors.Revmax.FiscalizationFailed(failure);
            }

            var result = await response.Content.ReadFromJsonAsync<RevmaxTransactExtResponse>(cancellationToken: cancellationToken);
            if (result is null)
            {
                const string failure = "The REVMax proxy returned an empty fiscalization response.";
                await TryAuditAsync(fiscalInvoiceNumber, failure, false, failure);
                return Errors.Revmax.FiscalizationFailed(failure);
            }

            if (!result.Success)
            {
                var failure = string.IsNullOrWhiteSpace(result.Message)
                    ? "REVMax rejected the cross-device credit note fiscalization request."
                    : result.Message;

                await TryAuditAsync(fiscalInvoiceNumber, failure, false, failure);
                return Errors.Revmax.FiscalizationFailed(failure);
            }

            var successMessage = string.IsNullOrWhiteSpace(result.Message)
                ? $"Credit note {fiscalInvoiceNumber} fiscalized successfully."
                : result.Message;

            await TryAuditAsync(fiscalInvoiceNumber, successMessage, true, null);

            return new FiscalizeCrossDeviceCreditNoteResult
            {
                CreditNoteNumber = creditNote.CreditNoteNumber,
                FiscalInvoiceNumber = fiscalInvoiceNumber,
                OriginalInvoiceNumber = originalInvoiceNumber,
                Response = result
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fiscalize cross-device credit note {CreditNoteNumber}", creditNoteNumber);
            var failure = "Failed to fiscalize the cross-device credit note.";
            await TryAuditAsync(creditNoteNumber, failure, false, ex.Message);
            return Errors.Revmax.FiscalizationFailed(failure);
        }
    }

    private async Task<InvoiceDto?> LoadOriginalInvoiceAsync(CreditNoteDto creditNote, CancellationToken cancellationToken)
    {
        if (creditNote.OriginalInvoiceDocEntry is int originalInvoiceDocEntry)
        {
            var (invoice, _) = await TryGetAsync<InvoiceDto>(
                $"api/invoice/{originalInvoiceDocEntry}",
                "original invoice",
                cancellationToken);
            if (invoice is not null)
            {
                return invoice;
            }
        }

        if (creditNote.OriginalInvoiceId is int originalInvoiceId)
        {
            var (invoice, _) = await TryGetAsync<InvoiceDto>(
                $"api/invoice/{originalInvoiceId}",
                "original invoice",
                cancellationToken);
            return invoice;
        }

        return null;
    }

    private async Task<(T? Value, string? Error)> TryGetAsync<T>(
        string path,
        string entityName,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(path, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return (default, $"The {entityName} was not found.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogWarning(
                "Failed to load {EntityName} from {Path}. Status: {StatusCode}. Body: {Body}",
                entityName,
                path,
                (int)response.StatusCode,
                errorBody);

            return (default, ExtractFailureMessage(response.StatusCode, errorBody));
        }

        var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken);
        return value is null
            ? (default, $"The API returned an empty {entityName} response.")
            : (value, null);
    }

    private async Task TryAuditAsync(string entityId, string details, bool isSuccess, string? errorMessage)
    {
        try
        {
            await auditService.LogAsync(
                AuditActions.FiscalizeCrossDeviceCreditNote,
                "RevmaxCreditNote",
                entityId,
                details,
                isSuccess,
                errorMessage);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to audit cross-device credit note fiscalization for {EntityId}", entityId);
        }
    }

    private static string GetFiscalInvoiceNumber(CreditNoteDto creditNote)
        => creditNote.SAPDocNum?.ToString() ?? creditNote.CreditNoteNumber;

    private static string BuildItemsXml(CreditNoteDto creditNote)
    {
        var items = new XElement("items");

        var lineNumber = 1;
        foreach (var line in creditNote.Lines)
        {
            var quantity = Math.Abs(line.Quantity);
            var price = Math.Abs(line.UnitPrice);
            var amount = quantity * price;

            var item = new XElement("item",
                new XElement("HH", lineNumber.ToString(CultureInfo.InvariantCulture)),
                new XElement("ITEMCODE", line.ItemCode ?? string.Empty),
                new XElement("ITEMNAME1", line.ItemDescription ?? string.Empty),
                new XElement("ITEMNAME2", string.Empty),
                new XElement("QTY", quantity.ToString("F2", CultureInfo.InvariantCulture)),
                new XElement("PRICE", price.ToString("F2", CultureInfo.InvariantCulture)),
                new XElement("AMT", amount.ToString("F2", CultureInfo.InvariantCulture)),
                new XElement("TAX", "0"),
                new XElement("TAXR", NormalizeTaxRate(line.TaxPercent).ToString("F4", CultureInfo.InvariantCulture))
            );

            items.Add(item);
            lineNumber++;
        }

        return items.ToString(SaveOptions.DisableFormatting);
    }

    private static string BuildCurrenciesXml(CreditNoteDto creditNote)
    {
        var rate = creditNote.ExchangeRate <= 0 ? 1m : creditNote.ExchangeRate;
        var currencyName = string.IsNullOrWhiteSpace(creditNote.Currency) ? "USD" : creditNote.Currency.Trim();
        var total = Math.Abs(creditNote.DocTotal);

        var currencies = new XElement("currencies",
            new XElement("currency",
                new XElement("Name", currencyName),
                new XElement("Amount", total.ToString("F2", CultureInfo.InvariantCulture)),
                new XElement("Rate", rate.ToString("F2", CultureInfo.InvariantCulture))
            )
        );

        return currencies.ToString(SaveOptions.DisableFormatting);
    }

    private static decimal NormalizeTaxRate(decimal taxPercent)
    {
        if (taxPercent <= 0)
        {
            return DefaultVatRate;
        }

        return taxPercent > 1 ? taxPercent / 100m : taxPercent;
    }

    private static string ExtractFailureMessage(HttpStatusCode statusCode, string? responseBody)
    {
        if (!string.IsNullOrWhiteSpace(responseBody))
        {
            try
            {
                using var document = JsonDocument.Parse(responseBody);
                var root = document.RootElement;

                if (root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
                {
                    return detail.GetString() ?? $"The API returned {(int)statusCode}.";
                }

                if (root.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                {
                    return title.GetString() ?? $"The API returned {(int)statusCode}.";
                }

                if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString() ?? $"The API returned {(int)statusCode}.";
                }
            }
            catch (JsonException)
            {
                // Ignore parse failures and fall back to the raw response below.
            }

            if (responseBody.Length <= 300)
            {
                return responseBody;
            }
        }

        return $"The API returned {(int)statusCode} while fiscalizing the credit note.";
    }

    private static string? NormalizeOrNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}