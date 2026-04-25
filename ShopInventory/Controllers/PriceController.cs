using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using ShopInventory.DTOs;
using ShopInventory.Features.Prices.Queries.GetCachedPrices;
using ShopInventory.Features.Prices.Queries.GetAllPrices;
using ShopInventory.Features.Prices.Queries.GetGroupedPrices;
using ShopInventory.Features.Prices.Queries.GetPriceByItemCode;
using ShopInventory.Features.Prices.Queries.GetPricesByCurrency;
using ShopInventory.Features.Prices.Queries.GetPriceLists;
using ShopInventory.Features.Prices.Queries.GetPricesByPriceList;
using ShopInventory.Features.Prices.Queries.GetItemPriceFromList;
using ShopInventory.Features.Prices.Queries.GetPricesByBusinessPartner;
using ShopInventory.Features.Prices.Commands.SyncPriceLists;
using ShopInventory.Features.Prices.Commands.SyncItemPricesForPriceList;

namespace ShopInventory.Controllers;

[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
public class PriceController(IMediator mediator) : ApiControllerBase
{
    [HttpGet("cached")]
    public async Task<IActionResult> GetCachedPrices(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetCachedPricesQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet]
    public async Task<IActionResult> GetAllPrices(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetAllPricesQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("grouped")]
    public async Task<IActionResult> GetGroupedPrices(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetGroupedPricesQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("{itemCode}")]
    public async Task<IActionResult> GetPriceByItemCode(string itemCode, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetPriceByItemCodeQuery(itemCode), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("currency/{currency}")]
    public async Task<IActionResult> GetPricesByCurrency(string currency, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetPricesByCurrencyQuery(currency), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("pricelists")]
    [OutputCache(PolicyName = "master-data")]
    public async Task<IActionResult> GetPriceLists([FromQuery] bool? forceRefresh = null, CancellationToken cancellationToken = default)
    {
        if (forceRefresh == true)
        {
            return BadRequest(new
            {
                Message = "Use POST api/price/pricelists/sync to refresh price lists. Normal GET responses are output cached."
            });
        }

        var result = await mediator.Send(new GetPriceListsQuery(false), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPost("pricelists/sync")]
    public async Task<IActionResult> SyncPriceLists(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new SyncPriceListsCommand(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("pricelists/{priceListNum:int}/items")]
    public async Task<IActionResult> GetPricesByPriceList(
        int priceListNum,
        [FromQuery] bool? forceRefresh = null,
        CancellationToken cancellationToken = default)
    {
        if (forceRefresh == true)
        {
            return BadRequest(new
            {
                Message = $"Use POST api/price/pricelists/{priceListNum}/sync to refresh item prices. Normal GET responses use the cached sync path."
            });
        }

        var result = await mediator.Send(new GetPricesByPriceListQuery(priceListNum, false), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPost("pricelists/{priceListNum:int}/sync")]
    public async Task<IActionResult> SyncItemPricesForPriceList(int priceListNum, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new SyncItemPricesForPriceListCommand(priceListNum), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("pricelists/{priceListNum:int}/items/{itemCode}")]
    public async Task<IActionResult> GetItemPriceFromList(int priceListNum, string itemCode, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetItemPriceFromListQuery(priceListNum, itemCode), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("businesspartner/{cardCode}")]
    public async Task<IActionResult> GetPricesByBusinessPartner(
        string cardCode,
        [FromQuery] bool? forceRefresh = null,
        CancellationToken cancellationToken = default)
    {
        if (forceRefresh == true)
        {
            return BadRequest(new
            {
                Message = "Force refresh is not supported on this GET endpoint. Refresh the underlying price list through the explicit sync endpoint."
            });
        }

        var result = await mediator.Send(new GetPricesByBusinessPartnerQuery(cardCode, false), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }
}
