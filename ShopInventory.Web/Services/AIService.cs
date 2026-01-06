using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace ShopInventory.Web.Services;

public class AISettings
{
    public string Provider { get; set; } = "OpenAI"; // OpenAI, AzureOpenAI, Ollama
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "gpt-4o-mini";
    public string Endpoint { get; set; } = "https://api.openai.com/v1";
    public string AzureDeploymentName { get; set; } = string.Empty;
    public double Temperature { get; set; } = 0.7;
    public int MaxTokens { get; set; } = 2048;
}

public interface IAIService
{
    Task<string> ChatAsync(string message, List<ChatMessage>? history = null, string? systemPrompt = null);
    Task<AIInsight> GetInventoryInsightsAsync();
    Task<SalesForecast> GetSalesForecastAsync(string? itemCode = null);
    Task<List<ProductRecommendation>> GetRecommendationsAsync(string customerCode);
    Task<AnomalyReport> DetectAnomaliesAsync();
    Task<string> AnalyzeDataAsync(string query, object data);
    bool IsConfigured { get; }
}

public class ChatMessage
{
    public string Role { get; set; } = "user"; // user, assistant, system
    public string Content { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class AIInsight
{
    public string Summary { get; set; } = string.Empty;
    public List<InsightItem> Insights { get; set; } = new();
    public List<string> Recommendations { get; set; } = new();
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
}

public class InsightItem
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty; // inventory, sales, performance, alerts
    public string Severity { get; set; } = "info"; // info, warning, critical
    public string Icon { get; set; } = "bi-lightbulb";
}

public class SalesForecast
{
    public string ItemCode { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public List<ForecastPoint> Predictions { get; set; } = new();
    public double Confidence { get; set; }
    public string Trend { get; set; } = "stable"; // increasing, decreasing, stable
    public string Analysis { get; set; } = string.Empty;
}

public class ForecastPoint
{
    public DateTime Date { get; set; }
    public decimal PredictedQuantity { get; set; }
    public decimal LowerBound { get; set; }
    public decimal UpperBound { get; set; }
}

public class ProductRecommendation
{
    public string ItemCode { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public double Score { get; set; }
    public decimal SuggestedQuantity { get; set; }
}

public class AnomalyReport
{
    public List<AnomalyItem> Anomalies { get; set; } = new();
    public int TotalAnomalies { get; set; }
    public string OverallRisk { get; set; } = "low"; // low, medium, high
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
}

public class AnomalyItem
{
    public string Type { get; set; } = string.Empty; // price, quantity, sales_pattern, stock_level
    public string Description { get; set; } = string.Empty;
    public string AffectedItem { get; set; } = string.Empty;
    public string Severity { get; set; } = "warning";
    public object? Data { get; set; }
}

public class AIService : IAIService
{
    private readonly AISettings _settings;
    private readonly HttpClient _httpClient;
    private readonly IInvoiceService _invoiceService;
    private readonly IMasterDataCacheService _masterDataService;
    private readonly ILogger<AIService> _logger;

    private readonly string _inventorySystemPrompt = @"You are an intelligent AI assistant for ShopInventory, a retail inventory management system integrated with SAP Business One. 
Your role is to help users with:
- Understanding inventory levels and stock status
- Analyzing sales patterns and trends
- Providing business insights and recommendations
- Answering questions about invoices, payments, and customers
- Suggesting optimal reorder points and quantities

Be concise, helpful, and data-driven in your responses. When analyzing data, provide specific numbers and actionable insights.
Format your responses with clear sections when appropriate. Use bullet points for lists.";

    public AIService(
        IOptions<AISettings> settings,
        HttpClient httpClient,
        IInvoiceService invoiceService,
        IMasterDataCacheService masterDataService,
        ILogger<AIService> logger)
    {
        _settings = settings.Value;
        _httpClient = httpClient;
        _invoiceService = invoiceService;
        _masterDataService = masterDataService;
        _logger = logger;
    }

    public bool IsConfigured => !string.IsNullOrEmpty(_settings.ApiKey) || _settings.Provider == "Ollama";

