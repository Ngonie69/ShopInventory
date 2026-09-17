using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Blazored.LocalStorage;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

/// <summary>The API's list of documents the van sales app created. Transport only.</summary>
public interface IVanSalesDocumentService
{
    Task<VanSalesInvoicesResponse> GetInvoicesAsync(
        VanSalesDocumentFilter filter,
        CancellationToken cancellationToken = default);

    /// <summary>Null when the API has no such invoice.</summary>
    Task<VanSalesInvoiceDetailModel?> GetInvoiceAsync(
        string reference,
        CancellationToken cancellationToken = default);

    Task<VanSalesCreditNotesResponse> GetCreditNotesAsync(
        VanSalesDocumentFilter filter,
        CancellationToken cancellationToken = default);
}

/// <remarks>
/// Throws on a refusal, with the API's own sentence, rather than answering an empty list: an empty van
/// invoice list is a real answer on a quiet day, and one that means "the request failed" must not look like
/// it.
/// </remarks>
public class VanSalesDocumentService(
    HttpClient httpClient,
    ILogger<VanSalesDocumentService> logger,
    ILocalStorageService localStorage,
    CustomAuthStateProvider authStateProvider
) : IVanSalesDocumentService
{
    private const string BaseUrl = "api/van-sales";

    public async Task<VanSalesInvoicesResponse> GetInvoicesAsync(
        VanSalesDocumentFilter filter,
        CancellationToken cancellationToken = default)
    {
        var query = BuildQuery(
            ("fromDate", filter.FromDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("toDate", filter.ToDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("repUserId", filter.RepUserId?.ToString()),
            ("state", filter.State),
            ("search", filter.Search),
            ("page", filter.Page.ToString(CultureInfo.InvariantCulture)),
            ("pageSize", filter.PageSize.ToString(CultureInfo.InvariantCulture)),
            ("channel", filter.Channel));

        using var response = await SendAuthenticatedAsync(
            () => httpClient.GetAsync($"{BaseUrl}/invoices{query}", cancellationToken));

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                await ExtractErrorMessageAsync(response, "The van sales invoices could not be loaded."));
        }

        return await response.Content.ReadFromJsonAsync<VanSalesInvoicesResponse>(cancellationToken)
               ?? new VanSalesInvoicesResponse();
    }

    public async Task<VanSalesInvoiceDetailModel?> GetInvoiceAsync(
        string reference,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthenticatedAsync(
            () => httpClient.GetAsync($"{BaseUrl}/invoices/{Uri.EscapeDataString(reference)}", cancellationToken));

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                await ExtractErrorMessageAsync(response, "The invoice could not be loaded."));
        }

        return await response.Content.ReadFromJsonAsync<VanSalesInvoiceDetailModel>(cancellationToken);
    }

    public async Task<VanSalesCreditNotesResponse> GetCreditNotesAsync(
        VanSalesDocumentFilter filter,
        CancellationToken cancellationToken = default)
    {
        var query = BuildQuery(
            ("fromDate", filter.FromDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("toDate", filter.ToDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("state", filter.State),
            ("search", filter.Search),
            ("page", filter.Page.ToString(CultureInfo.InvariantCulture)),
            ("pageSize", filter.PageSize.ToString(CultureInfo.InvariantCulture)),
            ("origin", filter.Origin),
            ("includeCancelled", filter.IncludeCancelled ? null : "false"));

        using var response = await SendAuthenticatedAsync(
            () => httpClient.GetAsync($"{BaseUrl}/credit-notes{query}", cancellationToken));

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                await ExtractErrorMessageAsync(response, "The van sales credit notes could not be loaded."));
        }

        return await response.Content.ReadFromJsonAsync<VanSalesCreditNotesResponse>(cancellationToken)
               ?? new VanSalesCreditNotesResponse();
    }

    private Task<HttpResponseMessage> SendAuthenticatedAsync(Func<Task<HttpResponseMessage>> sendAsync)
        => ApiTokenAuthentication.SendAsync(httpClient, authStateProvider, localStorage, sendAsync, logger);

    private static string BuildQuery(params (string Name, string? Value)[] parameters)
    {
        var pairs = parameters
            .Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .Select(p => $"{p.Name}={Uri.EscapeDataString(p.Value!)}")
            .ToList();

        return pairs.Count == 0 ? string.Empty : "?" + string.Join("&", pairs);
    }

    private static async Task<string> ExtractErrorMessageAsync(HttpResponseMessage response, string fallback)
    {
        var content = await response.Content.ReadAsStringAsync();

        if (string.IsNullOrWhiteSpace(content))
        {
            return fallback;
        }

        try
        {
            using var document = JsonDocument.Parse(content);

            if (document.RootElement.TryGetProperty("detail", out var detail)
                && detail.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(detail.GetString()))
            {
                return detail.GetString()!;
            }
        }
        catch (JsonException)
        {
            // Not problem details. The fallback still says something.
        }

        return fallback;
    }
}
