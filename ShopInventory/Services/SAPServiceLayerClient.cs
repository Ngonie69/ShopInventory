using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Models;

namespace ShopInventory.Services;

public class SAPServiceLayerClient : ISAPServiceLayerClient
{
    private readonly HttpClient _httpClient;
    private readonly SAPSettings _settings;
    private readonly ILogger<SAPServiceLayerClient> _logger;
    private string? _sessionId;
    private DateTime _sessionExpiry = DateTime.MinValue;

    public SAPServiceLayerClient(
        HttpClient httpClient,
        IOptions<SAPSettings> settings,
        ILogger<SAPServiceLayerClient> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;
    }

    private async Task EnsureAuthenticatedAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_sessionId) && DateTime.UtcNow < _sessionExpiry)
        {
            return;
        }

        await LoginAsync(cancellationToken);
    }

    private async Task LoginAsync(CancellationToken cancellationToken)
    {
        var loginRequest = new LoginRequest
        {
            CompanyDB = _settings.CompanyDB,
            UserName = _settings.Username,
            Password = _settings.Password
        };

        var json = JsonSerializer.Serialize(loginRequest);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync("Login", content, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("SAP Login failed: {StatusCode} - {Error}", response.StatusCode, errorContent);
            throw new Exception($"SAP Login failed: {response.StatusCode} - {errorContent}");
        }

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        var loginResponse = JsonSerializer.Deserialize<LoginResponse>(responseContent);

        _sessionId = loginResponse?.SessionId;
        _sessionExpiry = DateTime.UtcNow.AddMinutes(25); // SAP session typically lasts 30 mins

        // Extract session cookie from response
        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            var sessionCookie = cookies.FirstOrDefault(c => c.Contains("B1SESSION"));
            if (sessionCookie != null)
            {
                _logger.LogInformation("SAP Login successful, session established");
            }
        }
    }

    public async Task<List<InventoryTransfer>> GetInventoryTransfersToWarehouseAsync(
        string warehouseCode,
        CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        // Filter inventory transfers where ToWarehouse equals the specified warehouse
        var filter = $"$filter=ToWarehouse eq '{warehouseCode}'&$orderby=DocEntry desc";
        var url = $"StockTransfers?{filter}";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            // Session expired, re-login and retry
            _sessionId = null;
            await EnsureAuthenticatedAsync(cancellationToken);

            request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            response = await _httpClient.SendAsync(request, cancellationToken);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Failed to get inventory transfers: {StatusCode} - {Error}", response.StatusCode, errorContent);
            throw new Exception($"Failed to get inventory transfers: {response.StatusCode} - {errorContent}");
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<SAPResponse<InventoryTransfer>>(content);

        return result?.Value ?? new List<InventoryTransfer>();
    }

    public async Task<List<InventoryTransfer>> GetPagedInventoryTransfersToWarehouseAsync(
        string warehouseCode,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var skip = (page - 1) * pageSize;
        var filter = $"$filter=ToWarehouse eq '{warehouseCode}'&$orderby=DocEntry desc&$top={pageSize}&$skip={skip}";
        var url = $"StockTransfers?{filter}";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _sessionId = null;
            await EnsureAuthenticatedAsync(cancellationToken);

            request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            response = await _httpClient.SendAsync(request, cancellationToken);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Failed to get inventory transfers: {StatusCode} - {Error}", response.StatusCode, errorContent);
            throw new Exception($"Failed to get inventory transfers: {response.StatusCode} - {errorContent}");
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<SAPResponse<InventoryTransfer>>(content);

        return result?.Value ?? new List<InventoryTransfer>();
    }

    public async Task<List<InventoryTransfer>> GetInventoryTransfersByDateAsync(
        string warehouseCode,
        DateTime date,
        CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var dateStr = date.ToString("yyyy-MM-dd");
        var filter = $"$filter=ToWarehouse eq '{warehouseCode}' and DocDate eq '{dateStr}'&$orderby=DocEntry desc";
        var url = $"StockTransfers?{filter}";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _sessionId = null;
            await EnsureAuthenticatedAsync(cancellationToken);

            request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            response = await _httpClient.SendAsync(request, cancellationToken);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Failed to get inventory transfers by date: {StatusCode} - {Error}", response.StatusCode, errorContent);
            throw new Exception($"Failed to get inventory transfers: {response.StatusCode} - {errorContent}");
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<SAPResponse<InventoryTransfer>>(content);

        return result?.Value ?? new List<InventoryTransfer>();
    }

    public async Task<List<InventoryTransfer>> GetInventoryTransfersByDateRangeAsync(
        string warehouseCode,
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var fromDateStr = fromDate.ToString("yyyy-MM-dd");
        var toDateStr = toDate.ToString("yyyy-MM-dd");
        var filter = $"$filter=ToWarehouse eq '{warehouseCode}' and DocDate ge '{fromDateStr}' and DocDate le '{toDateStr}'&$orderby=DocEntry desc";
        var url = $"StockTransfers?{filter}";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _sessionId = null;
            await EnsureAuthenticatedAsync(cancellationToken);

            request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            response = await _httpClient.SendAsync(request, cancellationToken);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Failed to get inventory transfers by date range: {StatusCode} - {Error}", response.StatusCode, errorContent);
            throw new Exception($"Failed to get inventory transfers: {response.StatusCode} - {errorContent}");
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<SAPResponse<InventoryTransfer>>(content);

        return result?.Value ?? new List<InventoryTransfer>();
    }

    public async Task<InventoryTransfer?> GetInventoryTransferByDocEntryAsync(
        int docEntry,
        CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var url = $"StockTransfers({docEntry})";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _sessionId = null;
            await EnsureAuthenticatedAsync(cancellationToken);

            request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            response = await _httpClient.SendAsync(request, cancellationToken);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Failed to get inventory transfer {DocEntry}: {StatusCode} - {Error}", docEntry, response.StatusCode, errorContent);
            throw new Exception($"Failed to get inventory transfer: {response.StatusCode} - {errorContent}");
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<InventoryTransfer>(content);
    }

    #region Invoice Operations

    public async Task<Invoice> CreateInvoiceAsync(
        CreateInvoiceRequest request,
        CancellationToken cancellationToken = default)
    {
        // Validate request
        ValidateInvoiceRequest(request);

        await EnsureAuthenticatedAsync(cancellationToken);

        // Build the invoice payload for SAP
        // Note: DocCurrency should only be sent if explicitly specified and valid in SAP
        // If null, empty, or "USD" (which may not be configured), omit it to use local currency
        var docCurrency = !string.IsNullOrWhiteSpace(request.DocCurrency) && request.DocCurrency != "USD"
            ? request.DocCurrency
            : null;

        var invoicePayload = new
        {
            CardCode = request.CardCode,
            DocDate = request.DocDate ?? DateTime.Now.ToString("yyyy-MM-dd"),
            DocDueDate = request.DocDueDate ?? DateTime.Now.ToString("yyyy-MM-dd"),
            NumAtCard = request.NumAtCard,
            Comments = request.Comments,
            DocCurrency = docCurrency,
            SalesPersonCode = request.SalesPersonCode,
            DocumentLines = request.Lines?.Select((line, index) => new
            {
                LineNum = index,
                ItemCode = line.ItemCode,
                Quantity = line.Quantity,
                UnitPrice = line.UnitPrice,
                WarehouseCode = line.WarehouseCode,
                TaxCode = line.TaxCode,
                DiscountPercent = line.DiscountPercent,
                UoMCode = line.UoMCode,
                AccountCode = line.AccountCode,
                BatchNumbers = line.BatchNumbers?.Select(b => new
                {
                    BatchNumber = b.BatchNumber,
                    Quantity = b.Quantity
                }).ToList()
            }).ToList()
        };

        var json = JsonSerializer.Serialize(invoicePayload, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        var httpContent = new StringContent(json, Encoding.UTF8, "application/json");

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "Invoices");
        httpRequest.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        httpRequest.Content = httpContent;

        var response = await _httpClient.SendAsync(httpRequest, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _sessionId = null;
            await EnsureAuthenticatedAsync(cancellationToken);

            httpRequest = new HttpRequestMessage(HttpMethod.Post, "Invoices");
            httpRequest.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
            httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");
            response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Failed to create invoice: {StatusCode} - {Error}", response.StatusCode, errorContent);
            throw new Exception($"Failed to create invoice: {response.StatusCode} - {errorContent}");
        }

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        var invoice = JsonSerializer.Deserialize<Invoice>(responseContent);

        _logger.LogInformation("Invoice created successfully with DocEntry: {DocEntry}", invoice?.DocEntry);

        return invoice ?? throw new Exception("Failed to deserialize created invoice");
    }

    private void ValidateInvoiceRequest(CreateInvoiceRequest request)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(request.CardCode))
        {
            errors.Add("Customer code (CardCode) is required");
        }

        if (request.Lines == null || request.Lines.Count == 0)
        {
            errors.Add("At least one line item is required");
        }
        else
        {
            for (int i = 0; i < request.Lines.Count; i++)
            {
                var line = request.Lines[i];

                if (string.IsNullOrWhiteSpace(line.ItemCode))
                {
                    errors.Add($"Line {i + 1}: Item code is required");
                }

                if (line.Quantity <= 0)
                {
                    errors.Add($"Line {i + 1}: Quantity must be greater than zero (current: {line.Quantity})");
                }

                if (line.UnitPrice.HasValue && line.UnitPrice.Value < 0)
                {
                    errors.Add($"Line {i + 1}: Unit price cannot be negative (current: {line.UnitPrice})");
                }

                if (line.DiscountPercent.HasValue && (line.DiscountPercent.Value < 0 || line.DiscountPercent.Value > 100))
                {
                    errors.Add($"Line {i + 1}: Discount percent must be between 0 and 100 (current: {line.DiscountPercent})");
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join("; ", errors));
        }
    }

    public async Task<Invoice?> GetInvoiceByDocEntryAsync(
        int docEntry,
        CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var url = $"Invoices({docEntry})";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _sessionId = null;
            await EnsureAuthenticatedAsync(cancellationToken);

            request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            response = await _httpClient.SendAsync(request, cancellationToken);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Failed to get invoice {DocEntry}: {StatusCode} - {Error}", docEntry, response.StatusCode, errorContent);
            throw new Exception($"Failed to get invoice: {response.StatusCode} - {errorContent}");
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<Invoice>(content);
    }

    public async Task<List<Invoice>> GetInvoicesByCustomerAsync(
        string cardCode,
        CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var filter = $"$filter=CardCode eq '{cardCode}'&$orderby=DocEntry desc";
        var url = $"Invoices?{filter}";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _sessionId = null;
            await EnsureAuthenticatedAsync(cancellationToken);

            request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            response = await _httpClient.SendAsync(request, cancellationToken);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Failed to get invoices for customer {CardCode}: {StatusCode} - {Error}", cardCode, response.StatusCode, errorContent);
            throw new Exception($"Failed to get invoices: {response.StatusCode} - {errorContent}");
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<SAPResponse<Invoice>>(content);

        return result?.Value ?? new List<Invoice>();
    }

    public async Task<List<Invoice>> GetInvoicesByDateRangeAsync(
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var fromDateStr = fromDate.ToString("yyyy-MM-dd");
        var toDateStr = toDate.ToString("yyyy-MM-dd");
        var filter = $"$filter=DocDate ge '{fromDateStr}' and DocDate le '{toDateStr}'&$orderby=DocEntry desc";
        var url = $"Invoices?{filter}";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _sessionId = null;
            await EnsureAuthenticatedAsync(cancellationToken);

            request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            response = await _httpClient.SendAsync(request, cancellationToken);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Failed to get invoices by date range: {StatusCode} - {Error}", response.StatusCode, errorContent);
            throw new Exception($"Failed to get invoices: {response.StatusCode} - {errorContent}");
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<SAPResponse<Invoice>>(content);

        return result?.Value ?? new List<Invoice>();
    }

    public async Task<List<Invoice>> GetPagedInvoicesAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var skip = (page - 1) * pageSize;
        var filter = $"$orderby=DocEntry desc&$top={pageSize}&$skip={skip}";
        var url = $"Invoices?{filter}";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _sessionId = null;
            await EnsureAuthenticatedAsync(cancellationToken);

            request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            response = await _httpClient.SendAsync(request, cancellationToken);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Failed to get paged invoices: {StatusCode} - {Error}", response.StatusCode, errorContent);
            throw new Exception($"Failed to get invoices: {response.StatusCode} - {errorContent}");
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<SAPResponse<Invoice>>(content);

        return result?.Value ?? new List<Invoice>();
    }

    #endregion

    #region Product/Item Operations

    public async Task<List<Item>> GetAllItemsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var items = new List<Item>();
        int skip = 0;
        const int pageSize = 100;
        bool hasMore = true;

        while (hasMore)
        {
            // Get items that are inventory items (sales items)
            var endpoint = $"Items?$select=ItemCode,ItemName,BarCode,ItemType,ManageBatchNumbers,DefaultWarehouse&$filter=ItemType eq 'itItems' and Valid eq 'tYES'&$orderby=ItemCode&$top={pageSize}&$skip={skip}";

            var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _sessionId = null;
                await EnsureAuthenticatedAsync(cancellationToken);

                request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                response = await _httpClient.SendAsync(request, cancellationToken);
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Failed to get items: {StatusCode} - {Error}", response.StatusCode, errorContent);
                throw new Exception($"Failed to get items: {response.StatusCode} - {errorContent}");
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            // Parse response and check for nextLink
            using var doc = JsonDocument.Parse(content);
            var valueArray = doc.RootElement.GetProperty("value");
            var pageItems = JsonSerializer.Deserialize<List<Item>>(valueArray.GetRawText()) ?? new List<Item>();

            if (pageItems.Count == 0)
            {
                hasMore = false;
            }
            else
            {
                items.AddRange(pageItems);
                _logger.LogDebug("Retrieved {PageCount} items at skip={Skip}, total: {Total}", pageItems.Count, skip, items.Count);
                skip += pageItems.Count;

                // Check for odata.nextLink to determine if there are more pages
                hasMore = doc.RootElement.TryGetProperty("odata.nextLink", out _) ||
                          doc.RootElement.TryGetProperty("@odata.nextLink", out _) ||
                          pageItems.Count == pageSize;
            }
        }

        _logger.LogInformation("Retrieved {Count} items from SAP", items.Count);
        return items;
    }

    public async Task<List<Item>> GetItemsInWarehouseAsync(
        string warehouseCode,
        CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        // First get all batch numbers in the warehouse to identify which items have stock
        var batches = await GetAllBatchNumbersInWarehouseAsync(warehouseCode, cancellationToken);

        // Get unique item codes from batches
        var itemCodes = batches.Select(b => b.ItemCode).Where(c => !string.IsNullOrEmpty(c)).Distinct().ToList();

        // Fetch item details for each unique item code
        var items = new List<Item>();
        foreach (var itemCode in itemCodes)
        {
            var item = await GetItemByCodeAsync(itemCode!, cancellationToken);
            if (item != null)
            {
                items.Add(item);
            }
        }

        return items.OrderBy(i => i.ItemCode).ToList();
    }

    public async Task<List<Item>> GetPagedItemsInWarehouseAsync(
        string warehouseCode,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        // Get all items first (we need to know the full list to paginate)
        var allItems = await GetItemsInWarehouseAsync(warehouseCode, cancellationToken);

        // Apply pagination
        var skip = (page - 1) * pageSize;
        return allItems.Skip(skip).Take(pageSize).ToList();
    }

    public async Task<Item?> GetItemByCodeAsync(
        string itemCode,
        CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var url = $"Items('{itemCode}')";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _sessionId = null;
            await EnsureAuthenticatedAsync(cancellationToken);

            request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            response = await _httpClient.SendAsync(request, cancellationToken);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Failed to get item {ItemCode}: {StatusCode} - {Error}", itemCode, response.StatusCode, errorContent);
            throw new Exception($"Failed to get item: {response.StatusCode} - {errorContent}");
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<Item>(content);
    }

    public async Task<List<BatchNumber>> GetBatchNumbersForItemInWarehouseAsync(
        string itemCode,
        string warehouseCode,
        CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var queryCode = $"ITEM_BATCHES_{itemCode.Replace("-", "_").ToUpperInvariant()}_{warehouseCode.Replace("-", "_").ToUpperInvariant()}";

        // Try to delete existing query first
        await TryDeleteQueryAsync(queryCode, cancellationToken);

        // SQL query to get batch numbers for a specific item in a warehouse
        var sqlText = $"SELECT T0.\"ItemCode\", T0.\"DistNumber\" as \"BatchNum\", T1.\"Quantity\", T1.\"WhsCode\", " +
                      $"T0.\"ExpDate\", T0.\"MnfDate\", T0.\"InDate\", T0.\"Notes\" " +
                      $"FROM OBTN T0 INNER JOIN OBTQ T1 ON T0.\"AbsEntry\" = T1.\"MdAbsEntry\" " +
                      $"WHERE T0.\"ItemCode\" = '{itemCode}' AND T1.\"WhsCode\" = '{warehouseCode}' AND T1.\"Quantity\" > 0 " +
                      $"ORDER BY T0.\"DistNumber\"";

        try
        {
            // Create the SQL query
            await CreateSqlQueryAsync(queryCode, $"Batches for {itemCode} in {warehouseCode}", sqlText, cancellationToken);

            // Execute the query
            var url = $"SQLQueries('{queryCode}')/List";

            var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
            httpRequest.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
            httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var response = await _httpClient.SendAsync(httpRequest, cancellationToken);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _sessionId = null;
                await EnsureAuthenticatedAsync(cancellationToken);

                httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
                httpRequest.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
                httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            }

            // Handle 404 as no results found (empty batch list)
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogInformation("No batch numbers found for item {ItemCode} in warehouse {Warehouse}", itemCode, warehouseCode);
                return new List<BatchNumber>();
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("SQL query for batch numbers failed: {StatusCode} - {Error}, falling back to BatchNumberDetails",
                    response.StatusCode, errorContent);
                return await GetBatchNumbersForItemFallbackAsync(itemCode, warehouseCode, cancellationToken);
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseBatchNumbersFromSqlResult(content, warehouseCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SQL query for batch numbers failed, falling back to BatchNumberDetails");
            return await GetBatchNumbersForItemFallbackAsync(itemCode, warehouseCode, cancellationToken);
        }
    }

    private async Task<List<BatchNumber>> GetBatchNumbersForItemFallbackAsync(
        string itemCode,
        string warehouseCode,
        CancellationToken cancellationToken)
    {
        // Fallback: Query all batch numbers for the item and filter client-side
        // Note: BatchNumberDetails uses 'Batch' property, not 'BatchNum'
        var filter = Uri.EscapeDataString($"ItemCode eq '{itemCode}'");
        var url = $"BatchNumberDetails?$filter={filter}";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Failed to get batch numbers for item {ItemCode}: {StatusCode} - {Error}",
                itemCode, response.StatusCode, errorContent);
            throw new Exception($"Failed to get batch numbers: {response.StatusCode} - {errorContent}");
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<SAPResponse<BatchNumber>>(content);

        // Note: Without warehouse filtering at API level, we return all batches for the item
        // The batch details from BatchNumberDetails don't include warehouse, so we can't filter here
        return result?.Value ?? new List<BatchNumber>();
    }

    private List<BatchNumber> ParseBatchNumbersFromSqlResult(string jsonContent, string warehouseCode)
    {
        var batches = new List<BatchNumber>();
        try
        {
            using var doc = JsonDocument.Parse(jsonContent);
            if (doc.RootElement.TryGetProperty("value", out var valueArray))
            {
                foreach (var item in valueArray.EnumerateArray())
                {
                    var batch = new BatchNumber
                    {
                        ItemCode = item.TryGetProperty("ItemCode", out var ic) ? ic.GetString() : null,
                        BatchNum = item.TryGetProperty("BatchNum", out var bn) ? bn.GetString() : null,
                        Quantity = item.TryGetProperty("Quantity", out var qty) ? qty.GetDecimal() : 0,
                        Warehouse = warehouseCode,
                        ExpiryDate = item.TryGetProperty("ExpDate", out var exp) ? exp.GetString() : null,
                        ManufacturingDate = item.TryGetProperty("MnfDate", out var mnf) ? mnf.GetString() : null,
                        AdmissionDate = item.TryGetProperty("InDate", out var ind) ? ind.GetString() : null,
                        Notes = item.TryGetProperty("Notes", out var notes) ? notes.GetString() : null
                    };
                    batches.Add(batch);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse SQL result for batch numbers");
        }
        return batches;
    }

    public async Task<List<BatchNumber>> GetAllBatchNumbersInWarehouseAsync(
        string warehouseCode,
        CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var queryCode = $"WHS_BATCHES_{warehouseCode.Replace("-", "_").ToUpperInvariant()}";

        // Try to delete existing query first (in case SQL changed or warehouse changed)
        await TryDeleteQueryAsync(queryCode, cancellationToken);

        // SQL query to get all batch numbers in a warehouse
        var sqlText = $"SELECT T0.\"ItemCode\", T0.\"DistNumber\" as \"BatchNum\", T1.\"Quantity\", T1.\"WhsCode\", " +
                      $"T0.\"ExpDate\", T0.\"MnfDate\", T0.\"InDate\", T0.\"Notes\" " +
                      $"FROM OBTN T0 INNER JOIN OBTQ T1 ON T0.\"AbsEntry\" = T1.\"MdAbsEntry\" " +
                      $"WHERE T1.\"WhsCode\" = '{warehouseCode}' AND T1.\"Quantity\" > 0 " +
                      $"ORDER BY T0.\"ItemCode\", T0.\"DistNumber\"";

        // Create the SQL query
        await CreateSqlQueryAsync(queryCode, $"Batches in {warehouseCode}", sqlText, cancellationToken);

        // Execute the query and retrieve results
        return await ExecuteBatchQueryAsync(queryCode, warehouseCode, cancellationToken);
    }

    private async Task<List<BatchNumber>> ExecuteBatchQueryAsync(
        string queryCode,
        string warehouseCode,
        CancellationToken cancellationToken)
    {
        var batches = new List<BatchNumber>();
        var skip = 0;
        const int pageSize = 500;
        bool hasMore = true;

        while (hasMore)
        {
            var url = $"SQLQueries('{queryCode}')/List?$skip={skip}";

            var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
            httpRequest.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
            httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            httpRequest.Headers.Add("Prefer", "odata.maxpagesize=500");

            var response = await _httpClient.SendAsync(httpRequest, cancellationToken);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _sessionId = null;
                await EnsureAuthenticatedAsync(cancellationToken);

                httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
                httpRequest.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
                httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                httpRequest.Headers.Add("Prefer", "odata.maxpagesize=500");
                response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            // Handle 404 as no results found (empty batch list for the warehouse)
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogInformation("No batch numbers found in warehouse {Warehouse}", warehouseCode);
                break;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to get batch numbers in warehouse {Warehouse}: {StatusCode} - {Error}",
                    warehouseCode, response.StatusCode, content);
                throw new Exception($"Failed to get batch numbers: {response.StatusCode} - {content}");
            }

            var pageBatches = ParseBatchNumbersFromSqlResult(content, warehouseCode);
            batches.AddRange(pageBatches);

            // Check if there are more pages
            using var doc = JsonDocument.Parse(content);
            hasMore = doc.RootElement.TryGetProperty("odata.nextLink", out _) ||
                      doc.RootElement.TryGetProperty("@odata.nextLink", out _);

            if (hasMore)
            {
                skip += pageSize;
            }

            _logger.LogInformation("Retrieved {PageCount} batches from warehouse {Warehouse}, total: {Total}",
                pageBatches.Count, warehouseCode, batches.Count);
        }

        return batches;
    }

    #endregion

    #region Price Operations

    // Price List constants
    private const int UsdPriceList = 13;
    private const int ZigPriceList = 12;
    private const string PriceQueryCode = "SHOP_PRICES";

    public async Task<List<ItemPriceDto>> GetItemPricesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        // Try to delete existing query first (in case SQL changed)
        await TryDeleteQueryAsync(PriceQueryCode, cancellationToken);

        // Create and execute the SQL query
        var sqlText = @"SELECT T1.""ItemCode"", T1.""ItemName"", T0.""Price"", 'USD' AS ""Currency"" FROM ITM1 T0 INNER JOIN OITM T1 ON T0.""ItemCode"" = T1.""ItemCode"" WHERE T0.""PriceList"" = 13 AND T0.""Price"" > 0 UNION ALL SELECT T1.""ItemCode"", T1.""ItemName"", T0.""Price"", 'ZIG' AS ""Currency"" FROM ITM1 T0 INNER JOIN OITM T1 ON T0.""ItemCode"" = T1.""ItemCode"" WHERE T0.""PriceList"" = 12 AND T0.""Price"" > 0";

        await CreateSqlQueryAsync(PriceQueryCode, "Shop Item Prices", sqlText, cancellationToken);

        var prices = await ExecuteSqlQueryAsync(PriceQueryCode, cancellationToken);

        _logger.LogInformation("Retrieved {Count} item prices from SAP", prices.Count);
        return prices;
    }

    public async Task<List<ItemPriceDto>> GetItemPriceByCodeAsync(string itemCode, CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var queryCode = $"SHOP_PRICE_{itemCode.Replace("-", "_").ToUpperInvariant()}";

        // Try to delete existing query first
        await TryDeleteQueryAsync(queryCode, cancellationToken);

        // Create item-specific query
        var sqlText = $@"SELECT T1.""ItemCode"", T1.""ItemName"", T0.""Price"", 'USD' AS ""Currency"" FROM ITM1 T0 INNER JOIN OITM T1 ON T0.""ItemCode"" = T1.""ItemCode"" WHERE T0.""PriceList"" = 13 AND T0.""Price"" > 0 AND T1.""ItemCode"" = '{itemCode}' UNION ALL SELECT T1.""ItemCode"", T1.""ItemName"", T0.""Price"", 'ZIG' AS ""Currency"" FROM ITM1 T0 INNER JOIN OITM T1 ON T0.""ItemCode"" = T1.""ItemCode"" WHERE T0.""PriceList"" = 12 AND T0.""Price"" > 0 AND T1.""ItemCode"" = '{itemCode}'";

        await CreateSqlQueryAsync(queryCode, $"Price for {itemCode}", sqlText, cancellationToken);

        var prices = await ExecuteSqlQueryAsync(queryCode, cancellationToken);

        // Clean up item-specific query
        await TryDeleteQueryAsync(queryCode, cancellationToken);

        return prices;
    }

    private async Task TryDeleteQueryAsync(string queryCode, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"SQLQueries('{queryCode}')";
            var httpRequest = new HttpRequestMessage(HttpMethod.Delete, url);
            httpRequest.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
            await _httpClient.SendAsync(httpRequest, cancellationToken);
        }
        catch
        {
            // Ignore delete errors
        }
    }

    private async Task CreateSqlQueryAsync(string queryCode, string queryName, string sqlText, CancellationToken cancellationToken)
    {
        var payload = new
        {
            SqlCode = queryCode,
            SqlName = queryName,
            SqlText = sqlText
        };

        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "SQLQueries");
        httpRequest.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        httpRequest.Content = content;

        var response = await _httpClient.SendAsync(httpRequest, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _sessionId = null;
            await EnsureAuthenticatedAsync(cancellationToken);

            httpRequest = new HttpRequestMessage(HttpMethod.Post, "SQLQueries");
            httpRequest.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
            httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");
            response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        }

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogInformation("Create SQL Query response: {StatusCode} - {Content}", response.StatusCode, responseContent);

        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.Conflict)
        {
            throw new Exception($"Failed to create SQL query: {response.StatusCode} - {responseContent}");
        }
    }

    private async Task<List<ItemPriceDto>> ExecuteSqlQueryAsync(string queryCode, CancellationToken cancellationToken)
    {
        var prices = new List<ItemPriceDto>();
        var skip = 0;
        bool hasMore = true;

        while (hasMore)
        {
            var url = $"SQLQueries('{queryCode}')/List?$skip={skip}";

            var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
            httpRequest.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
            httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            httpRequest.Headers.Add("Prefer", "odata.maxpagesize=500");

            var response = await _httpClient.SendAsync(httpRequest, cancellationToken);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _sessionId = null;
                await EnsureAuthenticatedAsync(cancellationToken);

                httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
                httpRequest.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
                httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                httpRequest.Headers.Add("Prefer", "odata.maxpagesize=500");
                response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            }

            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                break;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to execute SQL query: {StatusCode} - {Error}", response.StatusCode, responseContent);
                throw new Exception($"Failed to execute SQL query: {response.StatusCode} - {responseContent}");
            }

            var (pagePrices, nextLink) = ParsePricesAndNextLink(responseContent);
            prices.AddRange(pagePrices);
            _logger.LogInformation("Page at skip={Skip}: Retrieved {PageCount} prices, total so far: {Total}, nextLink: {NextLink}",
                skip, pagePrices.Count, prices.Count, nextLink ?? "null");

            // If we got fewer than expected or no nextLink, we're done
            if (pagePrices.Count == 0 || string.IsNullOrEmpty(nextLink))
            {
                hasMore = false;
            }
            else
            {
                skip += pagePrices.Count;
            }
        }

        return prices;
    }

    private (List<ItemPriceDto> prices, string? nextLink) ParsePricesAndNextLink(string jsonContent)
    {
        var prices = new List<ItemPriceDto>();
        string? nextLink = null;

        try
        {
            using var doc = JsonDocument.Parse(jsonContent);

            // Check for next page link
            if (doc.RootElement.TryGetProperty("odata.nextLink", out var nextLinkProp))
            {
                nextLink = nextLinkProp.GetString();
            }
            else if (doc.RootElement.TryGetProperty("@odata.nextLink", out var nextLinkProp2))
            {
                nextLink = nextLinkProp2.GetString();
            }

            if (doc.RootElement.TryGetProperty("value", out var valueArray))
            {
                foreach (var item in valueArray.EnumerateArray())
                {
                    var price = new ItemPriceDto
                    {
                        ItemCode = item.TryGetProperty("ItemCode", out var ic) ? ic.GetString() : null,
                        ItemName = item.TryGetProperty("ItemName", out var name) ? name.GetString() : null,
                        Price = item.TryGetProperty("Price", out var p) ? p.GetDecimal() : 0,
                        Currency = item.TryGetProperty("Currency", out var curr) ? curr.GetString() : null
                    };
                    prices.Add(price);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse SQL result");
        }

        return (prices, nextLink);
    }

    #endregion

    #region Incoming Payment Operations

    public async Task<List<IncomingPayment>> GetIncomingPaymentsAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var url = "IncomingPayments?$orderby=DocEntry desc";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _sessionId = null;
            await EnsureAuthenticatedAsync(cancellationToken);

            request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            response = await _httpClient.SendAsync(request, cancellationToken);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Failed to get incoming payments: {StatusCode} - {Error}", response.StatusCode, errorContent);
            throw new Exception($"Failed to get incoming payments: {response.StatusCode} - {errorContent}");
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<SAPResponse<IncomingPayment>>(content);

        return result?.Value ?? new List<IncomingPayment>();
    }

    public async Task<List<IncomingPayment>> GetPagedIncomingPaymentsAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var skip = (page - 1) * pageSize;
        var filter = $"$orderby=DocEntry desc&$top={pageSize}&$skip={skip}";
        var url = $"IncomingPayments?{filter}";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _sessionId = null;
            await EnsureAuthenticatedAsync(cancellationToken);

            request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            response = await _httpClient.SendAsync(request, cancellationToken);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Failed to get paged incoming payments: {StatusCode} - {Error}", response.StatusCode, errorContent);
            throw new Exception($"Failed to get incoming payments: {response.StatusCode} - {errorContent}");
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<SAPResponse<IncomingPayment>>(content);

        return result?.Value ?? new List<IncomingPayment>();
    }

    public async Task<IncomingPayment?> GetIncomingPaymentByDocEntryAsync(
        int docEntry,
        CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var url = $"IncomingPayments({docEntry})";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _sessionId = null;
            await EnsureAuthenticatedAsync(cancellationToken);

            request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            response = await _httpClient.SendAsync(request, cancellationToken);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Failed to get incoming payment {DocEntry}: {StatusCode} - {Error}", docEntry, response.StatusCode, errorContent);
            throw new Exception($"Failed to get incoming payment: {response.StatusCode} - {errorContent}");
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<IncomingPayment>(content);
    }

    public async Task<IncomingPayment?> GetIncomingPaymentByDocNumAsync(
        int docNum,
        CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var filter = $"$filter=DocNum eq {docNum}";
        var url = $"IncomingPayments?{filter}";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _sessionId = null;
            await EnsureAuthenticatedAsync(cancellationToken);

            request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            response = await _httpClient.SendAsync(request, cancellationToken);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Failed to get incoming payment by DocNum {DocNum}: {StatusCode} - {Error}", docNum, response.StatusCode, errorContent);
            throw new Exception($"Failed to get incoming payment: {response.StatusCode} - {errorContent}");
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<SAPResponse<IncomingPayment>>(content);

        return result?.Value?.FirstOrDefault();
    }

    public async Task<List<IncomingPayment>> GetIncomingPaymentsByCustomerAsync(
        string cardCode,
        CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var filter = $"$filter=CardCode eq '{cardCode}'&$orderby=DocEntry desc";
        var url = $"IncomingPayments?{filter}";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _sessionId = null;
            await EnsureAuthenticatedAsync(cancellationToken);

            request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            response = await _httpClient.SendAsync(request, cancellationToken);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Failed to get incoming payments for customer {CardCode}: {StatusCode} - {Error}", cardCode, response.StatusCode, errorContent);
            throw new Exception($"Failed to get incoming payments: {response.StatusCode} - {errorContent}");
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<SAPResponse<IncomingPayment>>(content);

        return result?.Value ?? new List<IncomingPayment>();
    }

    public async Task<List<IncomingPayment>> GetIncomingPaymentsByDateRangeAsync(
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var fromDateStr = fromDate.ToString("yyyy-MM-dd");
        var toDateStr = toDate.ToString("yyyy-MM-dd");
        var filter = $"$filter=DocDate ge '{fromDateStr}' and DocDate le '{toDateStr}'&$orderby=DocEntry desc";
        var url = $"IncomingPayments?{filter}";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _sessionId = null;
            await EnsureAuthenticatedAsync(cancellationToken);

            request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            response = await _httpClient.SendAsync(request, cancellationToken);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Failed to get incoming payments by date range: {StatusCode} - {Error}", response.StatusCode, errorContent);
            throw new Exception($"Failed to get incoming payments: {response.StatusCode} - {errorContent}");
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<SAPResponse<IncomingPayment>>(content);

        return result?.Value ?? new List<IncomingPayment>();
    }

    #endregion

    #region Stock Quantity Operations

    private const string StockQueryPrefix = "STOCK_QTY_";

    public async Task<List<StockQuantityDto>> GetStockQuantitiesInWarehouseAsync(
        string warehouseCode,
        CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var queryCode = $"{StockQueryPrefix}{warehouseCode.Replace("-", "_").ToUpperInvariant()}";

        // Try to delete existing query first
        await TryDeleteQueryAsync(queryCode, cancellationToken);

        // SQL query to get stock quantities - conditionally include custom packaging code fields
        var customFieldsSql = _settings.UseCustomFields
            ? @",
            T0.""U_PackagingCode"" as ""PackagingCode"",
            T0.""U_PackagingCodeLabels"" as ""PackagingCodeLabels"",
            T0.""U_PackagingCodeLids"" as ""PackagingCodeLids"""
            : "";

        var sqlText = $@"SELECT 
            T0.""ItemCode"", 
            T0.""ItemName"", 
            T0.""CodeBars"" as ""BarCode"",
            T1.""WhsCode"" as ""WarehouseCode"",
            T1.""OnHand"" as ""InStock"",
            T1.""IsCommited"" as ""Committed"",
            T1.""OnOrder"" as ""Ordered"",
            T0.""InvntryUom"" as ""UoM""{customFieldsSql}
        FROM OITM T0 
        INNER JOIN OITW T1 ON T0.""ItemCode"" = T1.""ItemCode""
        WHERE T1.""WhsCode"" = '{warehouseCode}' 
        AND (T1.""OnHand"" > 0 OR T1.""IsCommited"" > 0 OR T1.""OnOrder"" > 0)
        ORDER BY T0.""ItemCode""";

        // Create the SQL query
        await CreateSqlQueryAsync(queryCode, $"Stock Quantities in {warehouseCode}", sqlText, cancellationToken);

        // Execute the query and retrieve results
        return await ExecuteStockQueryAsync(queryCode, warehouseCode, cancellationToken);
    }

    public async Task<List<StockQuantityDto>> GetPagedStockQuantitiesInWarehouseAsync(
        string warehouseCode,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var queryCode = $"{StockQueryPrefix}{warehouseCode.Replace("-", "_").ToUpperInvariant()}_PAGED";

        // Try to delete existing query first
        await TryDeleteQueryAsync(queryCode, cancellationToken);

        var skip = (page - 1) * pageSize;

        // SQL query to get stock quantities with pagination - conditionally include custom packaging code fields
        var customFieldsSql = _settings.UseCustomFields
            ? @",
            T0.""U_PackagingCode"" as ""PackagingCode"",
            T0.""U_PackagingCodeLabels"" as ""PackagingCodeLabels"",
            T0.""U_PackagingCodeLids"" as ""PackagingCodeLids"""
            : "";

        var sqlText = $@"SELECT 
            T0.""ItemCode"", 
            T0.""ItemName"", 
            T0.""CodeBars"" as ""BarCode"",
            T1.""WhsCode"" as ""WarehouseCode"",
            T1.""OnHand"" as ""InStock"",
            T1.""IsCommited"" as ""Committed"",
            T1.""OnOrder"" as ""Ordered"",
            T0.""InvntryUom"" as ""UoM""{customFieldsSql}
        FROM OITM T0 
        INNER JOIN OITW T1 ON T0.""ItemCode"" = T1.""ItemCode""
        WHERE T1.""WhsCode"" = '{warehouseCode}' 
        AND (T1.""OnHand"" > 0 OR T1.""IsCommited"" > 0 OR T1.""OnOrder"" > 0)
        ORDER BY T0.""ItemCode""";

        // Create the SQL query
        await CreateSqlQueryAsync(queryCode, $"Stock Quantities Paged in {warehouseCode}", sqlText, cancellationToken);

        // Execute the query with pagination
        return await ExecuteStockQueryPagedAsync(queryCode, warehouseCode, skip, pageSize, cancellationToken);
    }

    private async Task<List<StockQuantityDto>> ExecuteStockQueryAsync(
        string queryCode,
        string warehouseCode,
        CancellationToken cancellationToken)
    {
        var stocks = new List<StockQuantityDto>();
        var skip = 0;
        const int pageSize = 500;
        bool hasMore = true;

        while (hasMore)
        {
            var url = $"SQLQueries('{queryCode}')/List?$skip={skip}";

            var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
            httpRequest.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
            httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            httpRequest.Headers.Add("Prefer", "odata.maxpagesize=500");

            var response = await _httpClient.SendAsync(httpRequest, cancellationToken);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _sessionId = null;
                await EnsureAuthenticatedAsync(cancellationToken);

                httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
                httpRequest.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
                httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                httpRequest.Headers.Add("Prefer", "odata.maxpagesize=500");
                response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogInformation("No stock quantities found in warehouse {Warehouse}", warehouseCode);
                break;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to get stock quantities in warehouse {Warehouse}: {StatusCode} - {Error}",
                    warehouseCode, response.StatusCode, content);
                throw new Exception($"Failed to get stock quantities: {response.StatusCode} - {content}");
            }

            var pageStocks = ParseStockQuantitiesFromSqlResult(content);
            stocks.AddRange(pageStocks);

            using var doc = JsonDocument.Parse(content);
            hasMore = doc.RootElement.TryGetProperty("odata.nextLink", out _) ||
                      doc.RootElement.TryGetProperty("@odata.nextLink", out _);

            if (hasMore)
            {
                skip += pageSize;
            }

            _logger.LogInformation("Retrieved {PageCount} stock items from warehouse {Warehouse}, total: {Total}",
                pageStocks.Count, warehouseCode, stocks.Count);
        }

        return stocks;
    }

    private async Task<List<StockQuantityDto>> ExecuteStockQueryPagedAsync(
        string queryCode,
        string warehouseCode,
        int skip,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var url = $"SQLQueries('{queryCode}')/List?$skip={skip}&$top={pageSize}";

        var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
        httpRequest.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await _httpClient.SendAsync(httpRequest, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _sessionId = null;
            await EnsureAuthenticatedAsync(cancellationToken);

            httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
            httpRequest.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
            httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogInformation("No stock quantities found in warehouse {Warehouse}", warehouseCode);
            return new List<StockQuantityDto>();
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Failed to get stock quantities in warehouse {Warehouse}: {StatusCode} - {Error}",
                warehouseCode, response.StatusCode, content);
            throw new Exception($"Failed to get stock quantities: {response.StatusCode} - {content}");
        }

        return ParseStockQuantitiesFromSqlResult(content);
    }

    private List<StockQuantityDto> ParseStockQuantitiesFromSqlResult(string jsonContent)
    {
        var stocks = new List<StockQuantityDto>();
        try
        {
            using var doc = JsonDocument.Parse(jsonContent);
            if (doc.RootElement.TryGetProperty("value", out var valueArray))
            {
                foreach (var item in valueArray.EnumerateArray())
                {
                    var inStock = item.TryGetProperty("InStock", out var inStockProp) ? inStockProp.GetDecimal() : 0;
                    var committed = item.TryGetProperty("Committed", out var committedProp) ? committedProp.GetDecimal() : 0;
                    var ordered = item.TryGetProperty("Ordered", out var orderedProp) ? orderedProp.GetDecimal() : 0;

                    var stock = new StockQuantityDto
                    {
                        ItemCode = item.TryGetProperty("ItemCode", out var ic) ? ic.GetString() : null,
                        ItemName = item.TryGetProperty("ItemName", out var name) ? name.GetString() : null,
                        BarCode = item.TryGetProperty("BarCode", out var bc) ? bc.GetString() : null,
                        WarehouseCode = item.TryGetProperty("WarehouseCode", out var whs) ? whs.GetString() : null,
                        InStock = inStock,
                        Committed = committed,
                        Ordered = ordered,
                        Available = inStock - committed + ordered,
                        UoM = item.TryGetProperty("UoM", out var uom) ? uom.GetString() : null,
                        PackagingCode = item.TryGetProperty("PackagingCode", out var pc) ? pc.GetString() : null,
                        PackagingCodeLabels = item.TryGetProperty("PackagingCodeLabels", out var pcl) ? pcl.GetString() : null,
                        PackagingCodeLids = item.TryGetProperty("PackagingCodeLids", out var pclids) ? pclids.GetString() : null
                    };
                    stocks.Add(stock);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse SQL result for stock quantities");
        }
        return stocks;
    }

    public async Task<Dictionary<string, PackagingMaterialStockDto>> GetPackagingMaterialStockAsync(
        IEnumerable<string> itemCodes,
        string warehouseCode,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, PackagingMaterialStockDto>();

        var codes = itemCodes.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().ToList();
        if (codes.Count == 0)
            return result;

        await EnsureAuthenticatedAsync(cancellationToken);

        var queryCode = $"PKG_STK_{DateTime.UtcNow.Ticks % 100000000}";

        // Try to delete existing query first
        await TryDeleteQueryAsync(queryCode, cancellationToken);

        // Build IN clause for item codes
        var inClause = string.Join(",", codes.Select(c => $"'{c}'"));

        // SQL query to get total packaging material stock across ALL warehouses
        // This is important because packaging materials are often stored in different warehouses than finished goods
        var sqlText = $@"SELECT 
            T0.""ItemCode"", 
            T0.""ItemName"", 
            SUM(T1.""OnHand"") as ""InStock"",
            SUM(T1.""OnHand"") as ""Available"",
            T0.""InvntryUom"" as ""UoM""
        FROM OITM T0 
        INNER JOIN OITW T1 ON T0.""ItemCode"" = T1.""ItemCode""
        WHERE T0.""ItemCode"" IN ({inClause})
        GROUP BY T0.""ItemCode"", T0.""ItemName"", T0.""InvntryUom""";

        _logger.LogInformation("Fetching packaging material stock for {Count} items: {Items}", codes.Count, string.Join(", ", codes.Take(10)));

        try
        {
            // Create the SQL query
            await CreateSqlQueryAsync(queryCode, "Packaging Material Stock", sqlText, cancellationToken);

            // Execute the query with pagination to get all results
            var skip = 0;
            bool hasMore = true;

            _logger.LogInformation("Starting pagination for packaging material stock query...");

            while (hasMore)
            {
                var url = skip == 0
                    ? $"SQLQueries('{queryCode}')/List"
                    : $"SQLQueries('{queryCode}')/List?$skip={skip}";

                _logger.LogInformation("Fetching packaging stock page at skip={Skip}, url={Url}", skip, url);

                var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
                httpRequest.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
                httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                httpRequest.Headers.Add("Prefer", "odata.maxpagesize=100");

                var response = await _httpClient.SendAsync(httpRequest, cancellationToken);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    _sessionId = null;
                    await EnsureAuthenticatedAsync(cancellationToken);

                    httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
                    httpRequest.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
                    httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    httpRequest.Headers.Add("Prefer", "odata.maxpagesize=100");
                    response = await _httpClient.SendAsync(httpRequest, cancellationToken);
                }

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogWarning("Failed to get packaging material stock: {Status} - {Content}", response.StatusCode, errorContent);
                    break;
                }

                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                var pageResults = ParsePackagingMaterialStockFromSqlResult(content);

                foreach (var kvp in pageResults)
                {
                    result[kvp.Key] = kvp.Value;
                }

                _logger.LogInformation("Page at skip={Skip}: retrieved {PageCount} items, total so far: {Total}",
                    skip, pageResults.Count, result.Count);

                // Check for more pages using nextLink
                using var doc = JsonDocument.Parse(content);
                var hasNextLink = doc.RootElement.TryGetProperty("odata.nextLink", out var nextLinkProp) ||
                                  doc.RootElement.TryGetProperty("@odata.nextLink", out nextLinkProp);

                if (hasNextLink)
                {
                    var nextLink = nextLinkProp.GetString();
                    _logger.LogInformation("NextLink found: {NextLink}", nextLink);
                    skip += pageResults.Count > 0 ? pageResults.Count : 20;
                    hasMore = true;
                }
                else
                {
                    _logger.LogInformation("No nextLink found, pagination complete");
                    hasMore = false;
                }

                // Safety check
                if (pageResults.Count == 0)
                {
                    _logger.LogInformation("Empty page received, stopping pagination");
                    hasMore = false;
                }
            }

            _logger.LogInformation("Total packaging material stock items retrieved: {Count}", result.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get packaging material stock for items");
        }
        finally
        {
            // Clean up the query
            await TryDeleteQueryAsync(queryCode, cancellationToken);
        }

        return result;
    }

    private Dictionary<string, PackagingMaterialStockDto> ParsePackagingMaterialStockFromSqlResult(string jsonContent)
    {
        var result = new Dictionary<string, PackagingMaterialStockDto>();
        try
        {
            using var doc = JsonDocument.Parse(jsonContent);
            if (doc.RootElement.TryGetProperty("value", out var valueArray))
            {
                foreach (var item in valueArray.EnumerateArray())
                {
                    var itemCode = item.TryGetProperty("ItemCode", out var ic) ? ic.GetString() : null;
                    if (string.IsNullOrEmpty(itemCode))
                        continue;

                    var stock = new PackagingMaterialStockDto
                    {
                        ItemCode = itemCode,
                        ItemName = item.TryGetProperty("ItemName", out var name) ? name.GetString() : null,
                        InStock = item.TryGetProperty("InStock", out var inStockProp) ? inStockProp.GetDecimal() : 0,
                        Available = item.TryGetProperty("Available", out var availProp) ? availProp.GetDecimal() : 0,
                        UoM = item.TryGetProperty("UoM", out var uom) ? uom.GetString() : null
                    };
                    result[itemCode] = stock;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse packaging material stock from SQL result");
        }
        return result;
    }

    #endregion

    #region Sales Quantity Operations

    private const string SalesQueryPrefix = "SALES_QTY_";

    public async Task<List<SalesQuantityDto>> GetSalesQuantitiesByWarehouseAsync(
        string warehouseCode,
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var fromDateStr = fromDate.ToString("yyyyMMdd");
        var toDateStr = toDate.ToString("yyyyMMdd");
        var queryCode = $"{SalesQueryPrefix}{warehouseCode.Replace("-", "_").ToUpperInvariant()}_{fromDateStr}_{toDateStr}";

        // Try to delete existing query first
        await TryDeleteQueryAsync(queryCode, cancellationToken);

        // SQL query to get sales quantities by warehouse and date range with packaging code fields
        var sqlText = $@"SELECT 
            T1.""ItemCode"", 
            T2.""ItemName"", 
            T2.""CodeBars"" as ""BarCode"",
            SUM(T1.""Quantity"") as ""TotalQuantitySold"",
            SUM(T1.""LineTotal"") as ""TotalSalesValue"",
            COUNT(DISTINCT T0.""DocEntry"") as ""InvoiceCount"",
            T2.""InvntryUom"" as ""UoM"",
            T2.""U_PackagingCode"" as ""PackagingCode"",
            T2.""U_PackagingCodeLabels"" as ""PackagingCodeLabels"",
            T2.""U_PackagingCodeLids"" as ""PackagingCodeLids""
        FROM OINV T0 
        INNER JOIN INV1 T1 ON T0.""DocEntry"" = T1.""DocEntry""
        INNER JOIN OITM T2 ON T1.""ItemCode"" = T2.""ItemCode""
        WHERE T1.""WhsCode"" = '{warehouseCode}'
        AND T0.""DocDate"" >= '{fromDate:yyyy-MM-dd}'
        AND T0.""DocDate"" <= '{toDate:yyyy-MM-dd}'
        AND T0.""CANCELED"" = 'N'
        GROUP BY T1.""ItemCode"", T2.""ItemName"", T2.""CodeBars"", T2.""InvntryUom"", 
                 T2.""U_PackagingCode"", T2.""U_PackagingCodeLabels"", T2.""U_PackagingCodeLids""
        ORDER BY SUM(T1.""Quantity"") DESC";

        // Create the SQL query
        await CreateSqlQueryAsync(queryCode, $"Sales Quantities in {warehouseCode}", sqlText, cancellationToken);

        // Execute the query and retrieve results
        return await ExecuteSalesQueryAsync(queryCode, warehouseCode, cancellationToken);
    }

    private async Task<List<SalesQuantityDto>> ExecuteSalesQueryAsync(
        string queryCode,
        string warehouseCode,
        CancellationToken cancellationToken)
    {
        var sales = new List<SalesQuantityDto>();
        var skip = 0;
        const int pageSize = 500;
        bool hasMore = true;

        while (hasMore)
        {
            var url = $"SQLQueries('{queryCode}')/List?$skip={skip}";

            var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
            httpRequest.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
            httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            httpRequest.Headers.Add("Prefer", "odata.maxpagesize=500");

            var response = await _httpClient.SendAsync(httpRequest, cancellationToken);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _sessionId = null;
                await EnsureAuthenticatedAsync(cancellationToken);

                httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
                httpRequest.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
                httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                httpRequest.Headers.Add("Prefer", "odata.maxpagesize=500");
                response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogInformation("No sales found in warehouse {Warehouse}", warehouseCode);
                break;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to get sales quantities in warehouse {Warehouse}: {StatusCode} - {Error}",
                    warehouseCode, response.StatusCode, content);
                throw new Exception($"Failed to get sales quantities: {response.StatusCode} - {content}");
            }

            var pageSales = ParseSalesQuantitiesFromSqlResult(content);
            sales.AddRange(pageSales);

            using var doc = JsonDocument.Parse(content);
            hasMore = doc.RootElement.TryGetProperty("odata.nextLink", out _) ||
                      doc.RootElement.TryGetProperty("@odata.nextLink", out _);

            if (hasMore)
            {
                skip += pageSize;
            }

            _logger.LogInformation("Retrieved {PageCount} sales items from warehouse {Warehouse}, total: {Total}",
                pageSales.Count, warehouseCode, sales.Count);
        }

        return sales;
    }

    private List<SalesQuantityDto> ParseSalesQuantitiesFromSqlResult(string jsonContent)
    {
        var sales = new List<SalesQuantityDto>();
        try
        {
            using var doc = JsonDocument.Parse(jsonContent);
            if (doc.RootElement.TryGetProperty("value", out var valueArray))
            {
                foreach (var item in valueArray.EnumerateArray())
                {
                    var sale = new SalesQuantityDto
                    {
                        ItemCode = item.TryGetProperty("ItemCode", out var ic) ? ic.GetString() : null,
                        ItemName = item.TryGetProperty("ItemName", out var name) ? name.GetString() : null,
                        BarCode = item.TryGetProperty("BarCode", out var bc) ? bc.GetString() : null,
                        TotalQuantitySold = item.TryGetProperty("TotalQuantitySold", out var qty) ? qty.GetDecimal() : 0,
                        TotalSalesValue = item.TryGetProperty("TotalSalesValue", out var val) ? val.GetDecimal() : 0,
                        InvoiceCount = item.TryGetProperty("InvoiceCount", out var cnt) ? cnt.GetInt32() : 0,
                        UoM = item.TryGetProperty("UoM", out var uom) ? uom.GetString() : null,
                        PackagingCode = item.TryGetProperty("PackagingCode", out var pc) ? pc.GetString() : null,
                        PackagingCodeLabels = item.TryGetProperty("PackagingCodeLabels", out var pcl) ? pcl.GetString() : null,
                        PackagingCodeLids = item.TryGetProperty("PackagingCodeLids", out var pclids) ? pclids.GetString() : null
                    };
                    sales.Add(sale);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse SQL result for sales quantities");
        }
        return sales;
    }

    public async Task<List<Invoice>> GetInvoicesByWarehouseAndDateRangeAsync(
        string warehouseCode,
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var fromDateStr = fromDate.ToString("yyyy-MM-dd");
        var toDateStr = toDate.ToString("yyyy-MM-dd");

        // Filter invoices that have at least one line in the specified warehouse
        var filter = $"$filter=DocDate ge '{fromDateStr}' and DocDate le '{toDateStr}'&$orderby=DocEntry desc";
        var url = $"Invoices?{filter}";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _sessionId = null;
            await EnsureAuthenticatedAsync(cancellationToken);

            request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            response = await _httpClient.SendAsync(request, cancellationToken);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Failed to get invoices by warehouse and date range: {StatusCode} - {Error}", response.StatusCode, errorContent);
            throw new Exception($"Failed to get invoices: {response.StatusCode} - {errorContent}");
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<SAPResponse<Invoice>>(content);

        // Filter client-side to only include invoices with lines in the specified warehouse
        var invoices = result?.Value ?? new List<Invoice>();
        return invoices.Where(inv => inv.DocumentLines?.Any(line => line.WarehouseCode == warehouseCode) == true).ToList();
    }

    #endregion

    #region Warehouse Operations

    public async Task<List<WarehouseDto>> GetWarehousesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var allWarehouses = new List<WarehouseDto>();
        var skip = 0;
        const int pageSize = 100;
        bool hasMore = true;

        while (hasMore)
        {
            var url = $"Warehouses?$orderby=WarehouseCode&$skip={skip}";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Add("Prefer", $"odata.maxpagesize={pageSize}");

            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _sessionId = null;
                await EnsureAuthenticatedAsync(cancellationToken);

                request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.Add("Prefer", $"odata.maxpagesize={pageSize}");
                response = await _httpClient.SendAsync(request, cancellationToken);
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Failed to get warehouses: {StatusCode} - {Error}", response.StatusCode, errorContent);
                throw new Exception($"Failed to get warehouses: {response.StatusCode} - {errorContent}");
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var pageWarehouses = ParseWarehousesFromResponse(content);
            allWarehouses.AddRange(pageWarehouses);

            _logger.LogInformation("Retrieved {PageCount} warehouses at skip={Skip}, total: {Total}",
                pageWarehouses.Count, skip, allWarehouses.Count);

            // Check if there are more pages
            using var doc = JsonDocument.Parse(content);
            hasMore = doc.RootElement.TryGetProperty("odata.nextLink", out _) ||
                      doc.RootElement.TryGetProperty("@odata.nextLink", out _);

            if (!hasMore && pageWarehouses.Count == pageSize)
            {
                // Try next page anyway in case nextLink wasn't provided
                hasMore = true;
            }

            if (pageWarehouses.Count == 0)
            {
                hasMore = false;
            }

            skip += pageWarehouses.Count > 0 ? pageWarehouses.Count : pageSize;
        }

        return allWarehouses;
    }

    private List<WarehouseDto> ParseWarehousesFromResponse(string jsonContent)
    {
        var warehouses = new List<WarehouseDto>();
        try
        {
            using var doc = JsonDocument.Parse(jsonContent);
            if (doc.RootElement.TryGetProperty("value", out var valueArray))
            {
                foreach (var item in valueArray.EnumerateArray())
                {
                    var warehouse = new WarehouseDto
                    {
                        WarehouseCode = item.TryGetProperty("WarehouseCode", out var code) ? code.GetString() : null,
                        WarehouseName = item.TryGetProperty("WarehouseName", out var name) ? name.GetString() : null,
                        Location = item.TryGetProperty("Location", out var loc) ? GetStringOrNumber(loc) : null,
                        Street = item.TryGetProperty("Street", out var street) ? street.GetString() : null,
                        City = item.TryGetProperty("City", out var city) ? city.GetString() : null,
                        Country = item.TryGetProperty("Country", out var country) ? country.GetString() : null,
                        IsActive = !IsInactive(item)
                    };
                    warehouses.Add(warehouse);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse warehouses from response");
        }
        return warehouses;
    }

    private static bool IsInactive(JsonElement item)
    {
        if (!item.TryGetProperty("Inactive", out var inactive))
            return false;

        return inactive.ValueKind switch
        {
            JsonValueKind.String => inactive.GetString() == "tYES" || inactive.GetString() == "Y",
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => false
        };
    }

    private static string? GetStringOrNumber(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            _ => null
        };
    }

    #endregion

    #region Business Partner Operations

    public async Task<List<BusinessPartnerDto>> GetBusinessPartnersAsync(CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var allPartners = new List<BusinessPartnerDto>();
        var skip = 0;
        const int pageSize = 100;
        bool hasMore = true;

        while (hasMore)
        {
            // Filter for Customers (cCustomer) only, you can modify to include Suppliers (cSupplier) or both
            var url = $"BusinessPartners?$filter=CardType eq 'cCustomer'&$orderby=CardCode&$skip={skip}";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Add("Prefer", $"odata.maxpagesize={pageSize}");

            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _sessionId = null;
                await EnsureAuthenticatedAsync(cancellationToken);

                request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.Add("Prefer", $"odata.maxpagesize={pageSize}");
                response = await _httpClient.SendAsync(request, cancellationToken);
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Failed to get business partners: {StatusCode} - {Error}", response.StatusCode, errorContent);
                throw new Exception($"Failed to get business partners: {response.StatusCode} - {errorContent}");
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var pagePartners = ParseBusinessPartnersFromResponse(content);
            allPartners.AddRange(pagePartners);

            _logger.LogInformation("Retrieved {PageCount} business partners at skip={Skip}, total: {Total}",
                pagePartners.Count, skip, allPartners.Count);

            // Check if there are more pages
            using var doc = JsonDocument.Parse(content);
            hasMore = doc.RootElement.TryGetProperty("odata.nextLink", out _) ||
                      doc.RootElement.TryGetProperty("@odata.nextLink", out _);

            if (!hasMore && pagePartners.Count == pageSize)
            {
                hasMore = true;
            }

            if (pagePartners.Count == 0)
            {
                hasMore = false;
            }

            skip += pagePartners.Count > 0 ? pagePartners.Count : pageSize;
        }

        return allPartners;
    }

    public async Task<List<BusinessPartnerDto>> GetBusinessPartnersByTypeAsync(string cardType, CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var allPartners = new List<BusinessPartnerDto>();
        var skip = 0;
        const int pageSize = 100;
        bool hasMore = true;

        while (hasMore)
        {
            var url = $"BusinessPartners?$filter=CardType eq '{cardType}'&$orderby=CardCode&$skip={skip}";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Add("Prefer", $"odata.maxpagesize={pageSize}");

            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _sessionId = null;
                await EnsureAuthenticatedAsync(cancellationToken);

                request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.Add("Prefer", $"odata.maxpagesize={pageSize}");
                response = await _httpClient.SendAsync(request, cancellationToken);
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Failed to get business partners by type: {StatusCode} - {Error}", response.StatusCode, errorContent);
                throw new Exception($"Failed to get business partners: {response.StatusCode} - {errorContent}");
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var pagePartners = ParseBusinessPartnersFromResponse(content);
            allPartners.AddRange(pagePartners);

            using var doc = JsonDocument.Parse(content);
            hasMore = doc.RootElement.TryGetProperty("odata.nextLink", out _) ||
                      doc.RootElement.TryGetProperty("@odata.nextLink", out _);

            if (!hasMore && pagePartners.Count == pageSize)
            {
                hasMore = true;
            }

            if (pagePartners.Count == 0)
            {
                hasMore = false;
            }

            skip += pagePartners.Count > 0 ? pagePartners.Count : pageSize;
        }

        return allPartners;
    }

    public async Task<List<BusinessPartnerDto>> SearchBusinessPartnersAsync(string searchTerm, CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        // Search by CardCode or CardName containing the search term
        var filter = $"$filter=(contains(CardCode,'{searchTerm}') or contains(CardName,'{searchTerm}')) and CardType eq 'cCustomer'&$orderby=CardCode&$top=50";
        var url = $"BusinessPartners?{filter}";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _sessionId = null;
            await EnsureAuthenticatedAsync(cancellationToken);

            request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            response = await _httpClient.SendAsync(request, cancellationToken);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Failed to search business partners: {StatusCode} - {Error}", response.StatusCode, errorContent);
            throw new Exception($"Failed to search business partners: {response.StatusCode} - {errorContent}");
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseBusinessPartnersFromResponse(content);
    }

    public async Task<BusinessPartnerDto?> GetBusinessPartnerByCodeAsync(string cardCode, CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var url = $"BusinessPartners('{cardCode}')";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _sessionId = null;
            await EnsureAuthenticatedAsync(cancellationToken);

            request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            response = await _httpClient.SendAsync(request, cancellationToken);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Failed to get business partner {CardCode}: {StatusCode} - {Error}", cardCode, response.StatusCode, errorContent);
            throw new Exception($"Failed to get business partner: {response.StatusCode} - {errorContent}");
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseSingleBusinessPartnerFromResponse(content);
    }

    private List<BusinessPartnerDto> ParseBusinessPartnersFromResponse(string jsonContent)
    {
        var partners = new List<BusinessPartnerDto>();
        try
        {
            using var doc = JsonDocument.Parse(jsonContent);
            if (doc.RootElement.TryGetProperty("value", out var valueArray))
            {
                foreach (var item in valueArray.EnumerateArray())
                {
                    var partner = ParseBusinessPartnerElement(item);
                    partners.Add(partner);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse business partners from response");
        }
        return partners;
    }

    private BusinessPartnerDto? ParseSingleBusinessPartnerFromResponse(string jsonContent)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonContent);
            return ParseBusinessPartnerElement(doc.RootElement);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse single business partner from response");
            return null;
        }
    }

    private BusinessPartnerDto ParseBusinessPartnerElement(JsonElement item)
    {
        return new BusinessPartnerDto
        {
            CardCode = item.TryGetProperty("CardCode", out var cardCode) ? cardCode.GetString() : null,
            CardName = item.TryGetProperty("CardName", out var cardName) ? cardName.GetString() : null,
            CardType = item.TryGetProperty("CardType", out var cardType) ? cardType.GetString() : null,
            GroupCode = item.TryGetProperty("GroupCode", out var groupCode) ? GetStringOrNumber(groupCode) : null,
            Phone1 = item.TryGetProperty("Phone1", out var phone1) ? phone1.GetString() : null,
            Phone2 = item.TryGetProperty("Phone2", out var phone2) ? phone2.GetString() : null,
            Email = item.TryGetProperty("EmailAddress", out var email) ? email.GetString() : null,
            Address = item.TryGetProperty("Address", out var address) ? address.GetString() : null,
            City = item.TryGetProperty("City", out var city) ? city.GetString() : null,
            Country = item.TryGetProperty("Country", out var country) ? country.GetString() : null,
            Currency = item.TryGetProperty("Currency", out var currency) ? currency.GetString() : null,
            Balance = item.TryGetProperty("CurrentAccountBalance", out var balance) ? balance.GetDecimal() : null,
            IsActive = !IsFrozen(item)
        };
    }

    private static bool IsFrozen(JsonElement item)
    {
        if (!item.TryGetProperty("Frozen", out var frozen))
            return false;

        return frozen.ValueKind switch
        {
            JsonValueKind.String => frozen.GetString() == "tYES" || frozen.GetString() == "Y",
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => false
        };
    }

    #endregion

    #region Stock Validation

    /// <summary>
    /// Validates that sufficient stock is available for all items in an invoice request.
    /// Checks both overall item stock and batch-specific availability.
    /// </summary>
    public async Task<List<StockValidationError>> ValidateStockAvailabilityAsync(
        CreateInvoiceRequest request,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<StockValidationError>();

        if (request.Lines == null || request.Lines.Count == 0)
        {
            return errors;
        }

        // Group lines by warehouse to minimize API calls
        var linesByWarehouse = request.Lines
            .Select((line, index) => new { Line = line, Index = index })
            .GroupBy(x => x.Line.WarehouseCode ?? "01")
            .ToList();

        foreach (var warehouseGroup in linesByWarehouse)
        {
            var warehouseCode = warehouseGroup.Key;

            // Get stock quantities for this warehouse
            List<StockQuantityDto> stockQuantities;
            try
            {
                stockQuantities = await GetStockQuantitiesInWarehouseAsync(warehouseCode, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get stock quantities for warehouse {WarehouseCode}", warehouseCode);
                // If we can't get stock, continue without validation (SAP will reject if insufficient)
                continue;
            }

            var stockLookup = stockQuantities.ToDictionary(
                s => s.ItemCode ?? string.Empty,
                s => s,
                StringComparer.OrdinalIgnoreCase);

            // Get batch information for batch-managed items
            List<BatchNumber>? allBatches = null;
            try
            {
                allBatches = await GetAllBatchNumbersInWarehouseAsync(warehouseCode, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get batch numbers for warehouse {WarehouseCode}", warehouseCode);
            }

            var batchLookup = allBatches?
                .GroupBy(b => b.ItemCode ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.ToDictionary(b => b.BatchNum ?? string.Empty, b => b, StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase);

            // Validate each line
            foreach (var item in warehouseGroup)
            {
                var line = item.Line;
                var lineNumber = item.Index + 1;
                var itemCode = line.ItemCode ?? string.Empty;

                // Check if we have batch numbers specified
                if (line.BatchNumbers != null && line.BatchNumbers.Count > 0)
                {
                    // Validate each batch
                    foreach (var batchRequest in line.BatchNumbers)
                    {
                        var batchNum = batchRequest.BatchNumber ?? string.Empty;
                        decimal availableBatchQty = 0;

                        if (batchLookup != null &&
                            batchLookup.TryGetValue(itemCode, out var itemBatches) &&
                            itemBatches.TryGetValue(batchNum, out var batch))
                        {
                            availableBatchQty = batch.Quantity;
                        }

                        if (batchRequest.Quantity > availableBatchQty)
                        {
                            errors.Add(new StockValidationError
                            {
                                LineNumber = lineNumber,
                                ItemCode = itemCode,
                                ItemName = stockLookup.TryGetValue(itemCode, out var stock) ? stock.ItemName : null,
                                WarehouseCode = warehouseCode,
                                RequestedQuantity = batchRequest.Quantity,
                                AvailableQuantity = availableBatchQty,
                                BatchNumber = batchNum
                            });
                        }
                    }
                }
                else
                {
                    // No batches specified, check overall stock availability
                    if (stockLookup.TryGetValue(itemCode, out var stock))
                    {
                        if (line.Quantity > stock.Available)
                        {
                            errors.Add(new StockValidationError
                            {
                                LineNumber = lineNumber,
                                ItemCode = itemCode,
                                ItemName = stock.ItemName,
                                WarehouseCode = warehouseCode,
                                RequestedQuantity = line.Quantity,
                                AvailableQuantity = stock.Available
                            });
                        }
                    }
                    else
                    {
                        // Item not found in stock - might be zero or item doesn't exist
                        errors.Add(new StockValidationError
                        {
                            LineNumber = lineNumber,
                            ItemCode = itemCode,
                            WarehouseCode = warehouseCode,
                            RequestedQuantity = line.Quantity,
                            AvailableQuantity = 0
                        });
                    }
                }
            }
        }

        return errors;
    }

    /// <summary>
    /// Creates a new inventory transfer in SAP Business One.
    /// CRITICAL: Stock availability should be validated before calling this method.
    /// </summary>
    public async Task<InventoryTransfer> CreateInventoryTransferAsync(
        CreateInventoryTransferRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        // Validate the request
        ValidateInventoryTransferRequest(request);

        // Build the SAP payload
        var lines = new List<object>();
        for (int i = 0; i < request.Lines!.Count; i++)
        {
            var line = request.Lines[i];

            // CRITICAL: Double-check quantity is positive
            if (line.Quantity <= 0)
            {
                throw new ArgumentException($"Line {i + 1}: Quantity must be greater than zero. Current value: {line.Quantity}");
            }

            var linePayload = new Dictionary<string, object>
            {
                ["ItemCode"] = line.ItemCode!,
                ["Quantity"] = line.Quantity,
                ["FromWarehouseCode"] = line.FromWarehouseCode ?? request.FromWarehouse ?? "01",
                ["WarehouseCode"] = line.ToWarehouseCode ?? request.ToWarehouse!
            };

            // Add batch numbers if specified
            if (line.BatchNumbers != null && line.BatchNumbers.Count > 0)
            {
                var batchNumbers = line.BatchNumbers.Select(b =>
                {
                    // CRITICAL: Validate batch quantity
                    if (b.Quantity <= 0)
                    {
                        throw new ArgumentException($"Line {i + 1}: Batch '{b.BatchNumber}' quantity must be greater than zero");
                    }
                    return new
                    {
                        BatchNumber = b.BatchNumber,
                        Quantity = b.Quantity,
                        BaseLineNumber = i
                    };
                }).ToList();
                linePayload["StockTransferLinesBinAllocations"] = batchNumbers;
            }

            lines.Add(linePayload);
        }

        var payload = new Dictionary<string, object>
        {
            ["DocDate"] = request.DocDate ?? DateTime.Today.ToString("yyyy-MM-dd"),
            ["FromWarehouse"] = request.FromWarehouse ?? "01",
            ["ToWarehouse"] = request.ToWarehouse!,
            ["StockTransferLines"] = lines
        };

        if (!string.IsNullOrWhiteSpace(request.DueDate))
        {
            payload["DueDate"] = request.DueDate;
        }

        if (!string.IsNullOrWhiteSpace(request.Comments))
        {
            payload["Comments"] = request.Comments;
        }

        var jsonPayload = JsonSerializer.Serialize(payload);
        _logger.LogDebug("Creating inventory transfer with payload: {Payload}", jsonPayload);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "StockTransfers")
        {
            Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
        };
        httpRequest.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await _httpClient.SendAsync(httpRequest, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _sessionId = null;
            await EnsureAuthenticatedAsync(cancellationToken);

            httpRequest = new HttpRequestMessage(HttpMethod.Post, "StockTransfers")
            {
                Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
            };
            httpRequest.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
            httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        }

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Failed to create inventory transfer: {StatusCode} - {Error}",
                response.StatusCode, responseContent);

            // Check for SAP stock-related errors
            if (responseContent.Contains("insufficient", StringComparison.OrdinalIgnoreCase) ||
                responseContent.Contains("negative", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"SAP rejected transfer due to insufficient stock: {responseContent}");
            }

            throw new Exception($"Failed to create inventory transfer: {response.StatusCode} - {responseContent}");
        }

        var createdTransfer = JsonSerializer.Deserialize<InventoryTransfer>(responseContent);

        _logger.LogInformation("Inventory transfer created successfully. DocEntry: {DocEntry}, DocNum: {DocNum}",
            createdTransfer?.DocEntry, createdTransfer?.DocNum);

        return createdTransfer ?? throw new Exception("Failed to deserialize created inventory transfer");
    }

    private void ValidateInventoryTransferRequest(CreateInventoryTransferRequest request)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(request.ToWarehouse))
        {
            errors.Add("Destination warehouse (ToWarehouse) is required");
        }

        if (request.Lines == null || request.Lines.Count == 0)
        {
            errors.Add("At least one transfer line is required");
        }
        else
        {
            for (int i = 0; i < request.Lines.Count; i++)
            {
                var line = request.Lines[i];
                if (string.IsNullOrWhiteSpace(line.ItemCode))
                {
                    errors.Add($"Line {i + 1}: Item code is required");
                }
                if (line.Quantity <= 0)
                {
                    errors.Add($"Line {i + 1}: Quantity must be greater than zero");
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join("; ", errors));
        }
    }

    /// <summary>
    /// Creates a new incoming payment in SAP Business One.
    /// CRITICAL: All amounts are validated to be non-negative.
    /// </summary>
    public async Task<IncomingPayment> CreateIncomingPaymentAsync(
        CreateIncomingPaymentRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        // Validate the request
        ValidateIncomingPaymentRequest(request);

        var payload = new Dictionary<string, object>
        {
            ["CardCode"] = request.CardCode!,
            ["DocDate"] = request.DocDate ?? DateTime.Today.ToString("yyyy-MM-dd")
        };

        // Add payment amounts (all validated to be non-negative)
        if (request.CashSum > 0)
        {
            payload["CashSum"] = request.CashSum;
        }

        if (request.TransferSum > 0)
        {
            payload["TransferSum"] = request.TransferSum;
            if (!string.IsNullOrWhiteSpace(request.TransferReference))
            {
                payload["TransferReference"] = request.TransferReference;
            }
            if (!string.IsNullOrWhiteSpace(request.TransferDate))
            {
                payload["TransferDate"] = request.TransferDate;
            }
            if (!string.IsNullOrWhiteSpace(request.TransferAccount))
            {
                payload["TransferAccount"] = request.TransferAccount;
            }
        }

        if (request.CheckSum > 0)
        {
            payload["CheckSum"] = request.CheckSum;
        }

        if (request.CreditSum > 0)
        {
            payload["CreditSum"] = request.CreditSum;
        }

        if (!string.IsNullOrWhiteSpace(request.Remarks))
        {
            payload["Remarks"] = request.Remarks;
        }

        // Add payment invoices
        if (request.PaymentInvoices != null && request.PaymentInvoices.Count > 0)
        {
            var paymentInvoices = request.PaymentInvoices.Select((inv, i) =>
            {
                if (inv.SumApplied < 0)
                {
                    throw new ArgumentException($"Invoice {i + 1}: Sum applied cannot be negative");
                }
                return new
                {
                    DocEntry = inv.DocEntry,
                    SumApplied = inv.SumApplied,
                    InvoiceType = inv.InvoiceType ?? "it_Invoice"
                };
            }).ToList();
            payload["PaymentInvoices"] = paymentInvoices;
        }

        // Add payment checks
        if (request.PaymentChecks != null && request.PaymentChecks.Count > 0)
        {
            var paymentChecks = request.PaymentChecks.Select((chk, i) =>
            {
                if (chk.CheckSum < 0)
                {
                    throw new ArgumentException($"Check {i + 1}: Check sum cannot be negative");
                }
                return new
                {
                    DueDate = chk.DueDate,
                    CheckNumber = chk.CheckNumber,
                    BankCode = chk.BankCode,
                    Branch = chk.Branch,
                    AccountNum = chk.AccountNum,
                    CheckSum = chk.CheckSum
                };
            }).ToList();
            payload["PaymentChecks"] = paymentChecks;
        }

        // Add payment credit cards
        if (request.PaymentCreditCards != null && request.PaymentCreditCards.Count > 0)
        {
            var paymentCreditCards = request.PaymentCreditCards.Select((cc, i) =>
            {
                if (cc.CreditSum < 0)
                {
                    throw new ArgumentException($"Credit card {i + 1}: Credit sum cannot be negative");
                }
                return new
                {
                    CreditCard = cc.CreditCard,
                    CreditCardNumber = cc.CreditCardNumber,
                    CardValidUntil = cc.CardValidUntil,
                    VoucherNum = cc.VoucherNum,
                    CreditSum = cc.CreditSum
                };
            }).ToList();
            payload["PaymentCreditCards"] = paymentCreditCards;
        }

        var jsonPayload = JsonSerializer.Serialize(payload);
        _logger.LogDebug("Creating incoming payment with payload: {Payload}", jsonPayload);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "IncomingPayments")
        {
            Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
        };
        httpRequest.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await _httpClient.SendAsync(httpRequest, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _sessionId = null;
            await EnsureAuthenticatedAsync(cancellationToken);

            httpRequest = new HttpRequestMessage(HttpMethod.Post, "IncomingPayments")
            {
                Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
            };
            httpRequest.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
            httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        }

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Failed to create incoming payment: {StatusCode} - {Error}",
                response.StatusCode, responseContent);
            throw new Exception($"Failed to create incoming payment: {response.StatusCode} - {responseContent}");
        }

        var createdPayment = JsonSerializer.Deserialize<IncomingPayment>(responseContent);

        _logger.LogInformation("Incoming payment created successfully. DocEntry: {DocEntry}, DocNum: {DocNum}, Customer: {CardCode}",
            createdPayment?.DocEntry, createdPayment?.DocNum, request.CardCode);

        return createdPayment ?? throw new Exception("Failed to deserialize created incoming payment");
    }

    private void ValidateIncomingPaymentRequest(CreateIncomingPaymentRequest request)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(request.CardCode))
        {
            errors.Add("Customer code (CardCode) is required");
        }

        // CRITICAL: Validate all amounts are non-negative
        if (request.CashSum < 0)
        {
            errors.Add("Cash sum cannot be negative");
        }

        if (request.TransferSum < 0)
        {
            errors.Add("Transfer sum cannot be negative");
        }

        if (request.CheckSum < 0)
        {
            errors.Add("Check sum cannot be negative");
        }

        if (request.CreditSum < 0)
        {
            errors.Add("Credit sum cannot be negative");
        }

        // Validate at least one payment method has a positive amount
        var totalPayment = request.CashSum + request.TransferSum + request.CheckSum + request.CreditSum;
        if (totalPayment <= 0)
        {
            errors.Add("At least one payment amount must be greater than zero");
        }

        // Validate payment invoices
        if (request.PaymentInvoices != null)
        {
            for (int i = 0; i < request.PaymentInvoices.Count; i++)
            {
                var inv = request.PaymentInvoices[i];
                if (inv.SumApplied < 0)
                {
                    errors.Add($"Invoice {i + 1}: Sum applied cannot be negative");
                }
            }
        }

        // Validate payment checks
        if (request.PaymentChecks != null)
        {
            for (int i = 0; i < request.PaymentChecks.Count; i++)
            {
                var chk = request.PaymentChecks[i];
                if (chk.CheckSum < 0)
                {
                    errors.Add($"Check {i + 1}: Check sum cannot be negative");
                }
            }
        }

        // Validate payment credit cards
        if (request.PaymentCreditCards != null)
        {
            for (int i = 0; i < request.PaymentCreditCards.Count; i++)
            {
                var cc = request.PaymentCreditCards[i];
                if (cc.CreditSum < 0)
                {
                    errors.Add($"Credit card {i + 1}: Credit sum cannot be negative");
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join("; ", errors));
        }
    }

    #endregion

    #region G/L Account Operations

    public async Task<List<GLAccountDto>> GetGLAccountsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var allAccounts = new List<GLAccountDto>();
        var skip = 0;
        const int pageSize = 100;
        bool hasMore = true;

        while (hasMore)
        {
            // Filter for active accounts that are postable
            var url = $"ChartOfAccounts?$filter=ActiveAccount eq 'tYES'&$orderby=Code&$skip={skip}";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Add("Prefer", $"odata.maxpagesize={pageSize}");

            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _sessionId = null;
                await EnsureAuthenticatedAsync(cancellationToken);

                request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.Add("Prefer", $"odata.maxpagesize={pageSize}");
                response = await _httpClient.SendAsync(request, cancellationToken);
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Failed to get G/L accounts: {StatusCode} - {Error}", response.StatusCode, errorContent);
                throw new Exception($"Failed to get G/L accounts: {response.StatusCode} - {errorContent}");
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var pageAccounts = ParseGLAccountsFromResponse(content);
            allAccounts.AddRange(pageAccounts);

            _logger.LogInformation("Retrieved {PageCount} G/L accounts at skip={Skip}, total: {Total}",
                pageAccounts.Count, skip, allAccounts.Count);

            using var doc = JsonDocument.Parse(content);
            hasMore = doc.RootElement.TryGetProperty("odata.nextLink", out _) ||
                      doc.RootElement.TryGetProperty("@odata.nextLink", out _);

            if (!hasMore && pageAccounts.Count == pageSize)
            {
                hasMore = true;
            }

            if (pageAccounts.Count == 0)
            {
                hasMore = false;
            }

            skip += pageAccounts.Count > 0 ? pageAccounts.Count : pageSize;
        }

        return allAccounts;
    }

    public async Task<List<GLAccountDto>> GetGLAccountsByTypeAsync(string accountType, CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var allAccounts = new List<GLAccountDto>();
        var skip = 0;
        const int pageSize = 100;
        bool hasMore = true;

        while (hasMore)
        {
            var url = $"ChartOfAccounts?$filter=ActiveAccount eq 'tYES' and AccountType eq '{accountType}'&$orderby=Code&$skip={skip}";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Add("Prefer", $"odata.maxpagesize={pageSize}");

            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _sessionId = null;
                await EnsureAuthenticatedAsync(cancellationToken);

                request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.Add("Prefer", $"odata.maxpagesize={pageSize}");
                response = await _httpClient.SendAsync(request, cancellationToken);
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Failed to get G/L accounts by type: {StatusCode} - {Error}", response.StatusCode, errorContent);
                throw new Exception($"Failed to get G/L accounts: {response.StatusCode} - {errorContent}");
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var pageAccounts = ParseGLAccountsFromResponse(content);
            allAccounts.AddRange(pageAccounts);

            using var doc = JsonDocument.Parse(content);
            hasMore = doc.RootElement.TryGetProperty("odata.nextLink", out _) ||
                      doc.RootElement.TryGetProperty("@odata.nextLink", out _);

            if (!hasMore && pageAccounts.Count == pageSize)
            {
                hasMore = true;
            }

            if (pageAccounts.Count == 0)
            {
                hasMore = false;
            }

            skip += pageAccounts.Count > 0 ? pageAccounts.Count : pageSize;
        }

        return allAccounts;
    }

    public async Task<GLAccountDto?> GetGLAccountByCodeAsync(string accountCode, CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var url = $"ChartOfAccounts('{accountCode}')";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _sessionId = null;
            await EnsureAuthenticatedAsync(cancellationToken);

            request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            response = await _httpClient.SendAsync(request, cancellationToken);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Failed to get G/L account {AccountCode}: {StatusCode} - {Error}", accountCode, response.StatusCode, errorContent);
            throw new Exception($"Failed to get G/L account: {response.StatusCode} - {errorContent}");
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseSingleGLAccountFromResponse(content);
    }

    private List<GLAccountDto> ParseGLAccountsFromResponse(string jsonContent)
    {
        var accounts = new List<GLAccountDto>();
        try
        {
            using var doc = JsonDocument.Parse(jsonContent);
            if (doc.RootElement.TryGetProperty("value", out var valueArray))
            {
                foreach (var item in valueArray.EnumerateArray())
                {
                    var account = ParseGLAccountElement(item);
                    accounts.Add(account);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse G/L accounts from response");
        }
        return accounts;
    }

    private GLAccountDto? ParseSingleGLAccountFromResponse(string jsonContent)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonContent);
            return ParseGLAccountElement(doc.RootElement);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse single G/L account from response");
            return null;
        }
    }

    private GLAccountDto ParseGLAccountElement(JsonElement item)
    {
        var isActive = true;
        if (item.TryGetProperty("ActiveAccount", out var activeAccount))
        {
            isActive = activeAccount.ValueKind == JsonValueKind.String
                ? activeAccount.GetString() == "tYES"
                : activeAccount.ValueKind == JsonValueKind.True;
        }

        return new GLAccountDto
        {
            Code = item.TryGetProperty("Code", out var code) ? code.GetString() : null,
            Name = item.TryGetProperty("Name", out var name) ? name.GetString() : null,
            AccountType = item.TryGetProperty("AccountType", out var accountType) ? accountType.GetString() : null,
            Currency = item.TryGetProperty("AccountCurrency", out var currency) ? currency.GetString() : null,
            Balance = item.TryGetProperty("Balance", out var balance) && balance.ValueKind == JsonValueKind.Number
                ? balance.GetDecimal()
                : 0,
            IsActive = isActive
        };
    }

    #endregion
}
