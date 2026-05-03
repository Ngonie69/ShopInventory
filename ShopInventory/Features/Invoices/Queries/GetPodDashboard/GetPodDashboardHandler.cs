using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.Invoices.Queries.GetPodDashboard;

public sealed class GetPodDashboardHandler(
    ApplicationDbContext context,
    IDocumentService documentService,
    ILogger<GetPodDashboardHandler> logger
) : IRequestHandler<GetPodDashboardQuery, ErrorOr<PodDashboardDto>>
{
    public async Task<ErrorOr<PodDashboardDto>> Handle(
        GetPodDashboardQuery request,
        CancellationToken cancellationToken)
    {
        var user = await context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == request.UserId, cancellationToken);

        var now = DateTime.UtcNow;
        var todayStart = DateTime.SpecifyKind(now.Date, DateTimeKind.Utc);
        var weekOffset = now.DayOfWeek == DayOfWeek.Sunday
            ? -6
            : (int)DayOfWeek.Monday - (int)now.DayOfWeek;
        var weekStart = DateTime.SpecifyKind(now.Date.AddDays(weekOffset), DateTimeKind.Utc);
        var monthStart = DateTime.SpecifyKind(new DateTime(now.Year, now.Month, 1), DateTimeKind.Utc);

        var baseQuery = context.Set<DocumentAttachmentEntity>()
            .AsNoTracking()
            .Where(a => a.EntityType == "Invoice")
            .Where(a =>
                EF.Functions.ILike(a.FileName, "%pod%") ||
                EF.Functions.ILike(a.FileName, "%proof of delivery%") ||
                (a.Description != null && (
                    EF.Functions.ILike(a.Description, "%pod%") ||
                    EF.Functions.ILike(a.Description, "%proof of delivery%"))));

        if (string.Equals(user?.Role, "PodOperator", StringComparison.OrdinalIgnoreCase))
        {
            var scopedDocEntries = await documentService.GetScopedPodInvoiceDocEntriesAsync(
                await baseQuery.Select(a => a.EntityId).Distinct().ToListAsync(cancellationToken),
                user?.AssignedSection ?? string.Empty,
                cancellationToken);

            baseQuery = scopedDocEntries.Count == 0
                ? baseQuery.Where(_ => false)
                : baseQuery.Where(a => scopedDocEntries.Contains(a.EntityId));
        }
        else
        {
            baseQuery = baseQuery.Where(a => a.UploadedByUserId == request.UserId);
        }

        var stats = await baseQuery
            .GroupBy(a => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Today = g.Count(a => a.UploadedAt >= todayStart),
                ThisWeek = g.Count(a => a.UploadedAt >= weekStart),
                ThisMonth = g.Count(a => a.UploadedAt >= monthStart),
                TotalFileSize = g.Sum(a => a.FileSizeBytes),
                UniqueInvoices = g.Select(a => a.EntityId).Distinct().Count()
            })
            .FirstOrDefaultAsync(cancellationToken);

        var recentAttachments = await baseQuery
            .OrderByDescending(a => a.UploadedAt)
            .Take(10)
            .Select(a => new { a.Id, a.FileName, a.EntityId, a.FileSizeBytes, a.UploadedAt })
            .ToListAsync(cancellationToken);

        var invoiceLookup = await GetInvoiceLookupByDocEntryAsync(
            recentAttachments.Select(a => a.EntityId).Distinct().ToList(),
            request.UserId,
            cancellationToken);

        var thirtyDaysAgo = DateTime.SpecifyKind(now.Date.AddDays(-29), DateTimeKind.Utc);
        var dailyCounts = await baseQuery
            .Where(a => a.UploadedAt >= thirtyDaysAgo)
            .GroupBy(a => a.UploadedAt.Date)
            .Select(g => new { Date = g.Key, Count = g.Count() })
            .OrderBy(g => g.Date)
            .ToListAsync(cancellationToken);

        var dailyUploads = new List<PodDailyCountDto>();
        for (var date = thirtyDaysAgo.Date; date <= now.Date; date = date.AddDays(1))
        {
            var count = dailyCounts.FirstOrDefault(c => c.Date == date)?.Count ?? 0;
            dailyUploads.Add(new PodDailyCountDto { Date = date.ToString("MMM dd"), Count = count });
        }

        return new PodDashboardDto
        {
            Username = user?.Username ?? "Unknown",
            UploadsToday = stats?.Today ?? 0,
            UploadsThisWeek = stats?.ThisWeek ?? 0,
            UploadsThisMonth = stats?.ThisMonth ?? 0,
            TotalUploads = stats?.Total ?? 0,
            TotalFileSizeBytes = stats?.TotalFileSize ?? 0,
            TotalFileSizeFormatted = FormatFileSize(stats?.TotalFileSize ?? 0),
            UniqueInvoicesCovered = stats?.UniqueInvoices ?? 0,
            RecentUploads = recentAttachments.Select(a =>
            {
                invoiceLookup.TryGetValue(a.EntityId, out var invoiceInfo);
                return new PodRecentUploadDto
                {
                    Id = a.Id,
                    FileName = a.FileName,
                    InvoiceDocEntry = a.EntityId,
                    InvoiceDocNum = invoiceInfo.DocNum,
                    CardName = invoiceInfo.CardName,
                    FileSizeFormatted = FormatFileSize(a.FileSizeBytes),
                    UploadedAt = a.UploadedAt
                };
            }).ToList(),
            DailyUploads = dailyUploads
        };
    }

    private async Task<Dictionary<int, (int DocNum, string? CardName)>> GetInvoiceLookupByDocEntryAsync(
        List<int> entityIds,
        Guid userId,
        CancellationToken cancellationToken)
    {
        if (entityIds.Count == 0)
            return new Dictionary<int, (int DocNum, string? CardName)>();

        var invoices = await context.Invoices
            .AsNoTracking()
            .Where(i => i.SAPDocEntry != null && entityIds.Contains(i.SAPDocEntry.Value))
            .Select(i => new
            {
                DocEntry = i.SAPDocEntry!.Value,
                DocNum = i.SAPDocNum ?? 0,
                i.CardName,
                i.CreatedAt,
                i.UpdatedAt,
                i.Id
            })
            .ToListAsync(cancellationToken);

        var duplicateDocEntries = invoices
            .GroupBy(i => i.DocEntry)
            .Count(g => g.Count() > 1);

        if (duplicateDocEntries > 0)
        {
            logger.LogWarning(
                "Detected {DuplicateCount} duplicate cached invoice doc entries while loading POD dashboard for user {UserId}",
                duplicateDocEntries,
                userId);
        }

        return invoices
            .GroupBy(i => i.DocEntry)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var selected = g
                        .OrderByDescending(i => i.DocNum > 0)
                        .ThenByDescending(i => !string.IsNullOrWhiteSpace(i.CardName))
                        .ThenByDescending(i => i.UpdatedAt ?? i.CreatedAt)
                        .ThenByDescending(i => i.Id)
                        .First();

                    return (selected.DocNum, selected.CardName);
                });
    }

    private static string FormatFileSize(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB"];
        double length = bytes;
        var order = 0;

        while (length >= 1024 && order < sizes.Length - 1)
        {
            order++;
            length /= 1024;
        }

        return $"{length:0.##} {sizes[order]}";
    }
}
