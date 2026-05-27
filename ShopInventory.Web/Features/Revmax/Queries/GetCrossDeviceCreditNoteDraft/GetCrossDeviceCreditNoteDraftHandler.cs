using System.Net;
using System.Net.Http.Json;
using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.Revmax.Queries.GetCrossDeviceCreditNoteDraft;

public sealed class GetCrossDeviceCreditNoteDraftHandler(
    HttpClient httpClient,
    ILogger<GetCrossDeviceCreditNoteDraftHandler> logger
) : IRequestHandler<GetCrossDeviceCreditNoteDraftQuery, ErrorOr<GetCrossDeviceCreditNoteDraftResult>>
{
    public async Task<ErrorOr<GetCrossDeviceCreditNoteDraftResult>> Handle(
        GetCrossDeviceCreditNoteDraftQuery request,
        CancellationToken cancellationToken)
    {
        var creditNoteNumber = request.CreditNoteNumber.Trim();

        try
        {
            var (creditNote, creditNoteError) = await TryGetAsync<CreditNoteDto>(
                $"api/creditnote/number/{Uri.EscapeDataString(creditNoteNumber)}",
                "credit note",
                cancellationToken);

            if (creditNote is null)
            {
                if (creditNoteError is null)
                {
                    return Errors.Revmax.CreditNoteNotFound(creditNoteNumber);
                }

                return creditNoteError.Contains("not found", StringComparison.OrdinalIgnoreCase)
                    ? Errors.Revmax.CreditNoteNotFound(creditNoteNumber)
                    : Errors.Revmax.LoadDraftFailed(creditNoteError);
            }

            var originalInvoice = await LoadOriginalInvoiceAsync(creditNote, cancellationToken);
            var sourceInvoiceNumber = ResolveSourceInvoiceNumber(request.OriginalInvoiceNumberOverride, originalInvoice, creditNote);
            var (sourceFiscalInvoice, sourceFiscalInvoiceError) = await LoadSourceFiscalInvoiceAsync(sourceInvoiceNumber, cancellationToken);
            var (currentDevice, currentDeviceError) = await LoadCurrentDeviceAsync(cancellationToken);

            return new GetCrossDeviceCreditNoteDraftResult
            {
                CreditNote = creditNote,
                OriginalInvoice = originalInvoice,
                SourceFiscalInvoice = sourceFiscalInvoice,
                CurrentDevice = currentDevice,
                CreditNoteFiscalInvoiceNumber = GetFiscalInvoiceNumber(creditNote),
                SourceInvoiceNumber = sourceInvoiceNumber,
                SourceFiscalInvoiceError = sourceFiscalInvoiceError,
                CurrentDeviceError = currentDeviceError,
                SuggestedRefDeviceId = ParseInt(sourceFiscalInvoice?.DeviceID),
                SuggestedRefReceiptGlobalNo = sourceFiscalInvoice?.Data?.ReceiptGlobalNo,
                SuggestedRefFiscalDayNo = ParseInt(sourceFiscalInvoice?.FiscalDay),
                SuggestedCurrency = creditNote.Currency ?? originalInvoice?.DocCurrency,
                SuggestedBranchName = null,
                SuggestedCustomerName = originalInvoice?.CardName ?? creditNote.CardName,
                SuggestedCustomerVatNumber = null,
                SuggestedCustomerAddress = null,
                SuggestedCustomerTelephone = null,
                SuggestedCustomerEmail = null,
                SuggestedCustomerBpn = null,
                SuggestedInvoiceComment = FirstNonEmpty(creditNote.Comments, creditNote.Reason)
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to build cross-device credit note draft for {CreditNoteNumber}", creditNoteNumber);
            return Errors.Revmax.LoadDraftFailed("Failed to load the cross-device fiscalization draft.");
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

    private async Task<(RevmaxInvoiceResponse? Value, string? Error)> LoadSourceFiscalInvoiceAsync(
        string? sourceInvoiceNumber,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceInvoiceNumber))
        {
            return (null, "The linked credit note does not expose an original invoice number yet.");
        }

        var (response, error) = await TryGetAsync<RevmaxInvoiceResponse>(
            $"api/revmax/invoices/{Uri.EscapeDataString(sourceInvoiceNumber)}",
            "source fiscal invoice",
            cancellationToken);

        if (response is null)
        {
            return (null, error);
        }

        if (!response.Success || response.Data is null)
        {
            return (null, string.IsNullOrWhiteSpace(response.Message)
                ? $"REVMax could not resolve source invoice {sourceInvoiceNumber}."
                : response.Message);
        }

        return (response, null);
    }

    private async Task<(RevmaxCardDetailsResponse? Value, string? Error)> LoadCurrentDeviceAsync(CancellationToken cancellationToken)
    {
        var (response, error) = await TryGetAsync<RevmaxCardDetailsResponse>(
            "api/revmax/card-details",
            "REVMax device details",
            cancellationToken);

        if (response is null)
        {
            return (null, error);
        }

        if (!string.Equals(response.Code, "1", StringComparison.OrdinalIgnoreCase))
        {
            return (response, string.IsNullOrWhiteSpace(response.Message)
                ? "Unable to load the current REVMax device details."
                : response.Message);
        }

        return (response, null);
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

            var message = string.IsNullOrWhiteSpace(errorBody)
                ? $"The API returned {(int)response.StatusCode} while loading the {entityName}."
                : errorBody;

            return (default, message);
        }

        var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken);
        return value is null
            ? (default, $"The API returned an empty {entityName} response.")
            : (value, null);
    }

    private static string GetFiscalInvoiceNumber(CreditNoteDto creditNote)
        => creditNote.SAPDocNum?.ToString() ?? creditNote.CreditNoteNumber;

    private static string? ResolveSourceInvoiceNumber(
        string? overrideInvoiceNumber,
        InvoiceDto? originalInvoice,
        CreditNoteDto creditNote)
        => FirstNonEmpty(
            overrideInvoiceNumber,
            originalInvoice?.DocNum.ToString(),
            creditNote.OriginalInvoiceSAPDocNum?.ToString());

    private static int? ParseInt(string? value)
        => int.TryParse(value, out var parsed) ? parsed : null;

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}