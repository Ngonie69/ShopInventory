using MediatR;
using ShopInventory.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Authentication;
using ShopInventory.DTOs;
using ShopInventory.Features.Merchandiser.Commands.AssignProducts;
using ShopInventory.Features.Merchandiser.Commands.AssignProductsGlobal;
using ShopInventory.Features.Merchandiser.Commands.RemoveProducts;
using ShopInventory.Features.Merchandiser.Commands.RemoveProductsGlobal;
using ShopInventory.Features.Merchandiser.Commands.SubmitMobileOrder;
using ShopInventory.Features.Merchandiser.Commands.UpdateProductStatus;
using ShopInventory.Features.Merchandiser.Commands.UpdateProductStatusGlobal;
using ShopInventory.Features.Merchandiser.Queries.GetActiveProducts;
using ShopInventory.Features.Merchandiser.Queries.GetActiveProductsByUser;
using ShopInventory.Features.Merchandiser.Queries.GetCustomerProducts;
using ShopInventory.Features.Merchandiser.Queries.GetGlobalProducts;
using ShopInventory.Features.Merchandiser.Queries.GetMerchandiserProducts;
using ShopInventory.Features.Merchandiser.Queries.GetMerchandisers;
using ShopInventory.Features.Merchandiser.Queries.GetMobileOrders;
using ShopInventory.Features.Merchandiser.Queries.GetProductCategories;
using ShopInventory.Features.Merchandiser.Queries.GetSapSalesItems;
using ShopInventory.Models.Entities;
using System.Security.Claims;

namespace ShopInventory.Controllers;

[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
[Produces("application/json")]
public class MerchandiserController(IMediator mediator) : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetMerchandisers(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetMerchandisersQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("{userId:guid}/products")]
    public async Task<IActionResult> GetMerchandiserProducts(Guid userId, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetMerchandiserProductsQuery(userId), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("products")]
    public async Task<IActionResult> GetGlobalProducts(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetGlobalProductsQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("sap-sales-items")]
    public async Task<IActionResult> GetSapSalesItems(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetSapSalesItemsQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPost("products")]
    public async Task<IActionResult> AssignProductsGlobal([FromBody] AssignMerchandiserProductsRequest request, CancellationToken cancellationToken)
    {
        var username = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
        var result = await mediator.Send(new AssignProductsGlobalCommand(request, username), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPost("{userId:guid}/products")]
    public async Task<IActionResult> AssignProducts(Guid userId, [FromBody] AssignMerchandiserProductsRequest request, CancellationToken cancellationToken)
    {
        var username = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
        var result = await mediator.Send(new AssignProductsCommand(userId, request, username), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpDelete("products")]
    public async Task<IActionResult> RemoveProductsGlobal([FromBody] AssignMerchandiserProductsRequest request, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new RemoveProductsGlobalCommand(request), cancellationToken);
        return result.Match(_ => NoContent(), errors => Problem(errors));
    }

    [HttpPut("products/status")]
    public async Task<IActionResult> UpdateProductStatusGlobal([FromBody] UpdateMerchandiserProductStatusRequest request, CancellationToken cancellationToken)
    {
        var username = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
        var result = await mediator.Send(new UpdateProductStatusGlobalCommand(request, username), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPut("{userId:guid}/products/status")]
    public async Task<IActionResult> UpdateProductStatus(Guid userId, [FromBody] UpdateMerchandiserProductStatusRequest request, CancellationToken cancellationToken)
    {
        var username = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
        var result = await mediator.Send(new UpdateProductStatusCommand(userId, request, username), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpDelete("{userId:guid}/products")]
    public async Task<IActionResult> RemoveProducts(Guid userId, [FromBody] AssignMerchandiserProductsRequest request, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new RemoveProductsCommand(userId, request), cancellationToken);
        return result.Match(_ => NoContent(), errors => Problem(errors));
    }

    [HttpGet("mobile/categories")]
    public async Task<IActionResult> GetProductCategories(CancellationToken cancellationToken)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(userIdClaim, out var userId))
            return Forbid();

        var result = await mediator.Send(new GetProductCategoriesQuery(userId), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("mobile/active-products")]
    public async Task<IActionResult> GetActiveProductsForMobile(
        [FromQuery] string? search = null,
        [FromQuery] string? category = null,
        CancellationToken cancellationToken = default)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(userIdClaim, out var userId))
            return Forbid();

        var result = await mediator.Send(new GetActiveProductsQuery(userId, search, category), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("{userId:guid}/active-products")]
    public async Task<IActionResult> GetActiveProductsForMerchandiser(
        Guid userId,
        [FromQuery] string? search = null,
        [FromQuery] string? category = null,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetActiveProductsByUserQuery(userId, search, category), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("mobile/customer/{cardCode}/products")]
    public async Task<IActionResult> GetActiveProductsForCustomer(
        string cardCode,
        [FromQuery] string? search = null,
        [FromQuery] string? category = null,
        CancellationToken cancellationToken = default)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(userIdClaim, out var userId))
            return Forbid();

        var result = await mediator.Send(new GetCustomerProductsQuery(userId, cardCode, search, category), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPost("mobile/order")]
    [RequirePermission(Permission.CreateSalesOrders)]
    public async Task<IActionResult> SubmitMobileOrder([FromBody] MerchandiserOrderRequest request, CancellationToken cancellationToken)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(userIdClaim, out var userId))
            return Forbid();

        var result = await mediator.Send(new SubmitMobileOrderCommand(request, userId), cancellationToken);
        return result.Match(value => CreatedAtAction(nameof(GetMobileOrders), null, value), errors => Problem(errors));
    }

    [HttpGet("mobile/orders")]
    public async Task<IActionResult> GetMobileOrders(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] SalesOrderStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(userIdClaim, out var userId))
            return Forbid();

        var result = await mediator.Send(new GetMobileOrdersQuery(userId, page, pageSize, status), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }
}
