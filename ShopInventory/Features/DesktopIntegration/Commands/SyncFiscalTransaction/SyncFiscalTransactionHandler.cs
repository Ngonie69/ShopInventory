using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Queries.GetFiscalTransactions;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.DesktopIntegration.Commands.SyncFiscalTransaction;

public sealed class SyncFiscalTransactionHandler(
    ApplicationDbContext dbContext,
    ILogger<SyncFiscalTransactionHandler> logger) : IRequestHandler<SyncFiscalTransactionCommand, ErrorOr<FiscalTransactionLogItemDto>>
{
    public async Task<ErrorOr<FiscalTransactionLogItemDto>> Handle(
        SyncFiscalTransactionCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var nowUtc = DateTime.UtcNow;
            var request = command.Request;
            var timestampUtc = NormalizeUtc(request.TimestampUtc, nowUtc);
            var clientTransactionId = ResolveClientTransactionId(request);

            var entity = await dbContext.DesktopFiscalTransactions
                .SingleOrDefaultAsync(transaction => transaction.ClientTransactionId == clientTransactionId, cancellationToken);

            if (entity is not null && !RepresentsSameTransaction(entity, request))
            {
                logger.LogWarning(
                    "Desktop fiscal transaction client id {ClientTransactionId} was reused for a different transaction. DocNum {DocNum}, DocumentType {DocumentType}",
                    clientTransactionId,
                    request.DocNum,
                    request.DocumentType);

                clientTransactionId = BuildSyntheticClientTransactionId(request, clientTransactionId);
                entity = await dbContext.DesktopFiscalTransactions
                    .SingleOrDefaultAsync(transaction => transaction.ClientTransactionId == clientTransactionId, cancellationToken);
            }

            if (entity is null)
            {
                entity = new DesktopFiscalTransactionEntity
                {
                    ClientTransactionId = clientTransactionId,
                    CreatedAtUtc = nowUtc
                };

                dbContext.DesktopFiscalTransactions.Add(entity);
            }

            entity.TimestampUtc = timestampUtc;
            entity.DocNum = request.DocNum;
            entity.DocumentType = request.DocumentType.Trim();
            entity.Status = request.Status.Trim();
            entity.Message = NullIfWhiteSpace(request.Message);
            entity.VerificationCode = NullIfWhiteSpace(request.VerificationCode);
            entity.QRCode = NullIfWhiteSpace(request.QRCode);
            entity.DeviceSerialNumber = NullIfWhiteSpace(request.DeviceSerialNumber);
            entity.DeviceId = NullIfWhiteSpace(request.DeviceId);
            entity.FiscalDay = NullIfWhiteSpace(request.FiscalDay);
            entity.ReceiptGlobalNo = request.ReceiptGlobalNo.GetValueOrDefault() > 0 ? request.ReceiptGlobalNo : null;
            entity.CardCode = NullIfWhiteSpace(request.CardCode);
            entity.CardName = NullIfWhiteSpace(request.CardName);
            entity.DocTotal = request.DocTotal;
            entity.VatSum = request.VatSum;
            entity.Currency = NullIfWhiteSpace(request.Currency);
            entity.OriginalInvoiceNumber = NullIfWhiteSpace(request.OriginalInvoiceNumber);
            entity.RawRequest = NullIfWhiteSpace(request.RawRequest);
            entity.RawResponse = NullIfWhiteSpace(request.RawResponse);
            entity.SourceSystem = string.IsNullOrWhiteSpace(request.SourceSystem)
                ? entity.SourceSystem
                : request.SourceSystem.Trim();
            entity.CreatedByUserId = string.IsNullOrWhiteSpace(command.UserId)
                ? entity.CreatedByUserId
                : command.UserId.Trim();
            entity.CreatedByUsername = string.IsNullOrWhiteSpace(command.Username)
                ? entity.CreatedByUsername
                : command.Username.Trim();
            entity.LastSyncedAtUtc = nowUtc;

            await dbContext.SaveChangesAsync(cancellationToken);

            return Map(entity);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to sync desktop fiscal transaction {ClientTransactionId}", command.Request.ClientTransactionId);
            return Errors.DesktopIntegration.FiscalTransactionSyncFailed("Failed to sync fiscal transaction.");
        }
    }

    private static FiscalTransactionLogItemDto Map(DesktopFiscalTransactionEntity entity)
        => new()
        {
            Id = entity.Id,
            ClientTransactionId = entity.ClientTransactionId,
            TimestampUtc = entity.TimestampUtc,
            DocumentType = entity.DocumentType,
            DocNum = entity.DocNum,
            Status = entity.Status,
            Message = entity.Message,
            VerificationCode = entity.VerificationCode,
            QRCode = entity.QRCode,
            DeviceSerialNumber = entity.DeviceSerialNumber,
            DeviceId = entity.DeviceId,
            FiscalDay = entity.FiscalDay,
            ReceiptGlobalNo = entity.ReceiptGlobalNo,
            CardCode = entity.CardCode,
            CardName = entity.CardName,
            DocTotal = entity.DocTotal,
            VatSum = entity.VatSum,
            Currency = entity.Currency,
            OriginalInvoiceNumber = entity.OriginalInvoiceNumber,
            RawRequest = entity.RawRequest,
            RawResponse = entity.RawResponse,
            SourceSystem = entity.SourceSystem,
            CreatedByUserId = entity.CreatedByUserId,
            CreatedByUsername = entity.CreatedByUsername,
            CreatedAtUtc = entity.CreatedAtUtc,
            LastSyncedAtUtc = entity.LastSyncedAtUtc
        };

    private static DateTime NormalizeUtc(DateTime? value, DateTime fallbackUtc)
    {
        if (!value.HasValue)
        {
            return fallbackUtc;
        }

        return value.Value.Kind switch
        {
            DateTimeKind.Utc => value.Value,
            DateTimeKind.Local => value.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Local).ToUniversalTime()
        };
    }

    private static string ResolveClientTransactionId(SyncFiscalTransactionRequest request)
    {
        var clientTransactionId = NullIfWhiteSpace(request.ClientTransactionId);
        return clientTransactionId ?? BuildSyntheticClientTransactionId(request, "auto");
    }

    private static bool RepresentsSameTransaction(DesktopFiscalTransactionEntity entity, SyncFiscalTransactionRequest request)
        => entity.DocNum == request.DocNum
           && string.Equals(entity.DocumentType, request.DocumentType.Trim(), StringComparison.OrdinalIgnoreCase)
           && string.Equals(entity.OriginalInvoiceNumber, NullIfWhiteSpace(request.OriginalInvoiceNumber), StringComparison.OrdinalIgnoreCase)
           && string.Equals(entity.SourceSystem, NullIfWhiteSpace(request.SourceSystem) ?? entity.SourceSystem, StringComparison.OrdinalIgnoreCase);

    private static string BuildSyntheticClientTransactionId(SyncFiscalTransactionRequest request, string prefixSeed)
    {
        var sourceSystem = NullIfWhiteSpace(request.SourceSystem) ?? "InvoiceFiscalisation";
        var originalInvoiceNumber = NullIfWhiteSpace(request.OriginalInvoiceNumber) ?? string.Empty;
        var receiptGlobalNo = request.ReceiptGlobalNo?.ToString() ?? string.Empty;
        var seed = string.Join(
            "|",
            prefixSeed,
            sourceSystem,
            request.DocumentType.Trim(),
            request.DocNum.ToString(),
            originalInvoiceNumber,
            receiptGlobalNo,
            request.TimestampUtc?.ToUniversalTime().ToString("O") ?? string.Empty);

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed)));
        var prefix = SanitizePrefix(prefixSeed);
        return $"{prefix}-{hash[..24]}";
    }

    private static string SanitizePrefix(string value)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? "fiscal"
            : new string(value.Trim().Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "fiscal";
        }

        return normalized.Length <= 24 ? normalized : normalized[..24];
    }

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}