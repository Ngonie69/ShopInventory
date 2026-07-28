using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Common.Caching;

public sealed record PodReportCacheSnapshot(
    PodUploadStatusReportDto Report,
    DateTime RefreshedAtUtc,
    bool IsFresh);

public interface IPodReportCacheStore
{
    bool Enabled { get; }

    Task<PodReportCacheSnapshot?> GetAsync(
        DateTime fromDate,
        DateTime toDate,
        string scopeKey,
        CancellationToken cancellationToken);

    Task SaveAsync(
        DateTime fromDate,
        DateTime toDate,
        string scopeKey,
        PodUploadStatusReportDto report,
        CancellationToken cancellationToken);
}

internal sealed class PodReportCacheStore(
    ApplicationDbContext context,
    IOptions<PodReportCacheSettings> options,
    ILogger<PodReportCacheStore> logger) : IPodReportCacheStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly PodReportCacheSettings _settings = options.Value;

    public bool Enabled => _settings.Enabled;

    public async Task<PodReportCacheSnapshot?> GetAsync(
        DateTime fromDate,
        DateTime toDate,
        string scopeKey,
        CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return null;
        }

        var cacheKey = CreateCacheKey(fromDate, toDate, scopeKey);

        try
        {
            var row = await context.PodReportCacheEntries
                .AsNoTracking()
                .SingleOrDefaultAsync(entry => entry.CacheKey == cacheKey, cancellationToken);

            if (row is null)
            {
                return null;
            }

            var now = DateTime.UtcNow;
            if (row.RefreshedAtUtc < now.AddDays(-Math.Max(1, _settings.RetentionDays)))
            {
                return null;
            }

            var report = JsonSerializer.Deserialize<PodUploadStatusReportDto>(
                row.PayloadJson,
                SerializerOptions);

            if (report is null)
            {
                logger.LogWarning("Stored POD report cache entry {CacheKey} contained no report", cacheKey);
                return null;
            }

            return new PodReportCacheSnapshot(
                report,
                row.RefreshedAtUtc,
                row.ExpiresAtUtc > now);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Cache availability must never make the live SAP report unavailable.
            logger.LogWarning(ex, "Failed to read POD report cache entry {CacheKey}", cacheKey);
            return null;
        }
    }

    public async Task SaveAsync(
        DateTime fromDate,
        DateTime toDate,
        string scopeKey,
        PodUploadStatusReportDto report,
        CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return;
        }

        var cacheKey = CreateCacheKey(fromDate, toDate, scopeKey);

        try
        {
            var now = DateTime.UtcNow;
            var payload = JsonSerializer.Serialize(report, SerializerOptions);
            var row = await context.PodReportCacheEntries
                .SingleOrDefaultAsync(entry => entry.CacheKey == cacheKey, cancellationToken);

            if (row is null)
            {
                row = new PodReportCacheEntryEntity
                {
                    CacheKey = cacheKey,
                    FromDate = fromDate.Date,
                    ToDate = toDate.Date,
                    ScopeKey = scopeKey
                };
                context.PodReportCacheEntries.Add(row);
            }

            row.PayloadJson = payload;
            row.RefreshedAtUtc = now;
            row.ExpiresAtUtc = now.AddMinutes(Math.Max(1, _settings.FreshnessMinutes));

            await context.SaveChangesAsync(cancellationToken);

            var retentionCutoff = now.AddDays(-Math.Max(1, _settings.RetentionDays));
            await context.PodReportCacheEntries
                .Where(entry => entry.ExpiresAtUtc < retentionCutoff)
                .ExecuteDeleteAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A concurrent first writer can win the unique key, or the cache table may not have
            // been migrated yet. Either case is non-fatal: this response still came from SAP.
            logger.LogWarning(ex, "Failed to persist POD report cache entry {CacheKey}", cacheKey);
        }
    }

    internal static string CreateCacheKey(DateTime fromDate, DateTime toDate, string scopeKey) =>
        $"{fromDate:yyyyMMdd}:{toDate:yyyyMMdd}:{scopeKey}";
}
