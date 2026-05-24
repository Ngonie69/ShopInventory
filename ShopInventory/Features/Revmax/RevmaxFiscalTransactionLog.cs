using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Models.Revmax;

namespace ShopInventory.Features.Revmax;

internal static class RevmaxFiscalTransactionLog
{
    private const string SourceSystem = "RevmaxEndpoint";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    internal static async Task TryRecordAsync(
        ApplicationDbContext dbContext,
        IHttpContextAccessor httpContextAccessor,
        ILogger logger,
        string endpointName,
        TransactMRequest? request,
        string status,
        string? message,
        TransactMResponse? response = null,
        object? rawResponse = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var nowUtc = DateTime.UtcNow;
            var user = ResolveUser(httpContextAccessor.HttpContext, request?.Cashier);

            dbContext.DesktopFiscalTransactions.Add(new DesktopFiscalTransactionEntity
            {
                ClientTransactionId = BuildClientTransactionId(endpointName, request?.InvoiceNumber, nowUtc),
                TimestampUtc = nowUtc,
                DocumentType = ResolveDocumentType(request),
                DocNum = ResolveDocNum(request?.InvoiceNumber),
                Status = Truncate(status, 40) ?? "Failed",
                Message = Truncate(BuildMessage(endpointName, message), 2000),
                VerificationCode = Truncate(response?.VerificationCode, 120),
                QRCode = Truncate(response?.QRcode, 2000),
                DeviceSerialNumber = Truncate(response?.DeviceSerialNumber ?? response?.DeviceSerial, 120),
                DeviceId = Truncate(response?.DeviceID, 120),
                FiscalDay = Truncate(response?.FiscalDayNo ?? response?.FiscalDay, 40),
                ReceiptGlobalNo = ResolveReceiptGlobalNo(response?.ReceiptGlobalNo),
                CardName = Truncate(request?.CustomerName, 255),
                DocTotal = request?.InvoiceAmount ?? 0m,
                VatSum = request?.InvoiceTaxAmount ?? 0m,
                Currency = Truncate(request?.Currency, 10),
                OriginalInvoiceNumber = Truncate(request?.OriginalInvoiceNumber, 50),
                RawRequest = SerializeOrNull(request),
                RawResponse = SerializeOrNull(rawResponse ?? response),
                SourceSystem = SourceSystem,
                CreatedByUserId = Truncate(user.UserId, 100),
                CreatedByUsername = Truncate(user.Username, 100),
                CreatedAtUtc = nowUtc,
                LastSyncedAtUtc = nowUtc
            });

            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to record REVMax fiscal transaction log for endpoint {EndpointName}, invoice {InvoiceNumber}",
                endpointName,
                request?.InvoiceNumber);
        }
    }

    private static string ResolveDocumentType(TransactMRequest? request)
    {
        if (request is null)
        {
            return "Unknown";
        }

        return string.Equals(request.Istatus, "02", StringComparison.OrdinalIgnoreCase) ||
               !string.IsNullOrWhiteSpace(request.OriginalInvoiceNumber)
            ? "CreditNote"
            : "Invoice";
    }

    private static int ResolveDocNum(string? invoiceNumber)
        => int.TryParse(invoiceNumber, NumberStyles.Integer, CultureInfo.InvariantCulture, out var docNum) && docNum > 0
            ? docNum
            : 0;

    private static int? ResolveReceiptGlobalNo(string? receiptGlobalNo)
        => int.TryParse(receiptGlobalNo, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : null;

    private static string BuildClientTransactionId(string endpointName, string? invoiceNumber, DateTime nowUtc)
    {
        var endpointSegment = SanitizeSegment(endpointName) ?? "endpoint";
        var invoiceSegment = SanitizeSegment(invoiceNumber) ?? "unknown";
        var timestampSegment = nowUtc.ToString("yyyyMMddHHmmssfffffff", CultureInfo.InvariantCulture);
        var suffix = Guid.NewGuid().ToString("N")[..8];

        return Truncate($"revmax-{endpointSegment}-{invoiceSegment}-{timestampSegment}-{suffix}", 100)!;
    }

    private static string? SanitizeSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var sanitized = new string(value.Trim().Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return null;
        }

        return sanitized.Length <= 30 ? sanitized : sanitized[..30];
    }

    private static string? SerializeOrNull(object? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Serialize(value, JsonOptions);
        }
        catch
        {
            return value.ToString();
        }
    }

    private static string? BuildMessage(string endpointName, string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return endpointName;
        }

        return $"{endpointName}: {message.Trim()}";
    }

    private static (string? UserId, string? Username) ResolveUser(HttpContext? httpContext, string? fallbackUsername)
    {
        var identity = httpContext?.User.Identities
            .Where(identity => identity.IsAuthenticated)
            .OfType<ClaimsIdentity>()
            .FirstOrDefault(identity => !string.Equals(
                identity.FindFirst(ClaimTypes.AuthenticationMethod)?.Value,
                "ApiKey",
                StringComparison.OrdinalIgnoreCase))
            ?? httpContext?.User.Identities
                .Where(identity => identity.IsAuthenticated)
                .OfType<ClaimsIdentity>()
                .FirstOrDefault();

        var username = identity?.Name ?? identity?.FindFirst(ClaimTypes.Name)?.Value;
        var userId = identity?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        return (userId, string.IsNullOrWhiteSpace(username) ? NullIfWhiteSpace(fallbackUsername) : username);
    }

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Truncate(string? value, int maxLength)
        => string.IsNullOrEmpty(value) || value.Length <= maxLength ? value : value[..maxLength];
}