using System.Text.Json;

namespace ShopInventory.Web.Services;

public interface IGlobalSearchService
{
    Task<GlobalSearchResult> SearchAsync(string query, CancellationToken cancellationToken = default);
}

public class GlobalSearchResult
{
    public List<SearchResultItem> Invoices { get; set; } = new();
    public List<SearchResultItem> Products { get; set; } = new();
    public List<SearchResultItem> Customers { get; set; } = new();
    public List<SearchResultItem> Payments { get; set; } = new();
    public int TotalResults => Invoices.Count + Products.Count + Customers.Count + Payments.Count;
}

public class SearchResultItem
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}

public class GlobalSearchService : IGlobalSearchService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GlobalSearchService> _logger;

    public GlobalSearchService(HttpClient httpClient, ILogger<GlobalSearchService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<GlobalSearchResult> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var result = new GlobalSearchResult();

        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
            return result;

        var searchTasks = new List<Task>
        {
            SearchInvoicesAsync(query, result, cancellationToken),
            SearchProductsAsync(query, result, cancellationToken),
            SearchCustomersAsync(query, result, cancellationToken),
            SearchPaymentsAsync(query, result, cancellationToken)
        };

        try
        {
            await Task.WhenAll(searchTasks);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during global search for query: {Query}", query);
        }

        return result;
    }

    private async Task SearchInvoicesAsync(string query, GlobalSearchResult result, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.GetAsync($"api/invoice?top=10", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("value", out var items))
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        var docNum = item.GetProperty("DocNum").GetInt32().ToString();
                        var cardCode = item.TryGetProperty("CardCode", out var cc) ? cc.GetString() ?? "" : "";
                        var cardName = item.TryGetProperty("CardName", out var cn) ? cn.GetString() ?? "" : "";
                        var docEntry = item.GetProperty("DocEntry").GetInt32();
                        var docTotal = item.TryGetProperty("DocTotal", out var dt) ? dt.GetDecimal() : 0;

                        if (docNum.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                            cardCode.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                            cardName.Contains(query, StringComparison.OrdinalIgnoreCase))
                        {
                            result.Invoices.Add(new SearchResultItem
                            {
                                Id = docEntry.ToString(),
                                Title = $"Invoice #{docNum}",
                                Subtitle = $"{cardName} - ${docTotal:N2}",
                                Category = "Invoice",
                                Icon = "receipt",
                                Url = $"/invoices/{docEntry}"
                            });
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error searching invoices");
        }
    }

    private async Task SearchProductsAsync(string query, GlobalSearchResult result, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.GetAsync($"api/product/search?query={Uri.EscapeDataString(query)}&top=10", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("value", out var items))
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        var itemCode = item.TryGetProperty("ItemCode", out var ic) ? ic.GetString() ?? "" : "";
                        var itemName = item.TryGetProperty("ItemName", out var iname) ? iname.GetString() ?? "" : "";
                        var onHand = item.TryGetProperty("QuantityOnStock", out var qos) ? qos.GetDecimal() : 0;

                        result.Products.Add(new SearchResultItem
                        {
                            Id = itemCode,
                            Title = itemName,
                            Subtitle = $"{itemCode} - {onHand:N0} in stock",
                            Category = "Product",
                            Icon = "box",
                            Url = $"/products?search={Uri.EscapeDataString(itemCode)}"
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error searching products");
        }
    }

    private async Task SearchCustomersAsync(string query, GlobalSearchResult result, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.GetAsync($"api/businesspartner/search?query={Uri.EscapeDataString(query)}&top=10", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("value", out var items))
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        var cardCode = item.TryGetProperty("CardCode", out var cc) ? cc.GetString() ?? "" : "";
                        var cardName = item.TryGetProperty("CardName", out var cn) ? cn.GetString() ?? "" : "";
                        var balance = item.TryGetProperty("CurrentAccountBalance", out var cab) ? cab.GetDecimal() : 0;

                        result.Customers.Add(new SearchResultItem
                        {
                            Id = cardCode,
                            Title = cardName,
                            Subtitle = $"{cardCode} - Balance: ${balance:N2}",
                            Category = "Customer",
                            Icon = "person",
                            Url = $"/invoices?customer={Uri.EscapeDataString(cardCode)}"
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error searching customers");
        }
    }

    private async Task SearchPaymentsAsync(string query, GlobalSearchResult result, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.GetAsync($"api/payment?top=10", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("value", out var items))
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        var docNum = item.TryGetProperty("DocNum", out var dn) ? dn.GetInt32().ToString() : "";
                        var cardName = item.TryGetProperty("CardName", out var cn) ? cn.GetString() ?? "" : "";
                        var docEntry = item.TryGetProperty("DocEntry", out var de) ? de.GetInt32() : 0;
                        var docTotal = item.TryGetProperty("DocTotal", out var dt) ? dt.GetDecimal() : 0;

                        if (docNum.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                            cardName.Contains(query, StringComparison.OrdinalIgnoreCase))
                        {
                            result.Payments.Add(new SearchResultItem
                            {
                                Id = docEntry.ToString(),
                                Title = $"Payment #{docNum}",
                                Subtitle = $"{cardName} - ${docTotal:N2}",
                                Category = "Payment",
                                Icon = "cash",
                                Url = $"/payments"
                            });
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error searching payments");
        }
    }
}
