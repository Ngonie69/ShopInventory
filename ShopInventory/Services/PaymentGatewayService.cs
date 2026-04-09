using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ShopInventory.Services;

/// <summary>
/// Service for handling payments through multiple Zimbabwean payment gateways
/// </summary>
public interface IPaymentGatewayService
{
    Task<InitiatePaymentResponse> InitiatePaymentAsync(InitiatePaymentRequest request, string? initiatedBy = null);
    Task<PaymentStatusResponse?> CheckStatusAsync(int transactionId);
    Task<PaymentStatusResponse?> CheckStatusByExternalIdAsync(string externalId);
    Task<bool> ProcessCallbackAsync(string provider, PaymentCallbackPayload payload);
    Task<PaymentTransactionListResponse> GetTransactionsAsync(string? provider = null, string? status = null, int page = 1, int pageSize = 50);
    Task<PaymentProvidersResponse> GetAvailableProvidersAsync();
    Task<bool> RefundPaymentAsync(int transactionId, decimal? amount = null);
    Task<bool> CancelPaymentAsync(int transactionId);
}

public class PaymentGatewayService : IPaymentGatewayService
{
    private readonly ApplicationDbContext _context;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PaymentGatewaySettings _settings;
    private readonly IWebhookService _webhookService;
    private readonly ILogger<PaymentGatewayService> _logger;

    public PaymentGatewayService(
        ApplicationDbContext context,
        IHttpClientFactory httpClientFactory,
        IOptions<PaymentGatewaySettings> settings,
        IWebhookService webhookService,
        ILogger<PaymentGatewayService> logger)
    {
        _context = context;
        _httpClientFactory = httpClientFactory;
        _settings = settings.Value;
        _webhookService = webhookService;
        _logger = logger;
    }

    public async Task<InitiatePaymentResponse> InitiatePaymentAsync(InitiatePaymentRequest request, string? initiatedBy = null)
    {
        // Validate provider
        if (!PaymentProviders.All.Contains(request.Provider))
        {
            throw new ArgumentException($"Invalid payment provider: {request.Provider}");
        }

        // Create transaction record
        var transaction = new PaymentTransaction
        {
            Provider = request.Provider,
            PaymentMethod = request.Provider,
            Amount = request.Amount,
            Currency = request.Currency,
            PhoneNumber = request.PhoneNumber,
            Reference = request.Reference ?? $"INV-{DateTime.UtcNow:yyyyMMddHHmmss}",
            InvoiceId = request.InvoiceId,
            CustomerCode = request.CustomerCode,
            CallbackUrl = request.CallbackUrl,
            InitiatedBy = initiatedBy,
            Status = PaymentStatus.Pending
        };

        _context.PaymentTransactions.Add(transaction);
        await _context.SaveChangesAsync();

        try
        {
            var response = request.Provider switch
            {
                PaymentProviders.PayNow => await InitiatePayNowPaymentAsync(transaction, request),
                PaymentProviders.Innbucks => await InitiateInnbucksPaymentAsync(transaction, request),
                PaymentProviders.Ecocash => await InitiateEcocashPaymentAsync(transaction, request),
                _ => throw new NotSupportedException($"Provider {request.Provider} not supported")
            };

            response.TransactionId = transaction.Id;
            await _context.SaveChangesAsync();

            _logger.LogInformation("Payment initiated: {TransactionId} via {Provider} for {Amount} {Currency}",
                transaction.Id, request.Provider, request.Amount, request.Currency);

            return response;
        }
        catch (Exception ex)
        {
            transaction.Status = PaymentStatus.Failed;
            transaction.StatusMessage = ex.Message;
            await _context.SaveChangesAsync();

            _logger.LogError(ex, "Failed to initiate payment via {Provider}", request.Provider);
            throw;
        }
    }

    public async Task<PaymentStatusResponse?> CheckStatusAsync(int transactionId)
    {
        var transaction = await _context.PaymentTransactions.FindAsync(transactionId);
        if (transaction == null) return null;

        // If pending, try to get updated status from provider
        if (transaction.Status == PaymentStatus.Pending || transaction.Status == PaymentStatus.Processing)
        {
            await RefreshStatusFromProviderAsync(transaction);
        }

        return MapToStatusResponse(transaction);
    }

