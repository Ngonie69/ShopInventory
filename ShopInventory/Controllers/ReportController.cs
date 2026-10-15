using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Middleware;
using ShopInventory.Common.Security;
using ShopInventory.Features.ManagementSales.Commands.SendSalesPerformanceEmail;
using ShopInventory.Features.ManagementSales.Commands.UpdateSalesPerformanceEmailSchedule;
using ShopInventory.Features.ManagementSales.Queries.GetSalesPerformance;
using ShopInventory.Features.ManagementSales.Queries.GetSalesPerformanceEmailSchedule;
using ShopInventory.Features.Reports.Queries.GetCreditNoteSummary;
using ShopInventory.Features.Reports.Queries.GetItemVolumeSalesReport;
using ShopInventory.Features.Reports.Queries.GetLowStockAlerts;
using ShopInventory.Features.Reports.Queries.GetMerchandiserPurchaseOrderReport;
using ShopInventory.Features.Reports.Queries.GetOrderFulfillment;
using ShopInventory.Features.Reports.Queries.GetPaymentSummary;
using ShopInventory.Features.Reports.Queries.GetProfitOverview;
using ShopInventory.Features.Reports.Queries.GetPurchaseOrderSummary;
using ShopInventory.Features.Reports.Queries.GetReceivablesAging;
using ShopInventory.Features.Reports.Queries.GetNegativeStockTrend;
using ShopInventory.Features.Reports.Queries.GetSalesSummary;
using ShopInventory.Features.Reports.Queries.GetSlowMovingProducts;
using ShopInventory.Features.Reports.Queries.GetStockMovement;
using ShopInventory.Features.Reports.Queries.GetStockSummary;
using ShopInventory.Features.Reports.Queries.GetTopCustomers;
using ShopInventory.Features.Reports.Queries.GetTopProducts;

namespace ShopInventory.Controllers;

