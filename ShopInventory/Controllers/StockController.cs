using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.DTOs;
using ShopInventory.Features.Stock.Queries.GetWarehouses;
using ShopInventory.Features.Stock.Queries.GetWarehouseCodes;
using ShopInventory.Features.Stock.Queries.GetStockForItemsInWarehouse;
using ShopInventory.Features.Stock.Queries.GetStockInWarehouse;
using ShopInventory.Features.Stock.Queries.GetStockInWarehousePaged;
using ShopInventory.Features.Stock.Queries.GetSalesInWarehouse;
using ShopInventory.Features.Stock.Queries.GetSalesInWarehousePost;
using ShopInventory.Features.Stock.Queries.GetStockAsAt;
using ShopInventory.Features.Stock.Queries.GetStockMovements;
using ShopInventory.Features.Stock.Queries.GetStockMovementTotals;

namespace ShopInventory.Controllers;

[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
public class StockController(IMediator mediator) : ApiControllerBase
{
    /// <summary>
    /// Get all warehouses
    /// </summary>
    [HttpGet("warehouses")]
    public async Task<IActionResult> GetWarehouses(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetWarehousesQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Get just warehouse codes
    /// </summary>
    [HttpGet("warehouse-codes")]
    public async Task<IActionResult> GetWarehouseCodes(
        [FromQuery] bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetWarehouseCodesQuery(includeInactive), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Get all stock in a specific warehouse
    /// </summary>
    [HttpGet("warehouse/{warehouseCode}")]
    public async Task<IActionResult> GetStockInWarehouse(
        string warehouseCode,
        [FromQuery] bool includePackagingStock = true,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetStockInWarehouseQuery(warehouseCode, includePackagingStock), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// What a warehouse held at a past moment, per item, rebuilt from the stock ledger
    /// (the morning snapshot plus every journalled movement after it). <paramref name="at"/> is UTC.
    /// </summary>
    [HttpGet("warehouse/{warehouseCode}/as-at")]
    public async Task<IActionResult> GetStockAsAt(
        string warehouseCode,
        [FromQuery] DateTime at,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(
            new GetStockAsAtQuery(warehouseCode, DateTime.SpecifyKind(at.ToUniversalTime(), DateTimeKind.Utc)),
            cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// The stock ledger's movements in a warehouse between two UTC moments, optionally for one item.
    /// </summary>
    [HttpGet("warehouse/{warehouseCode}/movements")]
    public async Task<IActionResult> GetStockMovements(
        string warehouseCode,
        [FromQuery] DateTime fromUtc,
        [FromQuery] DateTime toUtc,
        [FromQuery] string? itemCode = null,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(
            new GetStockMovementsQuery(
                warehouseCode,
                DateTime.SpecifyKind(fromUtc.ToUniversalTime(), DateTimeKind.Utc),
                DateTime.SpecifyKind(toUtc.ToUniversalTime(), DateTimeKind.Utc),
                itemCode),
            cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// The stock ledger's movements between two UTC moments, totalled per item: sold, transferred in
    /// and out, and corrected.
    /// </summary>
    [HttpGet("warehouse/{warehouseCode}/movement-totals")]
    public async Task<IActionResult> GetStockMovementTotals(
        string warehouseCode,
        [FromQuery] DateTime fromUtc,
        [FromQuery] DateTime toUtc,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(
            new GetStockMovementTotalsQuery(
                warehouseCode,
                DateTime.SpecifyKind(fromUtc.ToUniversalTime(), DateTimeKind.Utc),
                DateTime.SpecifyKind(toUtc.ToUniversalTime(), DateTimeKind.Utc)),
            cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// List a warehouse's stock, paginated
    /// </summary>
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

    /// <summary>
    /// Stock for named items only — itemCodes is a comma-separated list
    /// </summary>
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

    /// <summary>
    /// Sales out of a warehouse
    /// </summary>
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

    /// <summary>
    /// A warehouse's sales, with the date range in the body rather than the query string
    /// </summary>
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
