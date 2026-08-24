using System.Security.Claims;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using ShopInventory.DTOs;
using ShopInventory.Features.VanSalesOrders.Commands.RegisterVanSalesCustomerDevice;
using ShopInventory.Features.VanSalesOrders.Queries.GetVanSalesCustomerCatalogue;
using ShopInventory.Features.VanSalesOrders.Queries.GetVanSalesCustomerProfile;
using ShopInventory.Models;

namespace ShopInventory.Controllers;

/// <summary>
/// The van sales customer's own view of themselves in the ordering app.
/// </summary>
/// <remarks>
/// Every action resolves the caller from the token. There is no route parameter naming a customer,
/// deliberately: an id in the URL is an id a caller can change, and this data — a shop's address,
/// its delivery days, what it buys — is exactly what a competitor would want.
/// </remarks>
[Route("api/van-sales-customer")]
[Authorize(Policy = "VanSalesCustomerAccess")]
public class VanSalesCustomerController(ISender mediator) : ApiControllerBase
{
    /// <summary>The signed-in shop, its route, and the delivery window it is ordering into.</summary>
    [HttpGet("profile")]
    [ProducesResponseType(typeof(VanSalesCustomerProfileResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetProfile(CancellationToken cancellationToken)
    {
        var accountId = GetAuthenticatedCustomerAccountId();
        if (accountId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(
            new GetVanSalesCustomerProfileQuery(accountId.Value),
            cancellationToken);

        return result.Match(Ok, Problem);
    }

    /// <summary>
    /// What this shop can order, priced, with a stock indication per item.
    /// </summary>
    /// <remarks>
    /// Honours <c>If-None-Match</c>. The handset caches the catalogue and sends back the tag it
    /// holds; an unchanged catalogue costs a 304 and no body, which on a rural connection paid for
    /// by the shopkeeper is the difference between the app opening and the app hanging.
    /// </remarks>
    [HttpGet("catalogue")]
    [ProducesResponseType(typeof(VanSalesCatalogueResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    public async Task<IActionResult> GetCatalogue(CancellationToken cancellationToken)
    {
        var accountId = GetAuthenticatedCustomerAccountId();
        if (accountId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(
            new GetVanSalesCustomerCatalogueQuery(accountId.Value),
            cancellationToken);

        return result.Match(
            catalogue =>
            {
                var etag = $"\"{catalogue.ETag}\"";

                // Compared against every tag offered, not just the first: a client may legitimately
                // hold several, and "*" means "anything you have".
                var offered = Request.Headers[HeaderNames.IfNoneMatch];
                if (offered.Count > 0
                    && offered.Any(tag => tag is not null
                                          && (tag == "*"
                                              || tag.Split(',')
                                                    .Select(t => t.Trim())
                                                    .Contains(etag, StringComparer.Ordinal))))
                {
                    Response.Headers.ETag = etag;
                    return (IActionResult)StatusCode(StatusCodes.Status304NotModified);
                }

                Response.Headers.ETag = etag;
                return Ok(catalogue);
            },
            Problem);
    }

    /// <summary>
    /// Register this handset for order notifications.
    /// </summary>
    /// <remarks>
    /// Idempotent on the token. The app calls it on every sign-in and whenever Firebase rotates the
    /// token, so creating a row per call would give a shopkeeper who reinstalls twice three copies
    /// of every notification.
    /// </remarks>
    [HttpPost("devices")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RegisterDevice(
        [FromBody] RegisterVanSalesCustomerDeviceRequest request,
        CancellationToken cancellationToken)
    {
        var accountId = GetAuthenticatedCustomerAccountId();
        if (accountId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(
            new RegisterVanSalesCustomerDeviceCommand(
                accountId.Value,
                request.DeviceToken,
                request.DeviceId,
                request.DeviceName,
                request.AppVersion),
            cancellationToken);

        return result.Match(_ => NoContent(), Problem);
    }

    /// <summary>
    /// The calling customer, taken from the token and from nowhere else.
    /// </summary>
    private int? GetAuthenticatedCustomerAccountId()
    {
        var claim = User.FindFirstValue(VanSalesCustomerClaims.AccountId);
        return int.TryParse(claim, out var accountId) ? accountId : null;
    }
}
