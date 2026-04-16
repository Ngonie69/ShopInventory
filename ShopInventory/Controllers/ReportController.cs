using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using ShopInventory.Features.Reports.Queries.GetCreditNoteSummary;
using ShopInventory.Features.Reports.Queries.GetLowStockAlerts;
using ShopInventory.Features.Reports.Queries.GetOrderFulfillment;
using ShopInventory.Features.Reports.Queries.GetPaymentSummary;
using ShopInventory.Features.Reports.Queries.GetProfitOverview;
using ShopInventory.Features.Reports.Queries.GetPurchaseOrderSummary;
using ShopInventory.Features.Reports.Queries.GetReceivablesAging;
using ShopInventory.Features.Reports.Queries.GetSalesSummary;
using ShopInventory.Features.Reports.Queries.GetSlowMovingProducts;
using ShopInventory.Features.Reports.Queries.GetStockMovement;
using ShopInventory.Features.Reports.Queries.GetStockSummary;
using ShopInventory.Features.Reports.Queries.GetTopCustomers;
using ShopInventory.Features.Reports.Queries.GetTopProducts;

namespace ShopInventory.Controllers;

[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
[OutputCache(PolicyName = "reports")]
public class ReportController(IMediator mediator) : ApiControllerBase
{
    [HttpGet("sales-summary")]
    public async Task<IActionResult> GetSalesSummary(
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetSalesSummaryQuery(fromDate, toDate), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("top-products")]
    public async Task<IActionResult> GetTopProducts(
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate,
        [FromQuery] int topCount = 10,
        [FromQuery] string? warehouseCode = null,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetTopProductsQuery(fromDate, toDate, topCount, warehouseCode), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("slow-moving-products")]
    public async Task<IActionResult> GetSlowMovingProducts(
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate,
        [FromQuery] int daysThreshold = 30,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetSlowMovingProductsQuery(fromDate, toDate, daysThreshold), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("stock-summary")]
    public async Task<IActionResult> GetStockSummary(
        [FromQuery] string? warehouseCode = null,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetStockSummaryQuery(warehouseCode), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("stock-movement")]
    public async Task<IActionResult> GetStockMovement(
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate,
        [FromQuery] string? warehouseCode = null,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetStockMovementQuery(fromDate, toDate, warehouseCode), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("low-stock-alerts")]
    public async Task<IActionResult> GetLowStockAlerts(
        [FromQuery] string? warehouseCode = null,
        [FromQuery] decimal? threshold = null,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetLowStockAlertsQuery(warehouseCode, threshold), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("payment-summary")]
    public async Task<IActionResult> GetPaymentSummary(
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetPaymentSummaryQuery(fromDate, toDate), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("top-customers")]
    public async Task<IActionResult> GetTopCustomers(
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate,
        [FromQuery] int topCount = 10,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetTopCustomersQuery(fromDate, toDate, topCount), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("order-fulfillment")]
    public async Task<IActionResult> GetOrderFulfillment(
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetOrderFulfillmentQuery(fromDate, toDate), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("credit-notes")]
    public async Task<IActionResult> GetCreditNoteSummary(
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetCreditNoteSummaryQuery(fromDate, toDate), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("purchase-orders")]
    public async Task<IActionResult> GetPurchaseOrderSummary(
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetPurchaseOrderSummaryQuery(fromDate, toDate), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("receivables-aging")]
    public async Task<IActionResult> GetReceivablesAging(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetReceivablesAgingQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("profit-overview")]
    public async Task<IActionResult> GetProfitOverview(
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetProfitOverviewQuery(fromDate, toDate), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }
}
