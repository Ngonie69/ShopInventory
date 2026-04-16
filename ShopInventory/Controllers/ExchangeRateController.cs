using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Features.ExchangeRates.Queries.GetAllActiveRates;
using ShopInventory.Features.ExchangeRates.Queries.GetCurrentRate;
using ShopInventory.Features.ExchangeRates.Queries.GetRateHistory;
using ShopInventory.Features.ExchangeRates.Queries.ConvertCurrency;

namespace ShopInventory.Controllers;

[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
public class ExchangeRateController(IMediator mediator) : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAllActive(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetAllActiveRatesQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("{fromCurrency}/{toCurrency}")]
    public async Task<IActionResult> GetCurrentRate(string fromCurrency, string toCurrency, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetCurrentRateQuery(fromCurrency, toCurrency), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("{fromCurrency}/{toCurrency}/history")]
    public async Task<IActionResult> GetRateHistory(
        string fromCurrency,
        string toCurrency,
        [FromQuery] int days = 30,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetRateHistoryQuery(fromCurrency, toCurrency, days), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("convert")]
    public async Task<IActionResult> Convert(
        [FromQuery] decimal amount,
        [FromQuery] string from,
        [FromQuery] string to,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new ConvertCurrencyQuery(amount, from, to), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }
}
