using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using ShopInventory.DTOs;
using ShopInventory.Features.Products.Queries.GetAllProducts;
using ShopInventory.Features.Products.Queries.GetPagedProductsInWarehouse;
using ShopInventory.Features.Products.Queries.GetProductBatches;
using ShopInventory.Features.Products.Queries.GetProductByCode;
using ShopInventory.Features.Products.Queries.GetProductsInWarehouse;

namespace ShopInventory.Controllers;

[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
public class ProductController(IMediator mediator) : ApiControllerBase
{
    /// <summary>
    /// Gets all products/items from SAP
    /// </summary>
    [HttpGet]
    [OutputCache(PolicyName = "master-data")]
    [ProducesResponseType(typeof(ProductsListResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetAllProducts(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetAllProductsQuery(), cancellationToken);

        return result.Match(
            value => Ok(value),
            errors => Problem(errors)
        );
    }

    /// <summary>
    /// Gets all products in a warehouse with their batch information
    /// </summary>
    [HttpGet("warehouse/{warehouseCode}")]
    [ProducesResponseType(typeof(WarehouseProductsResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetProductsInWarehouse(
        string warehouseCode,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new GetProductsInWarehouseQuery(warehouseCode), cancellationToken);

        return result.Match(
            value => Ok(value),
            errors => Problem(errors)
        );
    }

    /// <summary>
    /// Gets products in a warehouse with pagination
    /// </summary>
    [HttpGet("warehouse/{warehouseCode}/paged")]
    [ProducesResponseType(typeof(WarehouseProductsPagedResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetPagedProductsInWarehouse(
        string warehouseCode,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(
            new GetPagedProductsInWarehouseQuery(warehouseCode, page, pageSize), cancellationToken);

        return result.Match(
            value => Ok(value),
            errors => Problem(errors)
        );
    }

    /// <summary>
    /// Gets batch information for a specific product in a warehouse
    /// </summary>
    [HttpGet("warehouse/{warehouseCode}/item/{itemCode}/batches")]
    [ProducesResponseType(typeof(ProductBatchesResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetProductBatchesInWarehouse(
        string warehouseCode,
        string itemCode,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new GetProductBatchesQuery(warehouseCode, itemCode), cancellationToken);

        return result.Match(
            value => Ok(value),
            errors => Problem(errors)
        );
    }

    /// <summary>
    /// Gets a product by its item code
    /// </summary>
    [HttpGet("{itemCode}")]
    [ProducesResponseType(typeof(ProductDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetProductByCode(
        string itemCode,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new GetProductByCodeQuery(itemCode), cancellationToken);

        return result.Match(
            value => Ok(value),
            errors => Problem(errors)
        );
    }
}
