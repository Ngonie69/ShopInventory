using System.Text.Json;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetFiscalizedSalesReport;

public sealed class GetFiscalizedSalesReportHandler(
    ApplicationDbContext db,
    ILogger<GetFiscalizedSalesReportHandler> logger
) : IRequestHandler<GetFiscalizedSalesReportQuery, ErrorOr<FiscalizedSalesReportResult>>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public async Task<ErrorOr<FiscalizedSalesReportResult>> Handle(
        GetFiscalizedSalesReportQuery request,
        CancellationToken cancellationToken)
    {
        // Resolve period to concrete date range
        var (from, to) = ResolveDateRange(request);

        // Base query: all fiscalized + completed (consolidated) entries within the range
        var query = db.InvoiceQueue
            .AsNoTracking()
            .Where(q => q.Status == InvoiceQueueStatus.Fiscalized ||
                        q.Status == InvoiceQueueStatus.Completed)
            .Where(q => q.CreatedAt >= from && q.CreatedAt < to)
            .AsQueryable();

        // Apply optional filters
        if (!string.IsNullOrEmpty(request.CardCode))
            query = query.Where(q => q.CustomerCode == request.CardCode);

        if (!string.IsNullOrEmpty(request.WarehouseCode))
            query = query.Where(q => q.WarehouseCode == request.WarehouseCode);

        if (request.IsConsolidated.HasValue)
        {
            if (request.IsConsolidated.Value)
                query = query.Where(q => q.Status == InvoiceQueueStatus.Completed && q.SapDocNum != null);
            else
                query = query.Where(q => q.Status == InvoiceQueueStatus.Fiscalized);
        }

        // Total count for pagination
        var totalCount = await query.CountAsync(cancellationToken);

        // Build summary from the full filtered set (before pagination)
        var summary = await BuildSummaryAsync(query, cancellationToken);

        // Build daily breakdown
        var dailyBreakdown = await BuildDailyBreakdownAsync(query, cancellationToken);

        // Fetch paginated entries
        var entries = await query
            .OrderByDescending(q => q.CreatedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync(cancellationToken);

        var sales = entries.Select(MapToDto).ToList();
        var hasMore = (request.Page * request.PageSize) < totalCount;

        return new FiscalizedSalesReportResult(
            GeneratedAtUtc: DateTime.UtcNow,
            Period: request.Period.ToString(),
            FromDate: from,
            ToDate: to.AddDays(-1), // inclusive end date for display
            Summary: summary,
            DailyBreakdown: dailyBreakdown,
            Sales: sales,
            TotalCount: totalCount,
            Page: request.Page,
            PageSize: request.PageSize,
            HasMore: hasMore
        );
    }

    /// <summary>
    /// Resolves the report period to a concrete [from, to) date range.
    /// 'to' is exclusive (start of the next day after the range).
    /// </summary>
    private static (DateTime from, DateTime to) ResolveDateRange(GetFiscalizedSalesReportQuery request)
    {
        var referenceDate = (request.Date ?? DateTime.UtcNow).Date;

        var (from, to) = request.Period switch
        {
            ReportPeriod.Daily => (referenceDate, referenceDate.AddDays(1)),

            ReportPeriod.Weekly => (
                referenceDate.AddDays(-(int)referenceDate.DayOfWeek + (int)DayOfWeek.Monday),
                referenceDate.AddDays(-(int)referenceDate.DayOfWeek + (int)DayOfWeek.Monday + 7)),

            ReportPeriod.Monthly => (
                new DateTime(referenceDate.Year, referenceDate.Month, 1),
                new DateTime(referenceDate.Year, referenceDate.Month, 1).AddMonths(1)),

            ReportPeriod.Custom => (
                (request.FromDate ?? referenceDate).Date,
                (request.ToDate ?? referenceDate).Date.AddDays(1)),

            _ => (referenceDate, referenceDate.AddDays(1))
        };

        // Npgsql requires DateTime.Kind == Utc for timestamptz columns
        return (DateTime.SpecifyKind(from, DateTimeKind.Utc),
                DateTime.SpecifyKind(to, DateTimeKind.Utc));
    }

    private static async Task<List<DailyBreakdownDto>> BuildDailyBreakdownAsync(
        IQueryable<InvoiceQueueEntity> query,
        CancellationToken cancellationToken)
    {
        var dailyData = await query
            .GroupBy(q => q.CreatedAt.Date)
            .Select(g => new DailyBreakdownDto
            {
                Date = g.Key,
                SalesCount = g.Count(),
                TotalAmount = g.Sum(q => q.TotalAmount),
                VatAmount = g.Sum(q => q.TotalAmount) * 0.155m / 1.155m,
                ConsolidatedCount = g.Count(q => q.Status == InvoiceQueueStatus.Completed && q.SapDocNum != null),
                AwaitingConsolidationCount = g.Count(q => q.Status == InvoiceQueueStatus.Fiscalized)
            })
            .OrderBy(d => d.Date)
            .ToListAsync(cancellationToken);

        // Round in-memory
        foreach (var d in dailyData)
            d.VatAmount = Math.Round(d.VatAmount, 2);

        return dailyData;
    }

    private static async Task<FiscalizedSalesReportSummary> BuildSummaryAsync(
        IQueryable<InvoiceQueueEntity> query,
        CancellationToken cancellationToken)
    {
        var statusSummary = await query
            .GroupBy(q => q.Status)
            .Select(g => new
            {
                Status = g.Key,
                Count = g.Count(),
                TotalAmount = g.Sum(q => q.TotalAmount),
                ConsolidatedCount = g.Count(q => q.SapDocNum != null)
            })
            .ToListAsync(cancellationToken);

        // Amount by warehouse
        var warehouseAmounts = await query
            .Where(q => q.WarehouseCode != null)
            .GroupBy(q => q.WarehouseCode!)
            .Select(g => new { Warehouse = g.Key, Amount = g.Sum(q => q.TotalAmount) })
            .ToListAsync(cancellationToken);

        // Amount by customer
        var customerAmounts = await query
            .GroupBy(q => q.CustomerCode)
            .Select(g => new { Customer = g.Key, Amount = g.Sum(q => q.TotalAmount) })
            .ToListAsync(cancellationToken);

        var totalFiscalized = statusSummary.Sum(x => x.Count);
        var consolidated = statusSummary
            .Where(x => x.Status == InvoiceQueueStatus.Completed)
            .Sum(x => x.ConsolidatedCount);
        var awaitingConsolidation = statusSummary
            .Where(x => x.Status == InvoiceQueueStatus.Fiscalized)
            .Sum(x => x.Count);
        var totalAmount = statusSummary.Sum(x => x.TotalAmount);
        var uniqueCustomers = customerAmounts.Count;
        var uniqueWarehouses = warehouseAmounts.Count;
        var topCustomerAmounts = customerAmounts
            .OrderByDescending(x => x.Amount)
            .Take(20)
            .ToList();

        // Approximate VAT (15.5% on VAT-exclusive totals)
        var vatAmount = Math.Round(totalAmount * 0.155m / 1.155m, 2);

        return new FiscalizedSalesReportSummary
        {
            TotalFiscalizedSales = totalFiscalized,
            ConsolidatedCount = consolidated,
            AwaitingConsolidationCount = awaitingConsolidation,
            TotalSalesAmount = totalAmount,
            TotalVatAmount = vatAmount,
            UniqueCustomers = uniqueCustomers,
            UniqueWarehouses = uniqueWarehouses,
            AmountByWarehouse = warehouseAmounts.ToDictionary(x => x.Warehouse, x => x.Amount),
            AmountByCustomer = topCustomerAmounts.ToDictionary(x => x.Customer, x => x.Amount)
        };
    }

    private FiscalizedSaleDto MapToDto(InvoiceQueueEntity entry)
    {
        var lines = DeserializeLines(entry);
        var isConsolidated = entry.Status == InvoiceQueueStatus.Completed && entry.SapDocNum != null;

        // Approximate VAT from total (15.5% VAT-inclusive)
        var vatAmount = Math.Round(entry.TotalAmount * 0.155m / 1.155m, 2);

        return new FiscalizedSaleDto
        {
            QueueId = entry.Id,
            ExternalReference = entry.ExternalReference,
            ReservationId = entry.ReservationId,
            Status = entry.Status.ToString(),
            CustomerCode = entry.CustomerCode,
            CustomerName = GetCustomerNameFromPayload(entry),
            TotalAmount = entry.TotalAmount,
            VatAmount = vatAmount,
            Currency = entry.Currency,
            WarehouseCode = entry.WarehouseCode,
            FiscalDeviceNumber = entry.FiscalDeviceNumber,
            FiscalReceiptNumber = entry.FiscalReceiptNumber,
            FiscalizationSuccess = entry.FiscalizationSuccess,
            SapDocEntry = entry.SapDocEntry,
            SapDocNum = entry.SapDocNum,
            IsConsolidated = isConsolidated,
            CreatedAt = entry.CreatedAt,
            FiscalizedAt = entry.ProcessedAt,
            ConsolidatedAt = isConsolidated ? entry.ProcessedAt : null,
            SourceSystem = entry.SourceSystem,
            CreatedBy = entry.CreatedBy,
            Notes = entry.Notes,
            Lines = lines
        };
    }

    private List<FiscalizedSaleLineDto> DeserializeLines(InvoiceQueueEntity entry)
    {
        try
        {
            var request = JsonSerializer.Deserialize<CreateStockReservationRequest>(
                entry.InvoicePayload, JsonOptions);

            if (request?.Lines == null) return new();

            return request.Lines.Select((l, idx) => new FiscalizedSaleLineDto
            {
                LineNum = l.LineNum > 0 ? l.LineNum : idx,
                ItemCode = l.ItemCode,
                ItemDescription = l.ItemDescription,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice,
                LineTotal = Math.Round(l.Quantity * l.UnitPrice * (1 - l.DiscountPercent / 100m), 2),
                DiscountPercent = l.DiscountPercent,
                WarehouseCode = l.WarehouseCode,
                TaxCode = l.TaxCode,
                UoMCode = l.UoMCode
            }).ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to deserialize lines for queue entry {QueueId}", entry.Id);
            return new();
        }
    }

    private static string? GetCustomerNameFromPayload(InvoiceQueueEntity entry)
    {
        try
        {
            var request = JsonSerializer.Deserialize<CreateStockReservationRequest>(
                entry.InvoicePayload, JsonOptions);
            return request?.CardName;
        }
        catch
        {
            return null;
        }
    }
}