    public async Task<string> ChatAsync(string message, List<ChatMessage>? history = null, string? systemPrompt = null)
    {
        if (!IsConfigured)
        {
            return "AI is not configured. Please add your API key in the settings.";
        }

        try
        {
            var messages = new List<object>
            {
                new { role = "system", content = systemPrompt ?? _inventorySystemPrompt }
            };

            if (history != null)
            {
                foreach (var msg in history.TakeLast(10))
                {
                    messages.Add(new { role = msg.Role, content = msg.Content });
                }
            }

            messages.Add(new { role = "user", content = message });

            return _settings.Provider switch
            {
                "OpenAI" => await CallOpenAIAsync(messages),
                "AzureOpenAI" => await CallAzureOpenAIAsync(messages),
                "Ollama" => await CallOllamaAsync(messages),
                _ => await CallOpenAIAsync(messages)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling AI service");
            return $"Sorry, I encountered an error: {ex.Message}";
        }
    }

    public async Task<AIInsight> GetInventoryInsightsAsync()
    {
        var insights = new AIInsight();

        try
        {
            // Get recent data for analysis
            var products = await _masterDataService.GetProductsAsync();
            var recentInvoices = await _invoiceService.GetInvoicesAsync(1, 100);

            // Build context
            var context = new StringBuilder();
            context.AppendLine("Inventory Status:");

            var lowStockCount = 0;
            var outOfStockCount = 0;
            var totalProducts = products?.Count ?? 0;

            if (products != null)
            {
                foreach (var p in products.Take(50))
                {
                    if (p.QuantityOnStock <= 0) outOfStockCount++;
                    else if (p.QuantityOnStock < 10) lowStockCount++;
                }
                context.AppendLine($"- Total Products: {totalProducts}");
                context.AppendLine($"- Low Stock Items: {lowStockCount}");
                context.AppendLine($"- Out of Stock: {outOfStockCount}");
            }

            if (recentInvoices?.Invoices != null)
            {
                var totalSales = recentInvoices.Invoices.Sum(i => i.DocTotal);
                var avgOrderValue = recentInvoices.Invoices.Any() ? recentInvoices.Invoices.Average(i => i.DocTotal) : 0;
                context.AppendLine($"\nRecent Sales (last {recentInvoices.Invoices.Count} invoices):");
                context.AppendLine($"- Total Revenue: ${totalSales:N2}");
                context.AppendLine($"- Average Order Value: ${avgOrderValue:N2}");
            }

            var prompt = $@"Based on this inventory and sales data, provide 3-5 key business insights and recommendations:

{context}

Respond in JSON format:
{{
  ""summary"": ""A brief overall summary"",
  ""insights"": [
    {{""title"": ""..."", ""description"": ""..."", ""category"": ""inventory|sales|performance|alerts"", ""severity"": ""info|warning|critical""}}
  ],
  ""recommendations"": [""Action 1"", ""Action 2""]
}}";

            var response = await ChatAsync(prompt, systemPrompt: "You are a business analytics AI. Respond only with valid JSON.");

            try
            {
                // Try to parse JSON response
                var jsonStart = response.IndexOf('{');
                var jsonEnd = response.LastIndexOf('}') + 1;
                if (jsonStart >= 0 && jsonEnd > jsonStart)
                {
                    var json = response.Substring(jsonStart, jsonEnd - jsonStart);
                    var parsed = JsonSerializer.Deserialize<AIInsight>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                    if (parsed != null)
                    {
                        insights = parsed;
                    }
                }
            }
            catch
            {
                // If JSON parsing fails, create basic insight from response
                insights.Summary = response;
                insights.Insights.Add(new InsightItem
                {
                    Title = "AI Analysis",
                    Description = response,
                    Category = "performance",
                    Severity = "info"
                });
            }

            // Add stock alerts as insights
            if (outOfStockCount > 0)
            {
                insights.Insights.Insert(0, new InsightItem
                {
                    Title = $"{outOfStockCount} Items Out of Stock",
                    Description = "These items need immediate restocking to avoid lost sales.",
                    Category = "alerts",
                    Severity = "critical",
                    Icon = "bi-exclamation-triangle"
                });
            }

            if (lowStockCount > 0)
            {
                insights.Insights.Insert(0, new InsightItem
                {
                    Title = $"{lowStockCount} Items Running Low",
                    Description = "Consider reordering these items soon.",
                    Category = "inventory",
                    Severity = "warning",
                    Icon = "bi-box-seam"
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating inventory insights");
            insights.Summary = "Unable to generate insights at this time.";
        }

        insights.GeneratedAt = DateTime.UtcNow;
        return insights;
    }

    public async Task<SalesForecast> GetSalesForecastAsync(string? itemCode = null)
    {
        var forecast = new SalesForecast { ItemCode = itemCode ?? "ALL" };

        try
        {
            var recentInvoices = await _invoiceService.GetInvoicesAsync(1, 200);

            var prompt = $@"Based on recent sales data, provide a 7-day sales forecast.
Recent invoice count: {recentInvoices?.Invoices?.Count ?? 0}
Total sales value: ${recentInvoices?.Invoices?.Sum(i => i.DocTotal) ?? 0:N2}

Provide forecast in JSON:
{{
  ""trend"": ""increasing|decreasing|stable"",
  ""confidence"": 0.75,
  ""analysis"": ""Brief analysis of the trend"",
  ""predictions"": [
    {{""date"": ""2026-01-06"", ""predictedQuantity"": 100, ""lowerBound"": 80, ""upperBound"": 120}}
  ]
}}";

            var response = await ChatAsync(prompt, systemPrompt: "You are a sales forecasting AI. Respond only with valid JSON.");

            try
            {
                var jsonStart = response.IndexOf('{');
                var jsonEnd = response.LastIndexOf('}') + 1;
                if (jsonStart >= 0 && jsonEnd > jsonStart)
                {
                    var json = response.Substring(jsonStart, jsonEnd - jsonStart);
                    var parsed = JsonSerializer.Deserialize<SalesForecast>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                    if (parsed != null)
                    {
                        forecast = parsed;
                        forecast.ItemCode = itemCode ?? "ALL";
                    }
                }
            }
            catch
            {
                forecast.Analysis = response;
                forecast.Trend = "stable";
                forecast.Confidence = 0.5;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating sales forecast");
            forecast.Analysis = "Unable to generate forecast at this time.";
        }

        return forecast;
    }

    public async Task<List<ProductRecommendation>> GetRecommendationsAsync(string customerCode)
    {
        var recommendations = new List<ProductRecommendation>();

        try
        {
            var products = await _masterDataService.GetProductsAsync();
            var topProducts = products?.OrderByDescending(p => p.QuantityOnStock).Take(10).ToList();

            if (topProducts == null || !topProducts.Any())
                return recommendations;

            var prompt = $@"Based on customer {customerCode}'s purchase history and these available products:
{string.Join("\n", topProducts.Select(p => $"- {p.ItemCode}: {p.ItemName} (In Stock: {p.QuantityOnStock})"))}

Suggest 3-5 products they might be interested in. Respond in JSON:
{{
  ""recommendations"": [
    {{""itemCode"": ""..."", ""itemName"": ""..."", ""reason"": ""Why recommended"", ""score"": 0.85, ""suggestedQuantity"": 5}}
  ]
}}";

            var response = await ChatAsync(prompt, systemPrompt: "You are a product recommendation AI. Respond only with valid JSON.");

            try
            {
                var jsonStart = response.IndexOf('{');
                var jsonEnd = response.LastIndexOf('}') + 1;
                if (jsonStart >= 0 && jsonEnd > jsonStart)
                {
                    var json = response.Substring(jsonStart, jsonEnd - jsonStart);
                    var result = JsonSerializer.Deserialize<JsonElement>(json);
                    if (result.TryGetProperty("recommendations", out var recs))
                    {
                        recommendations = JsonSerializer.Deserialize<List<ProductRecommendation>>(recs.GetRawText(),
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
                    }
                }
            }
            catch
            {
                // Return empty list on parse failure
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating recommendations");
        }

        return recommendations;
    }

    public async Task<AnomalyReport> DetectAnomaliesAsync()
    {
        var report = new AnomalyReport();

        try
        {
            var products = await _masterDataService.GetProductsAsync();
            var invoices = await _invoiceService.GetInvoicesAsync(1, 100);

            var context = new StringBuilder();
            context.AppendLine("Inventory Data:");
            if (products != null)
            {
                var negativeStock = products.Where(p => p.QuantityOnStock < 0).ToList();
                var highStock = products.Where(p => p.QuantityOnStock > 1000).ToList();

                context.AppendLine($"- Products with negative stock: {negativeStock.Count}");
                context.AppendLine($"- Products with unusually high stock (>1000): {highStock.Count}");

                foreach (var p in negativeStock.Take(5))
                {
                    report.Anomalies.Add(new AnomalyItem
                    {
                        Type = "stock_level",
                        Description = $"Negative inventory: {p.QuantityOnStock} units",
                        AffectedItem = $"{p.ItemCode} - {p.ItemName}",
                        Severity = "critical"
                    });
                }
            }

            if (invoices?.Invoices != null)
            {
                var avgTotal = invoices.Invoices.Average(i => i.DocTotal);
                var outliers = invoices.Invoices.Where(i => i.DocTotal > avgTotal * 3).ToList();

                context.AppendLine($"\nInvoice Analysis:");
                context.AppendLine($"- Average invoice value: ${avgTotal:N2}");
                context.AppendLine($"- Unusually large invoices: {outliers.Count}");

                foreach (var inv in outliers.Take(3))
                {
                    report.Anomalies.Add(new AnomalyItem
                    {
                        Type = "sales_pattern",
                        Description = $"Invoice total ${inv.DocTotal:N2} is {(inv.DocTotal / avgTotal):N1}x above average",
                        AffectedItem = $"Invoice #{inv.DocNum}",
                        Severity = "warning"
                    });
                }
            }

            report.TotalAnomalies = report.Anomalies.Count;
            report.OverallRisk = report.Anomalies.Any(a => a.Severity == "critical") ? "high" :
                                 report.Anomalies.Any(a => a.Severity == "warning") ? "medium" : "low";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error detecting anomalies");
        }

        report.GeneratedAt = DateTime.UtcNow;
        return report;
    }

    public async Task<string> AnalyzeDataAsync(string query, object data)
    {
        var dataJson = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        var prompt = $@"Analyze this data and answer the following question:

Question: {query}

Data:
{dataJson}

Provide a clear, concise analysis.";

        return await ChatAsync(prompt);
    }

    private async Task<string> CallOpenAIAsync(List<object> messages)
    {
        // Convert messages to single input string for Responses API
        var inputText = string.Join("\n", messages.Select(m =>
        {
            var msg = (dynamic)m;
            return $"{msg.role}: {msg.content}";
        }));

        var request = new
        {
            model = _settings.Model,
            input = inputText
        };

        // Retry logic with exponential backoff for rate limits
        var maxRetries = 3;
        var delay = TimeSpan.FromSeconds(2);

        // Build the correct URL for Responses API
        var baseUrl = _settings.Endpoint.TrimEnd('/');
        var requestUrl = $"{baseUrl}/responses";

        _logger.LogInformation("Calling OpenAI Responses API at {Url} with model {Model}", requestUrl, _settings.Model);

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            using var requestMessage = new HttpRequestMessage(HttpMethod.Post, requestUrl);
            requestMessage.Headers.Add("Authorization", $"Bearer {_settings.ApiKey}");
            requestMessage.Content = JsonContent.Create(request);

            var response = await _httpClient.SendAsync(requestMessage);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<JsonElement>();
                // Responses API returns output array with message items containing content
                var outputArray = result.GetProperty("output");
                foreach (var outputItem in outputArray.EnumerateArray())
                {
                    if (outputItem.GetProperty("type").GetString() == "message")
                    {
                        var contentArray = outputItem.GetProperty("content");
                        foreach (var contentItem in contentArray.EnumerateArray())
                        {
                            if (contentItem.GetProperty("type").GetString() == "output_text")
                            {
                                return contentItem.GetProperty("text").GetString() ?? "";
                            }
                        }
                    }
                }
                return "";
            }

            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError("OpenAI API error (attempt {Attempt}): {StatusCode} - {Content}", attempt + 1, response.StatusCode, errorContent);

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                if (attempt < maxRetries)
                {
                    _logger.LogWarning("Rate limited, waiting {Delay} seconds before retry...", delay.TotalSeconds);
                    await Task.Delay(delay);
                    delay *= 2; // Exponential backoff
                    continue;
                }
                throw new Exception("Rate limit exceeded after retries. Your OpenAI account may have per-minute request limits. Please wait a minute and try again.");
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                throw new Exception("Invalid API key. Please check your OpenAI API key in settings.");
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                throw new Exception($"Model '{_settings.Model}' not found or Responses API not available.");
            }

            throw new Exception($"OpenAI API error: {response.StatusCode} - {errorContent}");
        }

        throw new Exception("Failed to get response from OpenAI after retries.");
    }

    private async Task<string> CallAzureOpenAIAsync(List<object> messages)
    {
        var request = new
        {
            messages = messages,
            temperature = _settings.Temperature,
            max_tokens = _settings.MaxTokens
        };

        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("api-key", _settings.ApiKey);

        var endpoint = $"{_settings.Endpoint}/openai/deployments/{_settings.AzureDeploymentName}/chat/completions?api-version=2024-02-15-preview";
        var response = await _httpClient.PostAsJsonAsync(endpoint, request);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        return result.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    private async Task<string> CallOllamaAsync(List<object> messages)
    {
        var request = new
        {
            model = _settings.Model,
            messages = messages,
            stream = false
        };

        var endpoint = string.IsNullOrEmpty(_settings.Endpoint) ? "http://localhost:11434" : _settings.Endpoint;
        var response = await _httpClient.PostAsJsonAsync($"{endpoint}/api/chat", request);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        return result.GetProperty("message").GetProperty("content").GetString() ?? "";
    }
}
