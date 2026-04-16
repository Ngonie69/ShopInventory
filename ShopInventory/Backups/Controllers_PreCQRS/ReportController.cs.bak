using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Controllers;

/// <summary>
/// Controller for business reports and analytics
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
[OutputCache(PolicyName = "reports")]
public class ReportController : ControllerBase
{
    private readonly IReportService _reportService;
    private readonly ILogger<ReportController> _logger;
    private static readonly TimeSpan ReportTimeout = TimeSpan.FromMinutes(5);

    public ReportController(IReportService reportService, ILogger<ReportController> logger)
    {
        _reportService = reportService;
        _logger = logger;
    }

    /// <summary>
    /// Creates a cancellation token that is NOT tied to the client disconnection.
    /// Reports are cached (900s), so we want them to complete even if the client
    /// disconnects — the next client request will get the cached result.
    /// Uses a 3-minute timeout to prevent SAP queries from running indefinitely.
    /// </summary>
    private static CancellationToken CreateReportTimeout()
    {
        return new CancellationTokenSource(ReportTimeout).Token;
    }

    /// <summary>
    /// Ensures a DateTime is in UTC format for PostgreSQL compatibility
    /// </summary>
    private static DateTime ToUtc(DateTime dateTime)
    {
        return dateTime.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
            : dateTime.ToUniversalTime();
    }

