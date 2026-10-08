using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;

namespace ShopInventory.Features.CustomerDocuments.Queries.GetCustomerDocumentDeliveries;

public sealed class GetCustomerDocumentDeliveriesHandler(ApplicationDbContext context)
    : IRequestHandler<GetCustomerDocumentDeliveriesQuery, ErrorOr<List<CustomerDocumentDeliveryDto>>>
{
    public async Task<ErrorOr<List<CustomerDocumentDeliveryDto>>> Handle(
        GetCustomerDocumentDeliveriesQuery query,
        CancellationToken cancellationToken)
    {
        if (query.SapDocEntry is null && query.DesktopSaleId is null)
            return Error.Validation("CustomerDocuments.DocumentRequired", "Name the invoice or the sale.");

        var deliveries = context.CustomerDocumentDeliveries.AsNoTracking();

        deliveries = query.SapDocEntry is { } docEntry
            ? deliveries.Where(delivery => delivery.SapDocEntry == docEntry)
            : deliveries.Where(delivery => delivery.DesktopSaleId == query.DesktopSaleId);

        return await deliveries
            .OrderByDescending(delivery => delivery.CreatedAtUtc)
            .ThenByDescending(delivery => delivery.Id)
            .Take(100)
            .Select(CustomerDocumentProjections.Delivery)
            .ToListAsync(cancellationToken);
    }
}
