using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.FactoryTransfers.Queries.GetFactoryTransferOptions;

/// <summary>
/// What the send form offers the caller: each of their warehouses that can send, and where to.
/// </summary>
/// <remarks>
/// Built from the same department table the send handler enforces, so the form never offers a route
/// the server would refuse.
/// </remarks>
public sealed class GetFactoryTransferOptionsHandler(
    ApplicationDbContext context,
    IUserManagementService userManagement)
    : IRequestHandler<GetFactoryTransferOptionsQuery, ErrorOr<FactoryTransferOptionsDto>>
{
    public async Task<ErrorOr<FactoryTransferOptionsDto>> Handle(
        GetFactoryTransferOptionsQuery query,
        CancellationToken cancellationToken)
    {
        var actor = await FactoryTransferActor.ResolveAsync(context, userManagement, query.UserId, cancellationToken);
        if (actor is null)
            return Errors.FactoryTransfer.UserNotFound;

        var routes = new List<FactoryTransferRouteDto>();
        if (actor.CanSend)
        {
            foreach (var department in FactoryDepartments.All)
            {
                foreach (var production in department.ProductionWarehouses.Where(actor.Holds))
                {
                    routes.Add(new FactoryTransferRouteDto
                    {
                        SourceWarehouseCode = production,
                        DepartmentCode = department.Code,
                        DepartmentName = department.Name,
                        Direction = FactoryTransferDirections.Release,
                        DestinationWarehouseCodes = [department.DispatchWarehouse]
                    });
                }

                if (actor.Holds(department.DispatchWarehouse))
                {
                    routes.Add(new FactoryTransferRouteDto
                    {
                        SourceWarehouseCode = department.DispatchWarehouse,
                        DepartmentCode = department.Code,
                        DepartmentName = department.Name,
                        Direction = FactoryTransferDirections.Return,
                        DestinationWarehouseCodes = department.ProductionWarehouses.ToList()
                    });
                }
            }

            if (actor.Holds(FactoryDepartments.WashbayWarehouseCode))
            {
                routes.Add(new FactoryTransferRouteDto
                {
                    SourceWarehouseCode = FactoryDepartments.WashbayWarehouseCode,
                    DepartmentCode = FactoryDepartments.WashbayDepartmentCode,
                    DepartmentName = "Washbay",
                    Direction = FactoryTransferDirections.CrateIssue,
                    DestinationWarehouseCodes = FactoryDepartments.WashbayIssueDestinations.ToList(),
                    PackagingOnly = true
                });
                foreach (var source in FactoryDepartments.WashbayReceiptSources)
                {
                    routes.Add(new FactoryTransferRouteDto
                    {
                        SourceWarehouseCode = source,
                        DepartmentCode = FactoryDepartments.WashbayDepartmentCode,
                        DepartmentName = "Washbay",
                        Direction = FactoryTransferDirections.CrateReceipt,
                        DestinationWarehouseCodes = [FactoryDepartments.WashbayWarehouseCode],
                        PackagingOnly = true
                    });
                }
            }

            // A depot sends stock back to dispatch from its centre or the shop beside it.
            foreach (var depot in FactoryDepartments.Depots)
            {
                foreach (var warehouse in depot.Warehouses.Where(actor.Holds))
                {
                    routes.Add(new FactoryTransferRouteDto
                    {
                        SourceWarehouseCode = warehouse,
                        DepartmentCode = FactoryDepartments.DepotDepartmentCode,
                        DepartmentName = depot.Name,
                        Direction = FactoryTransferDirections.DepotReturn,
                        DestinationWarehouseCodes = FactoryDepartments.DispatchWarehouses.ToList()
                    });
                }
            }
        }

        DepotDeliveryOptionsDto? delivery = null;
        if (actor.CanDeliver)
        {
            delivery = new DepotDeliveryOptionsDto
            {
                HoldingWarehouseCode = FactoryDepartments.DepotHoldingWarehouseCode,
                DispatchWarehouses = FactoryDepartments.DispatchWarehouses.Where(actor.Holds).ToList(),
                Depots = FactoryDepartments.Depots.Select(depot => new DepotDto { Code = depot.Centre, Name = depot.Name }).ToList()
            };
        }

        return new FactoryTransferOptionsDto
        {
            HoldingWarehouseCode = FactoryDepartments.HoldingWarehouseCode,
            CanSend = actor.CanSend,
            CanAccept = actor.CanAccept,
            CanAcceptAny = actor.CanAcceptAny,
            AssignedWarehouses = actor.Warehouses.ToList(),
            Routes = routes,
            DepotDelivery = delivery
        };
    }
}
