using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Features.ExchangeRates.Queries.GetAllActiveRates;
using ShopInventory.Features.ExchangeRates.Queries.GetCurrentRate;
using ShopInventory.Features.ExchangeRates.Queries.GetRateHistory;
using ShopInventory.Features.ExchangeRates.Queries.ConvertCurrency;
using ShopInventory.Features.ExchangeRates.Queries.GetMonitorStatus;
using ShopInventory.Features.ExchangeRates.Commands.RunMonitorCheck;

namespace ShopInventory.Controllers;

[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
public class ExchangeRateController(IMediator mediator) : ApiControllerBase
{
    /// <summary>
    /// Get all active exchange rates
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAllActive(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetAllActiveRatesQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Where the SAP rate monitor stands: today's rate, how far ahead rates are keyed, changes and alerts
    /// </summary>
    [HttpGet("monitor")]
    public async Task<IActionResult> GetMonitorStatus(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetMonitorStatusQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Run a rate monitor pass against SAP now
    /// </summary>
    /// <remarks>
    /// The roles that can open the Exchange Rates page. Not a manager's: a pass can raise alerts,
    /// which go to the bell and by email, and the manager role raises nothing.
    /// </remarks>
    [HttpPost("monitor/check")]
    [Authorize(Roles = "Admin,ApiUser,Cashier,StockController")]
    public async Task<IActionResult> RunMonitorCheck(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new RunMonitorCheckCommand(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Current rate between two currencies
    /// </summary>
    [HttpGet("{fromCurrency}/{toCurrency}")]
    public async Task<IActionResult> GetCurrentRate(string fromCurrency, string toCurrency, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetCurrentRateQuery(fromCurrency, toCurrency), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Rate history (default 30 days)
    /// </summary>
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

    /// <summary>
    /// Convert an amount
    /// </summary>
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
