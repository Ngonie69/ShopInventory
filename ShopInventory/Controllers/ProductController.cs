using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using ShopInventory.DTOs;
using ShopInventory.Features.Products.Queries.GetAllProducts;
using ShopInventory.Features.Products.Queries.GetItemGroups;
using ShopInventory.Features.Products.Queries.GetPagedProductsInWarehouse;
using ShopInventory.Features.Products.Queries.GetProductBatches;
using ShopInventory.Features.Products.Queries.GetProductByCode;
using ShopInventory.Features.Products.Queries.GetProductsInWarehouse;
using ShopInventory.Features.Products.Queries.GetVanSaleCatalogue;

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
    /// Gets the van sales approved catalogue — every item flagged <c>U_VanSale = 'Yes'</c> in SAP
    /// </summary>
    /// <remarks>
    /// Not warehouse-scoped, unlike <c>warehouse/{code}/paged?vanSaleOnly=true</c>, which intersects
    /// the same flag with the codes holding stock. That intersection answers what a van can sell; this
    /// answers what a van may be sent. A stock transfer request needs the second one — the items worth
    /// asking the depot for are precisely the ones the van has none of, and those are absent from the
    /// warehouse page by design.
    /// <para>
    /// Prices are not populated: a transfer request has no customer to price against. Callers that
    /// need a figure read <c>/price/grouped</c>, as they already do for products SAP prices at zero.
    /// </para>
    /// </remarks>
    [HttpGet("van-sale-catalogue")]
    [OutputCache(PolicyName = "master-data")]
    [ProducesResponseType(typeof(ProductsListResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetVanSaleCatalogue(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetVanSaleCatalogueQuery(), cancellationToken);

        return result.Match(
            value => Ok(value),
            errors => Problem(errors)
        );
    }

    /// <summary>
    /// Gets SAP's item groups, so a group code on a product can be shown as a name
    /// </summary>
    [HttpGet("groups")]
    [OutputCache(PolicyName = "master-data")]
    [ProducesResponseType(typeof(ItemGroupsListResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetItemGroups(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetItemGroupsQuery(), cancellationToken);

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
    /// <remarks>
    /// Pass <c>vanSaleOnly=true</c> to narrow the page to the van sales approved catalogue — the
    /// items flagged <c>U_VanSale = 'Yes'</c> in SAP. It is opt-in because the web's master-data
    /// cache reads this same route and needs every item.
    /// <para>
    /// Anything reading the whole warehouse should pass <c>cursor=true</c> and then follow
    /// <c>nextCursor</c> back in as <c>after</c>. The list being paged is the codes holding stock
    /// right now, so it moves between pages; offsets skip items when it does, and an offset page is
    /// not guaranteed dense. Offsets stay the default only because handsets in the field page that way.
    /// </para>
    /// </remarks>
    [HttpGet("warehouse/{warehouseCode}/paged")]
    [ProducesResponseType(typeof(WarehouseProductsPagedResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetPagedProductsInWarehouse(
        string warehouseCode,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? businessPartnerCode = null,
        [FromQuery] int? priceListNum = null,
        [FromQuery] bool vanSaleOnly = false,
        [FromQuery] bool cursor = false,
        [FromQuery] string? after = null,
        CancellationToken cancellationToken = default)
    {
        // A blank cursor is how "start from the beginning" arrives from most clients, and it means
        // the same thing as sending none — not a position, and not a reason to refuse the request.
        var normalizedAfter = string.IsNullOrWhiteSpace(after) ? null : after.Trim();

        var result = await mediator.Send(
            new GetPagedProductsInWarehouseQuery(
                warehouseCode, page, pageSize, businessPartnerCode, priceListNum, vanSaleOnly, cursor, normalizedAfter),
            cancellationToken);

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
