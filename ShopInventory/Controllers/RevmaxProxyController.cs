using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Features.Revmax.Commands.SetLicense;
using ShopInventory.Features.Revmax.Commands.Transact;
using ShopInventory.Features.Revmax.Commands.TransactExt;
using ShopInventory.Features.Revmax.Queries.GetCardDetails;
using ShopInventory.Features.Revmax.Queries.GetDayStatus;
using ShopInventory.Features.Revmax.Queries.GetInvoice;
using ShopInventory.Features.Revmax.Queries.GetLicense;
using ShopInventory.Features.Revmax.Queries.GetUnprocessedInvoicesSummary;
using ShopInventory.Features.Revmax.Queries.GetZReport;
using ShopInventory.Models.Revmax;

namespace ShopInventory.Controllers;

[Route("api/revmax")]
[Authorize(Policy = "ApiAccess")]
public class RevmaxProxyController(IMediator mediator) : ApiControllerBase
{
    [HttpGet("card-details")]
    public async Task<IActionResult> GetCardDetails(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetCardDetailsQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("day-status")]
    public async Task<IActionResult> GetDayStatus(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetDayStatusQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("license")]
    public async Task<IActionResult> GetLicense(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetLicenseQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPost("license")]
    public async Task<IActionResult> SetLicense([FromBody] SetLicenseRequest request, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new SetLicenseCommand(request.License), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("z-report")]
    public async Task<IActionResult> GetZReport(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetZReportQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("invoices/{invoiceNumber}")]
    public async Task<IActionResult> GetInvoice([FromRoute] string invoiceNumber, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetInvoiceQuery(invoiceNumber), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("unprocessed-invoices/summary")]
    public async Task<IActionResult> GetUnprocessedInvoicesSummary(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetUnprocessedInvoicesSummaryQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPost("transact")]
    public async Task<IActionResult> Transact([FromBody] TransactMRequest request, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new TransactCommand(request), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPost("transact-ext")]
    public async Task<IActionResult> TransactExt([FromBody] TransactMExtRequest request, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new TransactExtCommand(request), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }
}
