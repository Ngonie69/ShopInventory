using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.DTOs;
using ShopInventory.Features.Shops.Commands.CreateShop;
using ShopInventory.Features.Shops.Commands.SetShopActive;
using ShopInventory.Features.Shops.Commands.UpdateShop;
using ShopInventory.Features.Shops.Queries.GetShopById;
using ShopInventory.Features.Shops.Queries.GetShops;
using ShopInventory.Common.Security;
using ShopInventory.Models;

namespace ShopInventory.Controllers;

/// <summary>
/// Retail shops — the business partner, warehouse and cost centre a till operator sells on.
/// </summary>
/// <remarks>
/// Administrator-only throughout. A shop's warehouse decides both what its tills sell from and which
/// sales its operators can read, so editing one is a change to who can see whose money.
/// </remarks>
[Route("api/[controller]")]
[Authorize(Roles = ApplicationRoles.Admin)]
public class ShopsController(IMediator mediator) : ApiControllerBase
{
    /// <summary>
    /// List shops. Closed ones are excluded unless asked for.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetShops(
        [FromQuery] bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetShopsQuery(includeInactive), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// One shop
    /// </summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetShop(int id, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetShopByIdQuery(id), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Open a shop
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> CreateShop(
        [FromBody] CreateShopRequest request,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
            return Unauthorized();

        var result = await mediator.Send(new CreateShopCommand(request, userId.Value), cancellationToken);
        return result.Match(
            value => CreatedAtAction(nameof(GetShop), new { id = value.Id }, value),
            errors => Problem(errors));
    }

    /// <summary>
    /// Change a shop's name, business partner, warehouse or cost centre
    /// </summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> UpdateShop(
        int id,
        [FromBody] UpdateShopRequest request,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
            return Unauthorized();

        var result = await mediator.Send(new UpdateShopCommand(id, request, userId.Value), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Close a shop, or reopen a closed one.
    /// </summary>
    /// <remarks>
    /// Separate from the edit above because closing is refused while operators are still assigned —
    /// a rule a checkbox on the edit form would let a save walk past.
    /// </remarks>
    [HttpPut("{id:int}/active")]
    public async Task<IActionResult> SetShopActive(
        int id,
        [FromQuery] bool isActive,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
            return Unauthorized();

        var result = await mediator.Send(new SetShopActiveCommand(id, isActive, userId.Value), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }
}
