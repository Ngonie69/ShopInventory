using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetFiscalTransactions;

public sealed class GetFiscalTransactionsHandler(
    ApplicationDbContext dbContext,
    ILogger<GetFiscalTransactionsHandler> logger) : IRequestHandler<GetFiscalTransactionsQuery, ErrorOr<GetFiscalTransactionsResult>>
{
    public async Task<ErrorOr<GetFiscalTransactionsResult>> Handle(
        GetFiscalTransactionsQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var page = Math.Max(1, request.Page);
            var pageSize = Math.Clamp(request.PageSize, 1, 200);

            var query = ApplyFilters(dbContext.DesktopFiscalTransactions.AsNoTracking(), request);
            var totalCount = await query.CountAsync(cancellationToken);

            var statusCounts = (await query
                    .GroupBy(transaction => transaction.Status)
                    .Select(group => new
                    {
                        Status = group.Key,
                        Count = group.Count()
                    })
                    .ToListAsync(cancellationToken))
                .ToDictionary(item => item.Status, item => item.Count, StringComparer.OrdinalIgnoreCase);

            var uniqueOperators = await query
                .Where(transaction => transaction.CreatedByUsername != null && transaction.CreatedByUsername != string.Empty)
                .Select(transaction => transaction.CreatedByUsername!)
                .Distinct()
                .CountAsync(cancellationToken);

            var latestTransactionAtUtc = await query
                .MaxAsync(transaction => (DateTime?)transaction.TimestampUtc, cancellationToken);

            var transactions = await query
                .OrderByDescending(transaction => transaction.TimestampUtc)
                .ThenByDescending(transaction => transaction.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(transaction => new FiscalTransactionLogItemDto
                {
                    Id = transaction.Id,
                    ClientTransactionId = transaction.ClientTransactionId,
                    TimestampUtc = transaction.TimestampUtc,
                    DocumentType = transaction.DocumentType,
                    DocNum = transaction.DocNum,
                    Status = transaction.Status,
                    Message = transaction.Message,
                    VerificationCode = transaction.VerificationCode,
                    QRCode = transaction.QRCode,
                    DeviceSerialNumber = transaction.DeviceSerialNumber,
                    DeviceId = transaction.DeviceId,
                    FiscalDay = transaction.FiscalDay,
                    ReceiptGlobalNo = transaction.ReceiptGlobalNo,
                    CardCode = transaction.CardCode,
                    CardName = transaction.CardName,
                    DocTotal = transaction.DocTotal,
                    VatSum = transaction.VatSum,
                    Currency = transaction.Currency,
                    OriginalInvoiceNumber = transaction.OriginalInvoiceNumber,
                    SourceSystem = transaction.SourceSystem,
                    CreatedByUserId = transaction.CreatedByUserId,
                    CreatedByUsername = transaction.CreatedByUsername,
                    CreatedAtUtc = transaction.CreatedAtUtc,
                    LastSyncedAtUtc = transaction.LastSyncedAtUtc
                })
                .ToListAsync(cancellationToken);

            return new GetFiscalTransactionsResult
            {
                Summary = new FiscalTransactionLogSummary
                {
                    TotalTransactions = totalCount,
                    SuccessCount = GetStatusCount(statusCounts, "Success"),
                    FiscalisedCount = GetStatusCount(statusCounts, "Fiscalised"),
                    NotFiscalisedCount = GetStatusCount(statusCounts, "Not Fiscalised"),
                    FailedCount = GetStatusCount(statusCounts, "Failed"),
                    UniqueOperators = uniqueOperators,
                    LatestTransactionAtUtc = latestTransactionAtUtc
                },
                Transactions = transactions,
                TotalCount = totalCount,
                Page = page,
                PageSize = pageSize,
                HasMore = (page * pageSize) < totalCount
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load desktop fiscal transaction log");
            return Errors.DesktopIntegration.LoadFiscalTransactionsFailed("Failed to load fiscal transaction log.");
        }
    }

    private static IQueryable<DesktopFiscalTransactionEntity> ApplyFilters(
        IQueryable<DesktopFiscalTransactionEntity> query,
        GetFiscalTransactionsQuery request)
    {
        if (!string.IsNullOrWhiteSpace(request.DocumentType))
        {
            var documentType = request.DocumentType.Trim();
            query = query.Where(transaction => EF.Functions.ILike(transaction.DocumentType, documentType));
        }

        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            var status = request.Status.Trim();
            query = query.Where(transaction => EF.Functions.ILike(transaction.Status, status));
        }

        var fromUtc = NormalizeUtcDate(request.FromUtc);
        if (fromUtc.HasValue)
        {
            query = query.Where(transaction => transaction.TimestampUtc >= fromUtc.Value);
        }

        var toExclusiveUtc = NormalizeUtcDate(request.ToUtc)?.AddDays(1);
        if (toExclusiveUtc.HasValue)
        {
            query = query.Where(transaction => transaction.TimestampUtc < toExclusiveUtc.Value);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            var pattern = $"%{term}%";
            var hasDocNum = int.TryParse(term, out var docNum);

            query = query.Where(transaction =>
                (hasDocNum && transaction.DocNum == docNum)
                || EF.Functions.ILike(transaction.DocumentType, pattern)
                || EF.Functions.ILike(transaction.Status, pattern)
                || EF.Functions.ILike(transaction.SourceSystem, pattern)
                || (transaction.CardCode != null && EF.Functions.ILike(transaction.CardCode, pattern))
                || (transaction.CardName != null && EF.Functions.ILike(transaction.CardName, pattern))
                || (transaction.VerificationCode != null && EF.Functions.ILike(transaction.VerificationCode, pattern))
                || (transaction.OriginalInvoiceNumber != null && EF.Functions.ILike(transaction.OriginalInvoiceNumber, pattern))
                || (transaction.CreatedByUsername != null && EF.Functions.ILike(transaction.CreatedByUsername, pattern))
                || (transaction.Message != null && EF.Functions.ILike(transaction.Message, pattern)));
        }

        return query;
    }

    private static DateTime? NormalizeUtcDate(DateTime? value)
    {
        if (!value.HasValue)
            return null;

        return DateTime.SpecifyKind(value.Value.Date, DateTimeKind.Utc);
    }

    private static int GetStatusCount(IReadOnlyDictionary<string, int> statusCounts, string status)
        => statusCounts.TryGetValue(status, out var count) ? count : 0;
}