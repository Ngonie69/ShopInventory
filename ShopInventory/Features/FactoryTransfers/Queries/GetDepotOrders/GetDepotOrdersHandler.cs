using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.FactoryTransfers.Queries.GetDepotOrders;

/// <summary>
/// The open transfer requests a depot raised: what a truck loaded for it can fulfil.
/// </summary>
/// <remarks>
/// Read from SAP rather than the local approval rows, because a request copied in the SAP client
/// leaves no local trace: SAP is the only place that knows it is still open.
/// </remarks>
public sealed class GetDepotOrdersHandler(
    ApplicationDbContext context,
    IUserManagementService userManagement,
    ISAPServiceLayerClient sapClient,
    ILogger<GetDepotOrdersHandler> logger)
    : IRequestHandler<GetDepotOrdersQuery, ErrorOr<List<DepotOrderDto>>>
{
    public async Task<ErrorOr<List<DepotOrderDto>>> Handle(GetDepotOrdersQuery query, CancellationToken cancellationToken)
    {
        var actor = await FactoryTransferActor.ResolveAsync(context, userManagement, query.UserId, cancellationToken);
        if (actor is null)
            return Errors.FactoryTransfer.UserNotFound;
        if (!actor.CanDeliver)
            return Errors.FactoryTransfer.DeliverNotPermitted;

        var depot = FactoryDepartments.FindDepotCentre(query.DepotCode);
        if (depot is null)
            return Errors.FactoryTransfer.NotADepot(query.DepotCode.Trim());

        try
        {
            // Today as the floor: the read always includes every request still open, whatever its date.
            var requests = await sapClient.GetInventoryTransferRequestsByWarehouseAsync(depot.Centre, DateTime.UtcNow.Date, cancellationToken);
            return requests
                .Where(request => string.Equals(request.DocumentStatus, "bost_Open", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(request.ToWarehouse, depot.Centre, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(request => request.DocEntry)
                .Select(request => new DepotOrderDto
                {
                    DocEntry = request.DocEntry,
                    DocNum = request.DocNum,
                    DocDate = request.DocDate ?? string.Empty,
                    DepotCode = depot.Centre,
                    Comments = request.Comments ?? string.Empty,
                    Lines = (request.StockTransferLines ?? [])
                        .Where(line => !string.IsNullOrWhiteSpace(line.ItemCode) && line.Quantity > 0)
                        .Select(line => new DepotOrderLineDto
                        {
                            ItemCode = line.ItemCode!,
                            ItemDescription = line.ItemDescription ?? string.Empty,
                            Quantity = line.Quantity
                        })
                        .ToList()
                })
                .ToList();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Could not read the open orders for depot {Depot}", depot.Centre);
            return Errors.FactoryTransfer.PostFailed($"Could not read {depot.Name}'s open orders from SAP. Try again shortly.");
        }
    }
}