    /// <summary>
    /// Gets sales summary report for a date range
    /// </summary>
    /// <param name="fromDate">Start date (defaults to 30 days ago)</param>
    /// <param name="toDate">End date (defaults to today)</param>
    [HttpGet("sales-summary")]
    [ProducesResponseType(typeof(SalesSummaryReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetSalesSummary(
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate)
    {
        try
        {
            var from = ToUtc(fromDate ?? DateTime.UtcNow.AddDays(-30));
            var to = ToUtc(toDate ?? DateTime.UtcNow);

            var report = await _reportService.GetSalesSummaryAsync(from, to, CreateReportTimeout());
            return Ok(report);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Sales summary report timed out");
            return StatusCode(504, new ErrorResponseDto { Message = "Report timed out — SAP may be under heavy load. Try again." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating sales summary report");
            return StatusCode(500, new ErrorResponseDto { Message = $"Error generating report: {ex.Message}" });
        }
    }

    /// <summary>
    /// Gets top selling products report
    /// </summary>
    /// <param name="fromDate">Start date</param>
    /// <param name="toDate">End date</param>
    /// <param name="topCount">Number of top products to return (default 10)</param>
    /// <param name="warehouseCode">Optional warehouse filter</param>
    [HttpGet("top-products")]
    [ProducesResponseType(typeof(TopProductsReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetTopProducts(
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate,
        [FromQuery] int topCount = 10,
        [FromQuery] string? warehouseCode = null)
    {
        try
        {
            var from = ToUtc(fromDate ?? DateTime.UtcNow.AddDays(-30));
            var to = ToUtc(toDate ?? DateTime.UtcNow);

            var report = await _reportService.GetTopProductsAsync(from, to, topCount, warehouseCode, CreateReportTimeout());
            return Ok(report);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Top products report timed out");
            return StatusCode(504, new ErrorResponseDto { Message = "Report timed out — SAP may be under heavy load. Try again." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating top products report");
            return StatusCode(500, new ErrorResponseDto { Message = $"Error generating report: {ex.Message}" });
        }
    }

    /// <summary>
    /// Gets slow moving products report
    /// </summary>
    /// <param name="fromDate">Start date</param>
    /// <param name="toDate">End date</param>
    /// <param name="daysThreshold">Days without sale to be considered slow moving (default 30)</param>
    [HttpGet("slow-moving-products")]
    [ProducesResponseType(typeof(SlowMovingProductsReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetSlowMovingProducts(
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate,
        [FromQuery] int daysThreshold = 30)
    {
        try
        {
            var from = ToUtc(fromDate ?? DateTime.UtcNow.AddDays(-90));
            var to = ToUtc(toDate ?? DateTime.UtcNow);

            var report = await _reportService.GetSlowMovingProductsAsync(from, to, daysThreshold, CreateReportTimeout());
            return Ok(report);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Slow moving products report timed out");
            return StatusCode(504, new ErrorResponseDto { Message = "Report timed out — SAP may be under heavy load. Try again." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating slow moving products report");
            return StatusCode(500, new ErrorResponseDto { Message = $"Error generating report: {ex.Message}" });
        }
    }

    /// <summary>
    /// Gets stock summary report
    /// </summary>
    /// <param name="warehouseCode">Optional warehouse filter</param>
    [HttpGet("stock-summary")]
    [ProducesResponseType(typeof(StockSummaryReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetStockSummary(
        [FromQuery] string? warehouseCode = null)
    {
        try
        {
            var report = await _reportService.GetStockSummaryAsync(warehouseCode, CreateReportTimeout());
            return Ok(report);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Stock summary report timed out");
            return StatusCode(504, new ErrorResponseDto { Message = "Report timed out — SAP may be under heavy load. Try again." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating stock summary report");
            return StatusCode(500, new ErrorResponseDto { Message = $"Error generating report: {ex.Message}" });
        }
    }

    /// <summary>
    /// Gets stock movement report
    /// </summary>
    /// <param name="fromDate">Start date</param>
    /// <param name="toDate">End date</param>
    /// <param name="warehouseCode">Optional warehouse filter</param>
    [HttpGet("stock-movement")]
    [ProducesResponseType(typeof(StockMovementReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetStockMovement(
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate,
        [FromQuery] string? warehouseCode = null)
    {
        try
        {
            var from = ToUtc(fromDate ?? DateTime.UtcNow.AddDays(-30));
            var to = ToUtc(toDate ?? DateTime.UtcNow);

            var report = await _reportService.GetStockMovementAsync(from, to, warehouseCode, CreateReportTimeout());
            return Ok(report);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Stock movement report timed out");
            return StatusCode(504, new ErrorResponseDto { Message = "Report timed out — SAP may be under heavy load. Try again." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating stock movement report");
            return StatusCode(500, new ErrorResponseDto { Message = $"Error generating report: {ex.Message}" });
        }
    }

    /// <summary>
    /// Gets low stock alerts
    /// </summary>
    /// <param name="warehouseCode">Optional warehouse filter</param>
    /// <param name="threshold">Reorder level threshold (default 10)</param>
    [HttpGet("low-stock-alerts")]
    [ProducesResponseType(typeof(LowStockAlertReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetLowStockAlerts(
        [FromQuery] string? warehouseCode = null,
        [FromQuery] decimal? threshold = null)
    {
        try
        {
            var report = await _reportService.GetLowStockAlertsAsync(warehouseCode, threshold, CreateReportTimeout());
            return Ok(report);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Low stock alerts report timed out");
            return StatusCode(504, new ErrorResponseDto { Message = "Report timed out — SAP may be under heavy load. Try again." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating low stock alerts");
            return StatusCode(500, new ErrorResponseDto { Message = $"Error generating report: {ex.Message}" });
        }
    }

    /// <summary>
    /// Gets payment summary report
    /// </summary>
    /// <param name="fromDate">Start date</param>
    /// <param name="toDate">End date</param>
    [HttpGet("payment-summary")]
    [ProducesResponseType(typeof(PaymentSummaryReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetPaymentSummary(
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate)
    {
        try
        {
            var from = ToUtc(fromDate ?? DateTime.UtcNow.AddDays(-30));
            var to = ToUtc(toDate ?? DateTime.UtcNow);

            var report = await _reportService.GetPaymentSummaryAsync(from, to, CreateReportTimeout());
            return Ok(report);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Payment summary report timed out");
            return StatusCode(504, new ErrorResponseDto { Message = "Report timed out — SAP may be under heavy load. Try again." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating payment summary report");
            return StatusCode(500, new ErrorResponseDto { Message = $"Error generating report: {ex.Message}" });
        }
    }

    /// <summary>
    /// Gets top customers report
    /// </summary>
    /// <param name="fromDate">Start date</param>
    /// <param name="toDate">End date</param>
    /// <param name="topCount">Number of top customers to return (default 10)</param>
    [HttpGet("top-customers")]
    [ProducesResponseType(typeof(TopCustomersReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetTopCustomers(
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate,
        [FromQuery] int topCount = 10)
    {
        try
        {
            var from = ToUtc(fromDate ?? DateTime.UtcNow.AddDays(-30));
            var to = ToUtc(toDate ?? DateTime.UtcNow);

            var report = await _reportService.GetTopCustomersAsync(from, to, topCount, CreateReportTimeout());
            return Ok(report);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Top customers report timed out");
            return StatusCode(504, new ErrorResponseDto { Message = "Report timed out — SAP may be under heavy load. Try again." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating top customers report");
            return StatusCode(500, new ErrorResponseDto { Message = $"Error generating report: {ex.Message}" });
        }
    }

    /// <summary>
    /// Gets comprehensive order fulfillment report
    /// </summary>
    /// <param name="fromDate">Start date</param>
    /// <param name="toDate">End date</param>
    [HttpGet("order-fulfillment")]
    [ProducesResponseType(typeof(OrderFulfillmentReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetOrderFulfillment(
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate)
    {
        try
        {
            var from = ToUtc(fromDate ?? DateTime.UtcNow.AddDays(-30));
            var to = ToUtc(toDate ?? DateTime.UtcNow);

            var report = await _reportService.GetOrderFulfillmentAsync(from, to, CreateReportTimeout());
            return Ok(report);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Order fulfillment report timed out");
            return StatusCode(504, new ErrorResponseDto { Message = "Report timed out — SAP may be under heavy load. Try again." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating order fulfillment report");
            return StatusCode(500, new ErrorResponseDto { Message = $"Error generating report: {ex.Message}" });
        }
    }

    /// <summary>
    /// Gets credit notes summary report
    /// </summary>
    [HttpGet("credit-notes")]
    [ProducesResponseType(typeof(CreditNoteSummaryReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetCreditNoteSummary(
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate)
    {
        try
        {
            var from = ToUtc(fromDate ?? DateTime.UtcNow.AddDays(-30));
            var to = ToUtc(toDate ?? DateTime.UtcNow);

            var report = await _reportService.GetCreditNoteSummaryAsync(from, to, CreateReportTimeout());
            return Ok(report);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Credit notes report timed out");
            return StatusCode(504, new ErrorResponseDto { Message = "Report timed out — SAP may be under heavy load. Try again." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating credit notes report");
            return StatusCode(500, new ErrorResponseDto { Message = $"Error generating report: {ex.Message}" });
        }
    }

    /// <summary>
    /// Gets purchase orders summary report
    /// </summary>
    [HttpGet("purchase-orders")]
    [ProducesResponseType(typeof(PurchaseOrderSummaryReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetPurchaseOrderSummary(
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate)
    {
        try
        {
            var from = ToUtc(fromDate ?? DateTime.UtcNow.AddDays(-30));
            var to = ToUtc(toDate ?? DateTime.UtcNow);

            var report = await _reportService.GetPurchaseOrderSummaryAsync(from, to, CreateReportTimeout());
            return Ok(report);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Purchase orders report timed out");
            return StatusCode(504, new ErrorResponseDto { Message = "Report timed out — SAP may be under heavy load. Try again." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating purchase orders report");
            return StatusCode(500, new ErrorResponseDto { Message = $"Error generating report: {ex.Message}" });
        }
    }

    /// <summary>
    /// Gets receivables aging report
    /// </summary>
    [HttpGet("receivables-aging")]
    [ProducesResponseType(typeof(ReceivablesAgingReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetReceivablesAging()
    {
        try
        {
            var report = await _reportService.GetReceivablesAgingAsync(CreateReportTimeout());
            return Ok(report);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Receivables aging report timed out");
            return StatusCode(504, new ErrorResponseDto { Message = "Report timed out — SAP may be under heavy load. Try again." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating receivables aging report");
            return StatusCode(500, new ErrorResponseDto { Message = $"Error generating report: {ex.Message}" });
        }
    }

    /// <summary>
    /// Gets profit overview report
    /// </summary>
    [HttpGet("profit-overview")]
    [ProducesResponseType(typeof(ProfitOverviewReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetProfitOverview(
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate)
    {
        try
        {
            var from = ToUtc(fromDate ?? DateTime.UtcNow.AddDays(-30));
            var to = ToUtc(toDate ?? DateTime.UtcNow);

            var report = await _reportService.GetProfitOverviewAsync(from, to, CreateReportTimeout());
            return Ok(report);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Profit overview report timed out");
            return StatusCode(504, new ErrorResponseDto { Message = "Report timed out — SAP may be under heavy load. Try again." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating profit overview report");
            return StatusCode(500, new ErrorResponseDto { Message = $"Error generating report: {ex.Message}" });
        }
    }

}