    public async Task<PaymentStatusResponse?> CheckStatusByExternalIdAsync(string externalId)
    {
        var transaction = await _context.PaymentTransactions
            .FirstOrDefaultAsync(t => t.ExternalTransactionId == externalId);

        if (transaction == null) return null;

        return MapToStatusResponse(transaction);
    }

    public async Task<bool> ProcessCallbackAsync(string provider, PaymentCallbackPayload payload)
    {
        try
        {
            var normalizedProvider = NormalizeProvider(provider);
            if (string.IsNullOrEmpty(normalizedProvider))
            {
                _logger.LogWarning("Callback received for unsupported provider {Provider}", provider);
                return false;
            }

            if (!IsProviderEnabled(normalizedProvider))
            {
                _logger.LogWarning("Callback rejected because provider {Provider} is disabled", normalizedProvider);
                return false;
            }

            PaymentTransaction? transaction = null;

            // Find transaction by external ID or internal ID
            if (!string.IsNullOrEmpty(payload.ExternalTransactionId))
            {
                transaction = await _context.PaymentTransactions
                    .FirstOrDefaultAsync(t => t.Provider == normalizedProvider && t.ExternalTransactionId == payload.ExternalTransactionId);
            }

            if (transaction == null && !string.IsNullOrEmpty(payload.Reference))
            {
                transaction = await _context.PaymentTransactions
                    .FirstOrDefaultAsync(t => t.Provider == normalizedProvider && t.Reference == payload.Reference);
            }

            if (transaction == null && !string.IsNullOrEmpty(payload.TransactionId) && int.TryParse(payload.TransactionId, out var id))
            {
                transaction = await _context.PaymentTransactions
                    .FirstOrDefaultAsync(t => t.Provider == normalizedProvider && t.Id == id);
            }

            if (transaction == null)
            {
                _logger.LogWarning("Callback received for unknown transaction. Provider: {Provider}, ExternalId: {ExternalId}, Reference: {Reference}",
                    normalizedProvider, payload.ExternalTransactionId, payload.Reference);
                return false;
            }

            if (!VerifyCallbackSignature(normalizedProvider, payload))
            {
                _logger.LogWarning("Invalid callback signature for transaction {Id}", transaction.Id);
                return false;
            }

            if (!CallbackMatchesTransaction(transaction, payload))
            {
                _logger.LogWarning("Callback payload did not match transaction {Id}", transaction.Id);
                return false;
            }

            // Update transaction status
            var previousStatus = transaction.Status;
            transaction.Status = MapProviderStatus(payload.Status);
            transaction.StatusMessage = payload.StatusMessage;
            transaction.ProviderResponse = payload.RawData != null
                ? JsonSerializer.Serialize(payload.RawData)
                : JsonSerializer.Serialize(payload);
            transaction.UpdatedAt = DateTime.UtcNow;

            if (transaction.Status == PaymentStatus.Success)
            {
                transaction.CompletedAt = DateTime.UtcNow;

                // Trigger webhook for successful payment
                await _webhookService.TriggerEventAsync(WebhookEventTypes.PaymentReceived, new
                {
                    transactionId = transaction.Id,
                    externalId = transaction.ExternalTransactionId,
                    amount = transaction.Amount,
                    currency = transaction.Currency,
                    provider = transaction.Provider,
                    invoiceId = transaction.InvoiceId,
                    customerCode = transaction.CustomerCode
                });
            }
            else if (transaction.Status == PaymentStatus.Failed)
            {
                await _webhookService.TriggerEventAsync(WebhookEventTypes.PaymentFailed, new
                {
                    transactionId = transaction.Id,
                    externalId = transaction.ExternalTransactionId,
                    amount = transaction.Amount,
                    provider = transaction.Provider,
                    reason = transaction.StatusMessage
                });
            }

            await _context.SaveChangesAsync();

            _logger.LogInformation("Payment callback processed: {TransactionId} status changed from {Old} to {New}",
                transaction.Id, previousStatus, transaction.Status);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing payment callback for provider {Provider}", provider);
            return false;
        }
    }

