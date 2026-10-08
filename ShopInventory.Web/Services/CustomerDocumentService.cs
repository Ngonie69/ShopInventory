using System.Globalization;
using System.Net.Http.Json;
using Blazored.LocalStorage;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

/// <summary>
/// Customers' WhatsApp numbers and the documents sent to them, as the API holds them.
/// </summary>
/// <remarks>
/// A thin adapter. Reads of a history return empty on failure so a drawer still renders; everything
/// else throws an <see cref="HttpRequestException"/> carrying the API's own sentence, because the
/// person needs to know their send or their save did not happen — and why.
/// </remarks>
public interface ICustomerDocumentService
{
    Task<List<CustomerWhatsAppContactModel>> GetContactsAsync(string? cardCode, int? routeCustomerId, CancellationToken cancellationToken = default);

    Task<WhatsAppNumberCheckResultModel> CheckPhoneAsync(string phone, string? cardCode, int? routeCustomerId, CancellationToken cancellationToken = default);

    Task<List<CustomerWhatsAppContactModel>> SaveContactAsync(SaveCustomerWhatsAppContactModel request, CancellationToken cancellationToken = default);

    Task<CustomerWhatsAppContactModel> UpdateContactAsync(int contactId, UpdateCustomerWhatsAppContactModel request, CancellationToken cancellationToken = default);

    Task<List<CustomerWhatsAppContactModel>> OptOutContactAsync(int contactId, CancellationToken cancellationToken = default);

    Task<CustomerWhatsAppContactModel> RecheckContactAsync(int contactId, CancellationToken cancellationToken = default);

    Task RemoveContactAsync(int contactId, CancellationToken cancellationToken = default);

    Task<List<CustomerDocumentDeliveryModel>> GetInvoiceDeliveriesAsync(int docEntry, CancellationToken cancellationToken = default);

    Task<List<CustomerDocumentDeliveryModel>> RequestInvoiceAsync(int docEntry, RequestInvoiceWhatsAppModel request, CancellationToken cancellationToken = default);

    Task<InvoiceWhatsAppPreviewModel> PreviewInvoiceAsync(int docEntry, CancellationToken cancellationToken = default);

    Task<CustomerDocumentDeliveryModel> RetryAsync(long deliveryId, bool confirmNotReceived, CancellationToken cancellationToken = default);

    Task<CustomerDocumentDeliveryModel> CancelAsync(long deliveryId, CancellationToken cancellationToken = default);

    Task<CustomerDocumentDeliveryPageModel> GetLogAsync(
        string? status, string? trigger, string? search, DateTime? fromDate, DateTime? toDate, int page, int pageSize,
        CancellationToken cancellationToken = default);

