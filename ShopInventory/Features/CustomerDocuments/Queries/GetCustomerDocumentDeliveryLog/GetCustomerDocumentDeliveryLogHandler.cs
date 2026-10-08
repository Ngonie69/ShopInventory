using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.CustomerDocuments.Queries.GetCustomerDocumentDeliveryLog;

public sealed class GetCustomerDocumentDeliveryLogHandler(ApplicationDbContext context)
    : IRequestHandler<GetCustomerDocumentDeliveryLogQuery, ErrorOr<CustomerDocumentDeliveryPageDto>>
{
    public async Task<ErrorOr<CustomerDocumentDeliveryPageDto>> Handle(
        GetCustomerDocumentDeliveryLogQuery query,
        CancellationToken cancellationToken)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var deliveries = context.CustomerDocumentDeliveries.AsNoTracking();

        if (string.Equals(query.Status, "attention", StringComparison.OrdinalIgnoreCase))
        {
            // Written out rather than NeedsAttention.Contains(...): a parameter array of an enum stored
            // through a string converter is the shape providers have translated least reliably.
            deliveries = deliveries.Where(delivery =>
                delivery.Status == CustomerDocumentDeliveryStatus.Held
                || delivery.Status == CustomerDocumentDeliveryStatus.Uncertain
                || delivery.Status == CustomerDocumentDeliveryStatus.Failed);
        }
        else if (Enum.TryParse<CustomerDocumentDeliveryStatus>(query.Status, ignoreCase: true, out var status))
        {
            deliveries = deliveries.Where(delivery => delivery.Status == status);
        }

        if (Enum.TryParse<CustomerDocumentDeliveryTrigger>(query.Trigger, ignoreCase: true, out var trigger))
        {
            deliveries = deliveries.Where(delivery => delivery.Trigger == trigger);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            // Lower-cased on both sides rather than ILIKE, so the same query runs on PostgreSQL and on
            // the SQLite the tests use; Contains is a substring match on both, with nothing to escape.
            var search = query.Search.Trim();
            var lowered = search.ToLowerInvariant();
            deliveries = deliveries.Where(delivery =>
                delivery.DocumentNumber == search
                || (delivery.CardCode != null && delivery.CardCode.ToLower().Contains(lowered))
                || (delivery.CardName != null && delivery.CardName.ToLower().Contains(lowered))
                || (delivery.RouteCustomerName != null && delivery.RouteCustomerName.ToLower().Contains(lowered)));
        }

        if (query.FromDate is { } from)
        {
            var fromUtc = AuditService.FromCAT(from.Date);
            deliveries = deliveries.Where(delivery => delivery.CreatedAtUtc >= fromUtc);
        }

        if (query.ToDate is { } to)
        {
            var beforeUtc = AuditService.FromCAT(to.Date.AddDays(1));
            deliveries = deliveries.Where(delivery => delivery.CreatedAtUtc < beforeUtc);
        }

        var total = await deliveries.CountAsync(cancellationToken);
        var items = await deliveries
            .OrderByDescending(delivery => delivery.CreatedAtUtc)
            .ThenByDescending(delivery => delivery.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(CustomerDocumentProjections.Delivery)
            .ToListAsync(cancellationToken);

        return new CustomerDocumentDeliveryPageDto
        {
            Items = items,
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }
}