    public async Task<PaymentTransactionListResponse> GetTransactionsAsync(
        string? provider = null, string? status = null, int page = 1, int pageSize = 50)
    {
        var query = _context.PaymentTransactions.AsQueryable();

        if (!string.IsNullOrEmpty(provider))
            query = query.Where(t => t.Provider == provider);

        if (!string.IsNullOrEmpty(status))
            query = query.Where(t => t.Status == status);

        var totalCount = await query.CountAsync();
        var totalAmount = await query.Where(t => t.Status == PaymentStatus.Success).SumAsync(t => t.Amount);

        var transactions = await query
            .OrderByDescending(t => t.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(t => new PaymentTransactionDto
            {
                Id = t.Id,
                ExternalTransactionId = t.ExternalTransactionId,
                Provider = t.Provider,
                PaymentMethod = t.PaymentMethod,
                Amount = t.Amount,
                Currency = t.Currency,
                PhoneNumber = t.PhoneNumber,
                Reference = t.Reference,
                InvoiceId = t.InvoiceId,
                CustomerCode = t.CustomerCode,
                Status = t.Status,
                StatusMessage = t.StatusMessage,
                CreatedAt = t.CreatedAt,
                CompletedAt = t.CompletedAt
            })
            .ToListAsync();

        return new PaymentTransactionListResponse
        {
            Transactions = transactions,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
            TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize),
            TotalAmount = totalAmount
        };
    }

    public Task<PaymentProvidersResponse> GetAvailableProvidersAsync()
    {
        var providers = new List<PaymentProviderInfo>
        {
            new()
            {
                Name = PaymentProviders.PayNow,
                DisplayName = "PayNow Zimbabwe",
                IsEnabled = _settings.PayNow.IsEnabled,
                IsSandbox = _settings.PayNow.IsSandbox,
                SupportedCurrencies = new List<string> { "USD", "ZWL" },
                PaymentMethods = new List<string> { "Mobile", "Web", "USSD" },
                LogoUrl = "/images/payment/paynow.png"
            },
            new()
            {
                Name = PaymentProviders.Innbucks,
                DisplayName = "Innbucks",
                IsEnabled = _settings.Innbucks.IsEnabled,
                IsSandbox = _settings.Innbucks.IsSandbox,
                SupportedCurrencies = new List<string> { "USD" },
                PaymentMethods = new List<string> { "Mobile", "QR" },
                LogoUrl = "/images/payment/innbucks.png"
            },
            new()
            {
                Name = PaymentProviders.Ecocash,
                DisplayName = "EcoCash",
                IsEnabled = _settings.Ecocash.IsEnabled,
                IsSandbox = _settings.Ecocash.IsSandbox,
                SupportedCurrencies = new List<string> { "USD", "ZWL" },
                PaymentMethods = new List<string> { "Mobile", "USSD" },
                LogoUrl = "/images/payment/ecocash.png"
            }
        };

        return Task.FromResult(new PaymentProvidersResponse { Providers = providers });
    }

    public async Task<bool> RefundPaymentAsync(int transactionId, decimal? amount = null)
    {
        var transaction = await _context.PaymentTransactions.FindAsync(transactionId);
        if (transaction == null || transaction.Status != PaymentStatus.Success)
        {
            return false;
        }

        // TODO: Implement provider-specific refund logic
        // For now, just mark as refunded
        transaction.Status = PaymentStatus.Refunded;
        transaction.StatusMessage = $"Refunded {amount ?? transaction.Amount} {transaction.Currency}";
        transaction.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        await _webhookService.TriggerEventAsync(WebhookEventTypes.PaymentRefunded, new
        {
            transactionId = transaction.Id,
            amount = amount ?? transaction.Amount,
            currency = transaction.Currency,
            provider = transaction.Provider
        });

        _logger.LogInformation("Payment {TransactionId} refunded: {Amount} {Currency}",
            transactionId, amount ?? transaction.Amount, transaction.Currency);

        return true;
    }

    public async Task<bool> CancelPaymentAsync(int transactionId)
    {
        var transaction = await _context.PaymentTransactions.FindAsync(transactionId);
        if (transaction == null || transaction.Status != PaymentStatus.Pending)
        {
            return false;
        }

        transaction.Status = PaymentStatus.Cancelled;
        transaction.StatusMessage = "Cancelled by user";
        transaction.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        _logger.LogInformation("Payment {TransactionId} cancelled", transactionId);

        return true;
    }

    #region Provider-specific implementations