[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
// Reports scan months of SAP documents. Somebody is waiting on them, but not the way a rep waits
// on an approval, and letting one hold the interactive reservation would queue that approval
// behind it — the exact failure the reservation exists to prevent.
[SapBackgroundWork]
public class ReportController(IMediator mediator) : ApiControllerBase
{
    /// <summary>
    /// How much stock SAP has been holding below zero, day by day.
    /// </summary>
    /// <remarks>
    /// The outcome measure for the negative-stock work: every guard elsewhere stops a document that
    /// would take stock under, and none of them can say whether they worked. Read it repeatedly —
    /// one day's figure says nothing, a fortnight of them says everything.
    /// </remarks>
    [HttpGet("negative-stock-trend")]
    public async Task<IActionResult> GetNegativeStockTrend(
        [FromQuery] int days = 30,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetNegativeStockTrendQuery(days), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Sales summary for a date range
    /// </summary>
    [HttpGet("sales-summary")]
    public async Task<IActionResult> GetSalesSummary(
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetSalesSummaryQuery(fromDate, toDate), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Top selling products
    /// </summary>
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

    /// <summary>
    /// Products with no movement
    /// </summary>
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

    /// <summary>
    /// Stock on hand and its value, optionally for one warehouse
    /// </summary>
    [HttpGet("stock-summary")]
    public async Task<IActionResult> GetStockSummary(
        [FromQuery] string? warehouseCode = null,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetStockSummaryQuery(warehouseCode), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Stock movement over a date range
    /// </summary>
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

    /// <summary>
    /// Low stock alert items
    /// </summary>
    [HttpGet("low-stock-alerts")]
    public async Task<IActionResult> GetLowStockAlerts(
        [FromQuery] string? warehouseCode = null,
        [FromQuery] decimal? threshold = null,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetLowStockAlertsQuery(warehouseCode, threshold), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Payments over a date range
    /// </summary>
    [HttpGet("payment-summary")]
    public async Task<IActionResult> GetPaymentSummary(
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetPaymentSummaryQuery(fromDate, toDate), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Invoiced-less-credited quantity, converted volume, and net revenue for chosen business
    /// partners and items. Backs both the item volume report and the customer revenue report.
    /// </summary>
    [HttpGet("item-volume-sales")]
    public async Task<IActionResult> GetItemVolumeSales(
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate,
        [FromQuery] ItemVolumeSalesGrouping grouping = ItemVolumeSalesGrouping.Monthly,
        [FromQuery] List<string>? accountCodes = null,
        [FromQuery] List<string>? itemCodes = null,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(
            new GetItemVolumeSalesReportQuery(
                fromDate,
                toDate,
                grouping,
                accountCodes ?? new List<string>(),
                itemCodes ?? new List<string>()),
            cancellationToken);

        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Top customers
    /// </summary>
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

    /// <summary>
    /// Order fulfilment
    /// </summary>
    [HttpGet("order-fulfillment")]
    public async Task<IActionResult> GetOrderFulfillment(
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate,
        [FromQuery] string? cardCode = null,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetOrderFulfillmentQuery(fromDate, toDate, cardCode), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Credit notes over a date range
    /// </summary>
    [HttpGet("credit-notes")]
    public async Task<IActionResult> GetCreditNoteSummary(
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetCreditNoteSummaryQuery(fromDate, toDate), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Purchase orders over a date range
    /// </summary>
    [HttpGet("purchase-orders")]
    public async Task<IActionResult> GetPurchaseOrderSummary(
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetPurchaseOrderSummaryQuery(fromDate, toDate), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Merchandiser-raised purchase orders
    /// </summary>
    [HttpGet("merchandiser-purchase-orders")]
    [Authorize(Roles = "Admin,Manager,MerchandiserPurchaseOrderViewer")]
    public async Task<IActionResult> GetMerchandiserPurchaseOrderReport(
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate,
        [FromQuery] Guid? merchandiserUserId,
        [FromQuery] bool? hasAttachments,
        [FromQuery] string? search,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(
            new GetMerchandiserPurchaseOrderReportQuery(
                fromDate,
                toDate,
                merchandiserUserId,
                hasAttachments,
                search),
            cancellationToken);

        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Customer aging analysis
    /// </summary>
    [HttpGet("receivables-aging")]
    public async Task<IActionResult> GetReceivablesAging(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetReceivablesAgingQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Profit overview
    /// </summary>
    [HttpGet("profit-overview")]
    public async Task<IActionResult> GetProfitOverview(
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetProfitOverviewQuery(fromDate, toDate), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Shops, vending and vans over one period, each against the same number of days just before —
    /// the figures the management sales performance email carries.
    /// </summary>
    [Authorize(Roles = "Admin,Manager")]
    [HttpGet("sales-performance")]
    public async Task<IActionResult> GetSalesPerformance(
        [FromQuery] DateTime fromDate,
        [FromQuery] DateTime toDate,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
            return Unauthorized();

        var result = await mediator.Send(new GetSalesPerformanceQuery(userId.Value, fromDate, toDate), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// When the management sales performance email goes out, to whom, and which admin it is read as.
    /// </summary>
    [Authorize(Roles = "Admin")]
    [HttpGet("sales-performance/schedule")]
    public async Task<IActionResult> GetSalesPerformanceEmailSchedule(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetSalesPerformanceEmailScheduleQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Switches the daily, weekly and monthly management emails on or off and sets who receives them.
    /// The admin saving becomes the account the scheduled email is read as.
    /// </summary>
    [Authorize(Roles = "Admin")]
    [HttpPut("sales-performance/schedule")]
    public async Task<IActionResult> UpdateSalesPerformanceEmailSchedule(
        [FromBody] UpdateSalesPerformanceEmailScheduleRequest request,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
            return Unauthorized();

        var result = await mediator.Send(
            new UpdateSalesPerformanceEmailScheduleCommand(
                userId.Value, User.Identity?.Name, request.DailyEnabled, request.WeeklyEnabled, request.MonthlyEnabled, request.Recipients ?? []),
            cancellationToken);

        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Emails the management sales performance now, read under the sender's own scope — yesterday, the
    /// last complete week or month, or a custom period — to the people named, or to the schedule's list.
    /// </summary>
    [Authorize(Roles = "Admin,Manager")]
    [HttpPost("sales-performance/email")]
    public async Task<IActionResult> SendSalesPerformanceEmail(
        [FromBody] SendSalesPerformanceEmailRequest request,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
            return Unauthorized();

        var result = await mediator.Send(
            new SendSalesPerformanceEmailCommand(
                request.Cadence?.Trim().ToLowerInvariant() ?? string.Empty,
                Scheduled: false,
                userId.Value,
                User.Identity?.Name,
                request.FromDate,
                request.ToDate,
                request.Recipients),
            cancellationToken);

        return result.Match(value => Ok(value), errors => Problem(errors));
    }
}
