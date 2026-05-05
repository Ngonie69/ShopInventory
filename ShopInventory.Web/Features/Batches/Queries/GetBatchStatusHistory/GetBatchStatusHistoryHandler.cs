using System.Text.Json;
using System.Text.RegularExpressions;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Data;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.Batches.Queries.GetBatchStatusHistory;

public sealed class GetBatchStatusHistoryHandler(
    IDbContextFactory<WebAppDbContext> dbContextFactory,
    ILogger<GetBatchStatusHistoryHandler> logger
) : IRequestHandler<GetBatchStatusHistoryQuery, ErrorOr<BatchStatusHistoryResponse>>
{
    private static readonly Regex LegacyDetailPattern = new(
        @"(?:Updated|Failed to update) batch (?<batch>.+?) \((?<item>.+?)\) to (?<status>.+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public async Task<ErrorOr<BatchStatusHistoryResponse>> Handle(
        GetBatchStatusHistoryQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            var trimmedSearchTerm = request.SearchTerm?.Trim();
            var searchPattern = string.IsNullOrWhiteSpace(trimmedSearchTerm)
                ? null
                : $"%{trimmedSearchTerm}%";

            var query = dbContext.AuditLogs
                .AsNoTracking()
                .Where(log => log.Action == "BatchStatusUpdated" && log.EntityType == "Batch");

            if (!string.IsNullOrWhiteSpace(searchPattern))
            {
                query = query.Where(log =>
                    EF.Functions.ILike(log.Username, searchPattern) ||
                    EF.Functions.ILike(log.UserRole, searchPattern) ||
                    (log.EntityId != null && EF.Functions.ILike(log.EntityId, searchPattern)) ||
                    (log.Details != null && EF.Functions.ILike(log.Details, searchPattern)) ||
                    (log.ErrorMessage != null && EF.Functions.ILike(log.ErrorMessage, searchPattern)));
            }

            var skipCount = (request.Page - 1) * request.PageSize;
            var rows = await query
                .OrderByDescending(log => log.Timestamp)
                .ThenByDescending(log => log.Id)
                .Select(log => new BatchStatusHistoryAuditRow
                {
                    AuditLogId = log.Id,
                    Timestamp = log.Timestamp,
                    Username = log.Username,
                    UserRole = log.UserRole,
                    EntityId = log.EntityId,
                    Details = log.Details,
                    IsSuccess = log.IsSuccess,
                    ErrorMessage = log.ErrorMessage
                })
                .Skip(skipCount)
                .Take(request.PageSize + 1)
                .ToListAsync(cancellationToken);

            var hasMore = rows.Count > request.PageSize;
            var items = rows
                .Take(request.PageSize)
                .Select(MapToHistoryItem)
                .ToList();

            return new BatchStatusHistoryResponse
            {
                SearchTerm = trimmedSearchTerm ?? string.Empty,
                Page = request.Page,
                PageSize = request.PageSize,
                HasMore = hasMore,
                Items = items
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load batch status history");
            return Errors.Batch.SearchFailed("Failed to load batch status history.");
        }
    }

    private static BatchStatusHistoryItem MapToHistoryItem(BatchStatusHistoryAuditRow row)
    {
        var item = new BatchStatusHistoryItem
        {
            AuditLogId = row.AuditLogId,
            Username = string.IsNullOrWhiteSpace(row.Username) ? "System" : row.Username,
            UserRole = string.IsNullOrWhiteSpace(row.UserRole) ? "System" : row.UserRole,
            Timestamp = row.Timestamp,
            IsSuccess = row.IsSuccess,
            ErrorMessage = row.ErrorMessage
        };

        if (int.TryParse(row.EntityId, out var batchEntryId))
        {
            item.BatchEntryId = batchEntryId;
        }

        if (!string.IsNullOrWhiteSpace(row.Details))
        {
            if (!TryParseStructuredDetails(row.Details, item))
            {
                TryParseLegacyDetails(row.Details, item);
            }
        }

        if (string.IsNullOrWhiteSpace(item.BatchNumber))
        {
            item.BatchNumber = item.BatchEntryId > 0
                ? $"Entry {item.BatchEntryId}"
                : "Unknown";
        }

        if (string.IsNullOrWhiteSpace(item.ItemCode))
        {
            item.ItemCode = "Unknown";
        }

        return item;
    }

    private static bool TryParseStructuredDetails(string details, BatchStatusHistoryItem item)
    {
        try
        {
            using var document = JsonDocument.Parse(details);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            item.BatchNumber = GetStringValue(document.RootElement, "BatchNumber") ?? item.BatchNumber;
            item.ItemCode = GetStringValue(document.RootElement, "ItemCode") ?? item.ItemCode;
            item.WarehouseCode = GetStringValue(document.RootElement, "WarehouseCode") ?? item.WarehouseCode;
            item.FromStatus = GetStringValue(document.RootElement, "FromStatus") ?? item.FromStatus;
            item.ToStatus = GetStringValue(document.RootElement, "ToStatus") ?? item.ToStatus;

            return !string.IsNullOrWhiteSpace(item.BatchNumber) ||
                   !string.IsNullOrWhiteSpace(item.ItemCode) ||
                   !string.IsNullOrWhiteSpace(item.WarehouseCode) ||
                   !string.IsNullOrWhiteSpace(item.ToStatus);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void TryParseLegacyDetails(string details, BatchStatusHistoryItem item)
    {
        var match = LegacyDetailPattern.Match(details);
        if (!match.Success)
        {
            return;
        }

        item.BatchNumber = match.Groups["batch"].Value.Trim();
        item.ItemCode = match.Groups["item"].Value.Trim();
        item.ToStatus = match.Groups["status"].Value.Trim();
    }

    private static string? GetStringValue(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return property.GetString();
    }
}