    private async Task<InitiatePaymentResponse> InitiatePayNowPaymentAsync(PaymentTransaction transaction, InitiatePaymentRequest request)
    {
        if (!_settings.PayNow.IsEnabled)
        {
            throw new InvalidOperationException("PayNow is not enabled");
        }

        // PayNow integration
        // Reference: https://developers.paynow.co.zw/docs/integration_guide.html
        var client = _httpClientFactory.CreateClient();

        var baseUrl = _settings.PayNow.IsSandbox
            ? "https://www.paynow.co.zw/interface/initiatetransaction"
            : "https://www.paynow.co.zw/interface/initiatetransaction";

        var values = new Dictionary<string, string>
        {
            { "id", _settings.PayNow.IntegrationId },
            { "reference", transaction.Reference ?? transaction.Id.ToString() },
            { "amount", transaction.Amount.ToString("F2") },
            { "additionalinfo", $"Invoice payment - {transaction.Reference}" },
            { "returnurl", request.ReturnUrl ?? _settings.PayNow.ReturnUrl },
            { "resulturl", request.CallbackUrl ?? _settings.PayNow.ResultUrl },
            { "status", "Message" }
        };

        // Create hash
        var hashString = string.Join("", values.Values) + _settings.PayNow.IntegrationKey;
        values["hash"] = ComputeSha512Hash(hashString);

        if (!string.IsNullOrEmpty(request.PhoneNumber))
        {
            values["authemail"] = request.Email ?? "";
            values["phone"] = request.PhoneNumber;
        }

        var content = new FormUrlEncodedContent(values);
        var response = await client.PostAsync(baseUrl, content);
        var responseText = await response.Content.ReadAsStringAsync();

        // Parse PayNow response
        var responseParams = ParsePayNowResponse(responseText);

        if (responseParams.TryGetValue("status", out var status) && status.ToLower() == "ok")
        {
            transaction.ExternalTransactionId = responseParams.GetValueOrDefault("pollurl");
            transaction.Status = PaymentStatus.Processing;
            transaction.ProviderResponse = responseText;

            return new InitiatePaymentResponse
            {
                Status = PaymentStatus.Processing,
                Provider = PaymentProviders.PayNow,
                ExternalTransactionId = transaction.ExternalTransactionId,
                PaymentUrl = responseParams.GetValueOrDefault("browserurl"),
                Instructions = "Complete payment on the PayNow page or via mobile money",
                ExpiresAt = DateTime.UtcNow.AddMinutes(30)
            };
        }
        else
        {
            var error = responseParams.GetValueOrDefault("error", "Unknown error");
            throw new Exception($"PayNow error: {error}");
        }
    }

    private async Task<InitiatePaymentResponse> InitiateInnbucksPaymentAsync(PaymentTransaction transaction, InitiatePaymentRequest request)
    {
        if (!_settings.Innbucks.IsEnabled)
        {
            throw new InvalidOperationException("Innbucks is not enabled");
        }

        if (string.IsNullOrEmpty(request.PhoneNumber))
        {
            throw new ArgumentException("Phone number is required for Innbucks payments");
        }

        // Innbucks integration - placeholder implementation
        // Actual implementation would depend on Innbucks API documentation
        var client = _httpClientFactory.CreateClient();

        var payload = new
        {
            merchantId = _settings.Innbucks.MerchantId,
            amount = transaction.Amount,
            currency = transaction.Currency,
            phoneNumber = request.PhoneNumber,
            reference = transaction.Reference,
            callbackUrl = request.CallbackUrl ?? _settings.Innbucks.CallbackUrl
        };

        // TODO: Implement actual Innbucks API call
        // For now, return a simulated response
        transaction.ExternalTransactionId = $"INB-{Guid.NewGuid():N}".Substring(0, 20);
        transaction.Status = PaymentStatus.Processing;

        _logger.LogWarning("Innbucks payment initiated in simulation mode");

        return new InitiatePaymentResponse
        {
            Status = PaymentStatus.Processing,
            Provider = PaymentProviders.Innbucks,
            ExternalTransactionId = transaction.ExternalTransactionId,
            UssdCode = $"*199*{_settings.Innbucks.MerchantId}*{transaction.Amount}#",
            Instructions = $"Dial the USSD code or approve the payment request on your Innbucks app. Amount: ${transaction.Amount}",
            ExpiresAt = DateTime.UtcNow.AddMinutes(15)
        };
    }

