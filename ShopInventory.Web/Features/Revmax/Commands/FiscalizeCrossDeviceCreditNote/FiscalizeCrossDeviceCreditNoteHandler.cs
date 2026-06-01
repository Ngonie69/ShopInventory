using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

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
            var currency = NormalizeOrNull(request.Currency) ?? NormalizeOrNull(creditNote.Currency);
            var payload = new RevmaxTransactExtApiRequest
            {
                InvoiceNumber = fiscalInvoiceNumber,
                OriginalInvoiceNumber = originalInvoiceNumber,
                Currency = currency,
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
                Cashier = NormalizeOrNull(creditNote.CardCode),
                InvoiceComment = NormalizeOrNull(request.InvoiceComment) ?? FirstNonEmpty(creditNote.Comments, creditNote.Reason),
                ItemsXml = BuildItemsXml(creditNote),
                CurrenciesXml = BuildCurrenciesXml(creditNote, currency),
                refDeviceId = request.RefDeviceId,
                refReceiptGlobalNo = request.RefReceiptGlobalNo,
                refFiscalDayNo = request.RefFiscalDayNo
            };

            using var response = await httpClient.PostAsJsonAsync(
                "api/revmax/transact-ext",
                payload,
                JsonOptions,
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
        var originalInvoiceDocEntry = creditNote.OriginalInvoiceDocEntry ?? creditNote.OriginalInvoiceSAPDocEntry;
        if (originalInvoiceDocEntry is int docEntry)
        {
            var (invoice, _) = await TryGetAsync<InvoiceDto>(
                $"api/invoice/{docEntry}",
                "original invoice",
                cancellationToken);
            if (invoice is not null)
            {
                return invoice;
            }
        }

        if (creditNote.OriginalInvoiceSAPDocNum is int originalInvoiceSapDocNum)
        {
            var (invoice, _) = await TryGetAsync<InvoiceDto>(
                $"api/invoice/by-docnum/{originalInvoiceSapDocNum}",
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

    internal static List<object> BuildItemsXml(CreditNoteDto creditNote)
    {
        if (creditNote.Lines.Count == 0)
        {
            return new List<object>();
        }

        var items = new List<object>(creditNote.Lines.Count);

        foreach (var line in creditNote.Lines)
        {
            var quantity = Math.Abs(line.Quantity);
            var price = GetPriceAfterVat(line);
            var amount = GetLineAmount(line, quantity, price);
            var itemDescription = line.ItemDescription ?? string.Empty;
            var taxCode = NormalizeTaxCode(line.TaxCode);
            var taxId = taxCode is null ? ResolveTaxId(line.TaxPercent) : ResolveTaxId(taxCode);
            var taxRate = taxCode is null ? GetTaxRateString(line.TaxPercent) : GetTaxRateString(taxCode);

            items.Add(new
            {
                HH = line.LineNum.ToString(CultureInfo.InvariantCulture),
                ItemCode = line.ItemCode ?? string.Empty,
                ItemName1 = itemDescription,
                ItemName2 = itemDescription,
                Qty = quantity.ToString(CultureInfo.InvariantCulture),
                Price = price.ToString(CultureInfo.InvariantCulture),
                Amt = amount.ToString(CultureInfo.InvariantCulture),
                Tax = taxId.ToString(CultureInfo.InvariantCulture),
                TaxR = taxRate
            });
        }

        return items;
    }

    internal static List<object> BuildCurrenciesXml(CreditNoteDto creditNote, string? currency)
    {
        var currencyName = string.IsNullOrWhiteSpace(currency) ? "USD" : currency.Trim();
        var total = Math.Abs(creditNote.DocTotal);

        return new List<object>
        {
            new
            {
                Name = currencyName,
                Amount = total.ToString(CultureInfo.InvariantCulture),
                Rate = "1"
            }
        };
    }

    private static decimal GetPriceAfterVat(CreditNoteLineDto line)
        => Math.Abs(line.UnitPrice);

    private static decimal GetLineAmount(CreditNoteLineDto line, decimal quantity, decimal price)
    {
        var lineTotal = Math.Abs(line.LineTotal);
        return lineTotal > 0m ? lineTotal : quantity * price;
    }

    private static int ResolveTaxId(decimal taxPercent)
        => taxPercent > 0m ? 1 : 2;

    private static string GetTaxRateString(decimal taxPercent)
    {
        if (taxPercent <= 0m)
        {
            return "0";
        }

        var normalizedTaxRate = taxPercent > 1m ? taxPercent / 100m : taxPercent;
        return normalizedTaxRate.ToString(CultureInfo.InvariantCulture);
    }

    private static string? NormalizeTaxCode(string? taxCode)
        => string.IsNullOrWhiteSpace(taxCode) ? null : taxCode.Trim().ToUpperInvariant();

    private static int ResolveTaxId(string taxCode)
        => taxCode switch
        {
            "A1" or "X1" => 1,
            "B1" or "X0" => 2,
            "C1" => 3,
            "E1" => 5,
            _ => 1
        };

    private static string GetTaxRateString(string taxCode)
        => taxCode switch
        {
            "A1" or "X1" => "0.155",
            "B1" or "X0" or "C1" or "E1" => "0",
            _ => "0.155"
        };

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