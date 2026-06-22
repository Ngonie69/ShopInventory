using ShopInventory.Web.Models;
using System.Net.Http.Json;

namespace ShopInventory.Web.Services;

public interface ICreditNoteService
{
    Task<CreditNoteListResponse?> GetCreditNotesAsync(int page = 1, int pageSize = 20, CreditNoteStatus? status = null, string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null);
    Task<CreditNoteDto?> GetCreditNoteByIdAsync(int id);
    Task<CreditNoteDto?> GetCreditNoteByNumberAsync(string creditNoteNumber);
    Task<CreditNotesByInvoiceResponse?> GetCreditNotesForInvoiceAsync(int invoiceId);
    Task<CreditNoteDto?> CreateCreditNoteAsync(CreateCreditNoteRequest request);
    Task<CreateCreditNoteResult> CreateFromInvoiceAsync(int invoiceId, List<CreateCreditNoteLineRequest> lines, string reason, string? clientRequestId = null);
    Task<CreditNoteDto?> ApproveAsync(int id);
    Task<bool> DeleteCreditNoteAsync(int id);
}

public class CreditNoteService : ICreditNoteService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<CreditNoteService> _logger;

    public CreditNoteService(HttpClient httpClient, ILogger<CreditNoteService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<CreditNoteListResponse?> GetCreditNotesAsync(int page = 1, int pageSize = 20,
        CreditNoteStatus? status = null, string? cardCode = null, DateTime? fromDate = null, DateTime? toDate = null)
    {
        try
        {
            var queryParams = new List<string> { $"page={page}", $"pageSize={pageSize}" };

            if (status.HasValue)
                queryParams.Add($"status={(int)status.Value}");
            if (!string.IsNullOrEmpty(cardCode))
                queryParams.Add($"cardCode={Uri.EscapeDataString(cardCode)}");
            if (fromDate.HasValue)
                queryParams.Add($"fromDate={fromDate.Value:yyyy-MM-dd}");
            if (toDate.HasValue)
                queryParams.Add($"toDate={toDate.Value:yyyy-MM-dd}");

            var url = $"api/creditnote?{string.Join("&", queryParams)}";
            return await _httpClient.GetFromJsonAsync<CreditNoteListResponse>(url);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching credit notes");
            return null;
        }
    }

    public async Task<CreditNoteDto?> GetCreditNoteByIdAsync(int id)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<CreditNoteDto>($"api/creditnote/{id}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching credit note {Id}", id);
            return null;
        }
    }

    public async Task<CreditNoteDto?> GetCreditNoteByNumberAsync(string creditNoteNumber)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<CreditNoteDto>($"api/creditnote/number/{Uri.EscapeDataString(creditNoteNumber)}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching credit note by number {CreditNoteNumber}", creditNoteNumber);
            return null;
        }
    }

    public async Task<CreditNotesByInvoiceResponse?> GetCreditNotesForInvoiceAsync(int invoiceId)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<CreditNotesByInvoiceResponse>($"api/creditnote/by-invoice/{invoiceId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching credit notes for invoice {InvoiceId}", invoiceId);
            return null;
        }
    }

    public async Task<CreditNoteDto?> CreateCreditNoteAsync(CreateCreditNoteRequest request)
    {
        try
        {
            var clientRequestId = EnsureClientRequestId(request);

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "api/creditnote")
            {
                Content = JsonContent.Create(request)
            };
            httpRequest.Headers.Add("Idempotency-Key", clientRequestId);

            var response = await _httpClient.SendAsync(httpRequest);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<CreditNoteDto>();
            }
            var errorBody = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Failed to create credit note: {StatusCode} - {Error}", response.StatusCode, errorBody);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating credit note");
            return null;
        }
    }

    private static string EnsureClientRequestId(CreateCreditNoteRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ClientRequestId))
        {
            request.ClientRequestId = request.ClientRequestId.Trim();
            return request.ClientRequestId;
        }

        request.ClientRequestId = Guid.NewGuid().ToString("N");
        return request.ClientRequestId;
    }

    public async Task<CreateCreditNoteResult> CreateFromInvoiceAsync(int invoiceId, List<CreateCreditNoteLineRequest> lines, string reason, string? clientRequestId = null)
    {
        try
        {
            var key = string.IsNullOrWhiteSpace(clientRequestId) ? Guid.NewGuid().ToString("N") : clientRequestId.Trim();

            // API expects Reason, Lines and ClientRequestId in the body (InvoiceId is in the URL)
            var request = new { Lines = lines, Reason = reason, ClientRequestId = key };

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"api/creditnote/from-invoice/{invoiceId}")
            {
                Content = JsonContent.Create(request)
            };
            httpRequest.Headers.Add("Idempotency-Key", key);

            var response = await _httpClient.SendAsync(httpRequest);
            if (response.IsSuccessStatusCode)
            {
                var creditNote = await response.Content.ReadFromJsonAsync<CreditNoteDto>();
                return new CreateCreditNoteResult { Success = true, CreditNote = creditNote };
            }
            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Failed to create credit note from invoice {InvoiceId}: {StatusCode} - {Error}", invoiceId, response.StatusCode, errorContent);

            // Try to parse error message from response
            string errorMessage = "Failed to create credit note.";
            try
            {
                var jsonOptions = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var errorResponse = System.Text.Json.JsonSerializer.Deserialize<ErrorResponse>(errorContent, jsonOptions);
                if (!string.IsNullOrEmpty(errorResponse?.Message))
                {
                    errorMessage = errorResponse.Message;
                }
            }
            catch
            {
                // If parsing fails, use the raw content if it's readable
                if (!string.IsNullOrWhiteSpace(errorContent) && errorContent.Length < 200)
                {
                    errorMessage = errorContent;
                }
            }

            return new CreateCreditNoteResult { Success = false, ErrorMessage = errorMessage };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating credit note from invoice {InvoiceId}", invoiceId);
            return new CreateCreditNoteResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    private class ErrorResponse
    {
        public string? Message { get; set; }
    }

    public async Task<CreditNoteDto?> ApproveAsync(int id)
    {
        try
        {
            var response = await _httpClient.PostAsync($"api/creditnote/{id}/approve", null);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<CreditNoteDto>();
            }
            _logger.LogWarning("Failed to approve credit note {Id}: {StatusCode}", id, response.StatusCode);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error approving credit note {Id}", id);
            return null;
        }
    }

    public async Task<bool> DeleteCreditNoteAsync(int id)
    {
        try
        {
            var response = await _httpClient.DeleteAsync($"api/creditnote/{id}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting credit note {Id}", id);
            return false;
        }
    }
}