    private async Task<InitiatePaymentResponse> InitiateEcocashPaymentAsync(PaymentTransaction transaction, InitiatePaymentRequest request)
    {
        if (!_settings.Ecocash.IsEnabled)
        {
            throw new InvalidOperationException("Ecocash is not enabled");
        }

        if (string.IsNullOrEmpty(request.PhoneNumber))
        {
            throw new ArgumentException("Phone number is required for Ecocash payments");
        }

        // Ecocash integration - placeholder implementation
        // Actual implementation would depend on Ecocash Merchant API documentation
        var client = _httpClientFactory.CreateClient();

        // TODO: Implement actual Ecocash API call
        transaction.ExternalTransactionId = $"ECO-{Guid.NewGuid():N}".Substring(0, 20);
        transaction.Status = PaymentStatus.Processing;

        _logger.LogWarning("Ecocash payment initiated in simulation mode");

        return new InitiatePaymentResponse
        {
            Status = PaymentStatus.Processing,
            Provider = PaymentProviders.Ecocash,
            ExternalTransactionId = transaction.ExternalTransactionId,
            UssdCode = $"*151*2*1*{_settings.Ecocash.MerchantCode}*{transaction.Amount}#",
            Instructions = $"A payment request has been sent to {request.PhoneNumber}. Please enter your Ecocash PIN to confirm. Amount: ${transaction.Amount}",
            ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        };
    }

    #endregion

    #region Helper methods

