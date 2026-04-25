using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using ShopInventory.DTOs;
using ShopInventory.Features.Stock.Queries.GetWarehouses;
using ShopInventory.Features.Stock.Queries.GetWarehouseCodes;
using ShopInventory.Features.Stock.Queries.GetStockForItemsInWarehouse;
using ShopInventory.Features.Stock.Queries.GetStockInWarehouse;
using ShopInventory.Features.Stock.Queries.GetStockInWarehousePaged;
using ShopInventory.Features.Stock.Queries.GetSalesInWarehouse;
using ShopInventory.Features.Stock.Queries.GetSalesInWarehousePost;

namespace ShopInventory.Controllers;

[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
public class StockController(IMediator mediator) : ApiControllerBase
{
    [HttpGet("warehouses")]
    [OutputCache(PolicyName = "warehouses")]
    public async Task<IActionResult> GetWarehouses(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetWarehousesQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("warehouse-codes")]
    [OutputCache(PolicyName = "warehouses")]
    public async Task<IActionResult> GetWarehouseCodes(
        [FromQuery] bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetWarehouseCodesQuery(includeInactive), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("warehouse/{warehouseCode}")]
    public async Task<IActionResult> GetStockInWarehouse(
        string warehouseCode,
        [FromQuery] bool includePackagingStock = true,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetStockInWarehouseQuery(warehouseCode, includePackagingStock), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("warehouse/{warehouseCode}/paged")]
    public async Task<IActionResult> GetStockInWarehousePaged(
        string warehouseCode,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetStockInWarehousePagedQuery(warehouseCode, page, pageSize), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("warehouse/{warehouseCode}/items")]
    public async Task<IActionResult> GetStockForItemsInWarehouse(
        string warehouseCode,
        [FromQuery] string itemCodes,
        CancellationToken cancellationToken = default)
    {
        var codes = itemCodes
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var result = await mediator.Send(new GetStockForItemsInWarehouseQuery(warehouseCode, codes), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("warehouse/{warehouseCode}/sales")]
    public async Task<IActionResult> GetSalesInWarehouse(
        string warehouseCode,
        [FromQuery] DateTime fromDate,
        [FromQuery] DateTime toDate,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetSalesInWarehouseQuery(warehouseCode, fromDate, toDate), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPost("warehouse/{warehouseCode}/sales")]
    public async Task<IActionResult> GetSalesInWarehousePost(
        string warehouseCode,
        [FromBody] SalesQueryRequestDto request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetSalesInWarehousePostQuery(warehouseCode, request.FromDate, request.ToDate), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }
}