    Task<CustomerDocumentDeliveryStatusModel> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<CustomerDocumentDeliveryStatusModel> UpdateSettingsAsync(UpdateCustomerDocumentDeliverySettingsModel request, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class CustomerDocumentService(
    HttpClient httpClient,
    ILogger<CustomerDocumentService> logger,
    ILocalStorageService localStorage,
    CustomAuthStateProvider authStateProvider) : ICustomerDocumentService
{
    private const string ContactsUrl = "api/customer-whatsapp-contacts";
    private const string DeliveriesUrl = "api/customer-document-deliveries";

    public Task<List<CustomerWhatsAppContactModel>> GetContactsAsync(string? cardCode, int? routeCustomerId, CancellationToken cancellationToken = default) =>
        ReadAsync<List<CustomerWhatsAppContactModel>>(
            ContactsUrl + Query(("cardCode", cardCode), ("routeCustomerId", Number(routeCustomerId))),
            "The customer's WhatsApp numbers could not be loaded.",
            cancellationToken);

    public Task<WhatsAppNumberCheckResultModel> CheckPhoneAsync(string phone, string? cardCode, int? routeCustomerId, CancellationToken cancellationToken = default) =>
        ReadAsync<WhatsAppNumberCheckResultModel>(
            $"{ContactsUrl}/phone-check" + Query(("phone", phone), ("cardCode", cardCode), ("routeCustomerId", Number(routeCustomerId))),
            "The number could not be checked.",
            cancellationToken);

    public Task<List<CustomerWhatsAppContactModel>> SaveContactAsync(SaveCustomerWhatsAppContactModel request, CancellationToken cancellationToken = default) =>
        WriteAsync<List<CustomerWhatsAppContactModel>>(
            () => httpClient.PostAsJsonAsync(ContactsUrl, request, cancellationToken),
            "The WhatsApp number could not be saved.",
            cancellationToken);

    public Task<CustomerWhatsAppContactModel> UpdateContactAsync(int contactId, UpdateCustomerWhatsAppContactModel request, CancellationToken cancellationToken = default) =>
        WriteAsync<CustomerWhatsAppContactModel>(
            () => httpClient.PutAsJsonAsync($"{ContactsUrl}/{contactId}", request, cancellationToken),
            "The WhatsApp number could not be updated.",
            cancellationToken);

    public Task<List<CustomerWhatsAppContactModel>> OptOutContactAsync(int contactId, CancellationToken cancellationToken = default) =>
        WriteAsync<List<CustomerWhatsAppContactModel>>(
            () => httpClient.PostAsync($"{ContactsUrl}/{contactId}/opt-out", content: null, cancellationToken),
            "The number could not be opted out.",
            cancellationToken);

    public Task<CustomerWhatsAppContactModel> RecheckContactAsync(int contactId, CancellationToken cancellationToken = default) =>
        WriteAsync<CustomerWhatsAppContactModel>(
            () => httpClient.PostAsync($"{ContactsUrl}/{contactId}/check", content: null, cancellationToken),
            "WhatsApp could not be asked about the number.",
            cancellationToken);

    public async Task RemoveContactAsync(int contactId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthenticatedAsync(
            () => httpClient.DeleteAsync($"{ContactsUrl}/{contactId}", cancellationToken));

        if (!response.IsSuccessStatusCode)
        {
            throw await RefusalAsync(response, "The number could not be removed.", cancellationToken);
        }
    }

    public async Task<List<CustomerDocumentDeliveryModel>> GetInvoiceDeliveriesAsync(int docEntry, CancellationToken cancellationToken = default)
    {
        try
        {
            return await ReadAsync<List<CustomerDocumentDeliveryModel>>(
                DeliveriesUrl + Query(("sapDocEntry", Number(docEntry))),
                "The invoice's WhatsApp history could not be loaded.",
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not load the WhatsApp history of invoice {DocEntry}", docEntry);
            return [];
        }
    }

    public Task<List<CustomerDocumentDeliveryModel>> RequestInvoiceAsync(int docEntry, RequestInvoiceWhatsAppModel request, CancellationToken cancellationToken = default) =>
        WriteAsync<List<CustomerDocumentDeliveryModel>>(
            () => httpClient.PostAsJsonAsync($"{DeliveriesUrl}/invoices/{docEntry}", request, cancellationToken),
            "The invoice could not be queued for WhatsApp.",
            cancellationToken);

    public Task<InvoiceWhatsAppPreviewModel> PreviewInvoiceAsync(int docEntry, CancellationToken cancellationToken = default) =>
        ReadAsync<InvoiceWhatsAppPreviewModel>(
            $"{DeliveriesUrl}/invoices/{docEntry}/preview",
            "The document could not be prepared for a preview.",
            cancellationToken);

    public Task<CustomerDocumentDeliveryModel> RetryAsync(long deliveryId, bool confirmNotReceived, CancellationToken cancellationToken = default) =>
        WriteAsync<CustomerDocumentDeliveryModel>(
            () => httpClient.PostAsJsonAsync(
                $"{DeliveriesUrl}/{deliveryId}/retry",
                new RetryCustomerDocumentDeliveryModel { ConfirmNotReceived = confirmNotReceived },
                cancellationToken),
            "The document could not be sent again.",
            cancellationToken);

    public Task<CustomerDocumentDeliveryModel> CancelAsync(long deliveryId, CancellationToken cancellationToken = default) =>
        WriteAsync<CustomerDocumentDeliveryModel>(
            () => httpClient.PostAsync($"{DeliveriesUrl}/{deliveryId}/cancel", content: null, cancellationToken),
            "The send could not be withdrawn.",
            cancellationToken);

    public Task<CustomerDocumentDeliveryPageModel> GetLogAsync(
        string? status, string? trigger, string? search, DateTime? fromDate, DateTime? toDate, int page, int pageSize,
        CancellationToken cancellationToken = default) =>
        ReadAsync<CustomerDocumentDeliveryPageModel>(
            $"{DeliveriesUrl}/log" + Query(
                ("status", status),
                ("trigger", trigger),
                ("search", search),
                ("fromDate", fromDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                ("toDate", toDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                ("page", Number(page)),
                ("pageSize", Number(pageSize))),
            "The delivery log could not be loaded.",
            cancellationToken);

    public Task<CustomerDocumentDeliveryStatusModel> GetStatusAsync(CancellationToken cancellationToken = default) =>
        ReadAsync<CustomerDocumentDeliveryStatusModel>(
            $"{DeliveriesUrl}/status",
            "The WhatsApp delivery status could not be loaded.",
            cancellationToken);

    public Task<CustomerDocumentDeliveryStatusModel> UpdateSettingsAsync(UpdateCustomerDocumentDeliverySettingsModel request, CancellationToken cancellationToken = default) =>
        WriteAsync<CustomerDocumentDeliveryStatusModel>(
            () => httpClient.PutAsJsonAsync($"{DeliveriesUrl}/settings", request, cancellationToken),
            "The settings could not be saved.",
            cancellationToken);

    private async Task<T> ReadAsync<T>(string url, string fallback, CancellationToken cancellationToken)
    {
        using var response = await SendAuthenticatedAsync(() => httpClient.GetAsync(url, cancellationToken));

        if (!response.IsSuccessStatusCode)
        {
            throw await RefusalAsync(response, fallback, cancellationToken);
        }

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
               ?? throw new HttpRequestException(fallback);
    }

    private async Task<T> WriteAsync<T>(Func<Task<HttpResponseMessage>> send, string fallback, CancellationToken cancellationToken)
    {
        using var response = await SendAuthenticatedAsync(send);

        if (!response.IsSuccessStatusCode)
        {
            throw await RefusalAsync(response, fallback, cancellationToken);
        }

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
               ?? throw new HttpRequestException(fallback);
    }

    private async Task<HttpRequestException> RefusalAsync(HttpResponseMessage response, string fallback, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        logger.LogWarning(
            "Customer documents call {Method} {Path} refused: {StatusCode} {Body}",
            response.RequestMessage?.Method,
            response.RequestMessage?.RequestUri?.AbsolutePath,
            (int)response.StatusCode,
            ApiErrorResponse.SanitizeForLog(body));

        return ApiErrorResponse.CreateHttpRequestException(response.StatusCode, body, fallback);
    }

    private Task<HttpResponseMessage> SendAuthenticatedAsync(Func<Task<HttpResponseMessage>> send)
        => ApiTokenAuthentication.SendAsync(httpClient, authStateProvider, localStorage, send, logger);

    private static string? Number(int? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static string Query(params (string Name, string? Value)[] parameters)
    {
        var pairs = parameters
            .Where(parameter => !string.IsNullOrWhiteSpace(parameter.Value))
            .Select(parameter => $"{parameter.Name}={Uri.EscapeDataString(parameter.Value!.Trim())}")
            .ToList();

        return pairs.Count == 0 ? string.Empty : "?" + string.Join("&", pairs);
    }
}