    private async Task RefreshStatusFromProviderAsync(PaymentTransaction transaction)
    {
        try
        {
            // Provider-specific status check
            switch (transaction.Provider)
            {
                case PaymentProviders.PayNow:
                    await RefreshPayNowStatusAsync(transaction);
                    break;
                    // Add other providers as needed
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh status for transaction {Id}", transaction.Id);
        }
    }

    private async Task RefreshPayNowStatusAsync(PaymentTransaction transaction)
    {
        if (string.IsNullOrEmpty(transaction.ExternalTransactionId)) return;

        var client = _httpClientFactory.CreateClient();
        var response = await client.GetStringAsync(transaction.ExternalTransactionId);
        var responseParams = ParsePayNowResponse(response);

        if (responseParams.TryGetValue("status", out var status))
        {
            transaction.Status = MapProviderStatus(status);
            transaction.StatusMessage = responseParams.GetValueOrDefault("error") ?? status;
            transaction.UpdatedAt = DateTime.UtcNow;

            if (transaction.Status == PaymentStatus.Success)
            {
                transaction.CompletedAt = DateTime.UtcNow;
            }

            await _context.SaveChangesAsync();
        }
    }

    private static Dictionary<string, string> ParsePayNowResponse(string response)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in response.Split('&'))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2)
            {
                result[Uri.UnescapeDataString(parts[0])] = Uri.UnescapeDataString(parts[1]);
            }
        }
        return result;
    }

    private static string MapProviderStatus(string? providerStatus)
    {
        return providerStatus?.ToLower() switch
        {
            "paid" or "success" or "completed" => PaymentStatus.Success,
            "failed" or "error" or "declined" => PaymentStatus.Failed,
            "cancelled" or "canceled" => PaymentStatus.Cancelled,
            "refunded" => PaymentStatus.Refunded,
            "pending" or "created" or "sent" => PaymentStatus.Pending,
            "awaiting delivery" or "processing" => PaymentStatus.Processing,
            _ => PaymentStatus.Pending
        };
    }

    private bool VerifyCallbackSignature(string provider, PaymentCallbackPayload payload)
    {
        if (string.IsNullOrWhiteSpace(payload.Signature))
        {
            _logger.LogWarning("Missing callback signature for provider {Provider}", provider);
            return false;
        }

        return provider switch
        {
            PaymentProviders.PayNow => VerifyPayNowCallbackHash(payload),
            PaymentProviders.Innbucks => VerifyHmacCallback(payload, _settings.Innbucks.ApiSecret),
            PaymentProviders.Ecocash => VerifyHmacCallback(payload, _settings.Ecocash.ApiSecret),
            _ => false
        };
    }

    private static string ComputeSha512Hash(string input)
    {
        using var sha512 = SHA512.Create();
        var bytes = sha512.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToUpperInvariant();
    }

    private static string ComputeHmacSha256(string input, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var bytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToUpperInvariant();
    }

    private static string NormalizeProvider(string provider)
    {
        return provider.Trim() switch
        {
            var value when value.Equals(PaymentProviders.PayNow, StringComparison.OrdinalIgnoreCase) => PaymentProviders.PayNow,
            var value when value.Equals(PaymentProviders.Innbucks, StringComparison.OrdinalIgnoreCase) => PaymentProviders.Innbucks,
            var value when value.Equals(PaymentProviders.Ecocash, StringComparison.OrdinalIgnoreCase) => PaymentProviders.Ecocash,
            _ => string.Empty
        };
    }

    private bool IsProviderEnabled(string provider)
    {
        return provider switch
        {
            PaymentProviders.PayNow => _settings.PayNow.IsEnabled,
            PaymentProviders.Innbucks => _settings.Innbucks.IsEnabled,
            PaymentProviders.Ecocash => _settings.Ecocash.IsEnabled,
            _ => false
        };
    }

    private bool VerifyPayNowCallbackHash(PaymentCallbackPayload payload)
    {
        if (payload.RawData == null || payload.RawData.Count == 0)
        {
            _logger.LogWarning("PayNow callback rejected because raw callback fields were not captured");
            return false;
        }

        if (string.IsNullOrWhiteSpace(_settings.PayNow.IntegrationKey))
        {
            _logger.LogWarning("PayNow callback rejected because IntegrationKey is not configured");
            return false;
        }

        var builder = new StringBuilder();
        foreach (var pair in payload.RawData)
        {
            if (pair.Key.Equals("hash", StringComparison.OrdinalIgnoreCase) || pair.Value == null)
            {
                continue;
            }

            builder.Append(pair.Value.ToString());
        }

        var expectedHash = ComputeSha512Hash(builder + _settings.PayNow.IntegrationKey);
        return FixedTimeEquals(expectedHash, payload.Signature);
    }

    private bool VerifyHmacCallback(PaymentCallbackPayload payload, string secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            _logger.LogWarning("Callback rejected because the provider secret is not configured");
            return false;
        }

        var amount = payload.Amount.HasValue
            ? payload.Amount.Value.ToString("0.00", CultureInfo.InvariantCulture)
            : string.Empty;

        var canonicalPayload = string.Join("|",
            payload.TransactionId ?? string.Empty,
            payload.ExternalTransactionId ?? string.Empty,
            payload.Reference ?? string.Empty,
            amount,
            payload.Currency ?? string.Empty,
            payload.Status ?? string.Empty,
            payload.StatusMessage ?? string.Empty);

        var expectedHash = ComputeHmacSha256(canonicalPayload, secret);
        return FixedTimeEquals(expectedHash, payload.Signature);
    }

    private static bool FixedTimeEquals(string expected, string? provided)
    {
        if (string.IsNullOrWhiteSpace(provided))
        {
            return false;
        }

        var normalizedExpected = NormalizeSignature(expected);
        var normalizedProvided = NormalizeSignature(provided);

        if (normalizedExpected.Length != normalizedProvided.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(normalizedExpected),
            Encoding.UTF8.GetBytes(normalizedProvided));
    }

    private static string NormalizeSignature(string signature)
    {
        var normalized = signature.Trim();
        if (normalized.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[7..];
        }

        return normalized.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
    }

    private static bool CallbackMatchesTransaction(PaymentTransaction transaction, PaymentCallbackPayload payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.Reference) && !string.Equals(transaction.Reference, payload.Reference, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(payload.ExternalTransactionId) &&
            !string.Equals(transaction.ExternalTransactionId, payload.ExternalTransactionId, StringComparison.Ordinal))
        {
            return false;
        }

        if (payload.Amount.HasValue && payload.Amount.Value != transaction.Amount)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(payload.Currency) &&
            !string.Equals(transaction.Currency, payload.Currency, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static PaymentStatusResponse MapToStatusResponse(PaymentTransaction transaction)
    {
        return new PaymentStatusResponse
        {
            TransactionId = transaction.Id,
            ExternalTransactionId = transaction.ExternalTransactionId,
            Status = transaction.Status,
            StatusMessage = transaction.StatusMessage,
            Amount = transaction.Amount,
            Currency = transaction.Currency,
            Provider = transaction.Provider,
            CreatedAt = transaction.CreatedAt,
            CompletedAt = transaction.CompletedAt
        };
    }

    #endregion
}
