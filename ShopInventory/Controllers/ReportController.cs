using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Controllers;

/// <summary>
/// Controller for business reports and analytics
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
public class ReportController : ControllerBase
{
    private readonly IReportService _reportService;
    private readonly ILogger<ReportController> _logger;

    public ReportController(IReportService reportService, ILogger<ReportController> logger)
    {
        _reportService = reportService;
        _logger = logger;
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
        [FromQuery] DateTime? toDate,
        CancellationToken cancellationToken)
    {
        try
        {
            var from = ToUtc(fromDate ?? DateTime.UtcNow.AddDays(-30));
            var to = ToUtc(toDate ?? DateTime.UtcNow);

            var report = await _reportService.GetSalesSummaryAsync(from, to, cancellationToken);
            return Ok(report);
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
        [FromQuery] string? warehouseCode = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var from = ToUtc(fromDate ?? DateTime.UtcNow.AddDays(-30));
            var to = ToUtc(toDate ?? DateTime.UtcNow);

            var report = await _reportService.GetTopProductsAsync(from, to, topCount, warehouseCode, cancellationToken);
            return Ok(report);
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
        [FromQuery] int daysThreshold = 30,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var from = ToUtc(fromDate ?? DateTime.UtcNow.AddDays(-90));
            var to = ToUtc(toDate ?? DateTime.UtcNow);

            var report = await _reportService.GetSlowMovingProductsAsync(from, to, daysThreshold, cancellationToken);
            return Ok(report);
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
        [FromQuery] string? warehouseCode = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var report = await _reportService.GetStockSummaryAsync(warehouseCode, cancellationToken);
            return Ok(report);
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
        [FromQuery] string? warehouseCode = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var from = ToUtc(fromDate ?? DateTime.UtcNow.AddDays(-30));
            var to = ToUtc(toDate ?? DateTime.UtcNow);

            var report = await _reportService.GetStockMovementAsync(from, to, warehouseCode, cancellationToken);
            return Ok(report);
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
        [FromQuery] decimal? threshold = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var report = await _reportService.GetLowStockAlertsAsync(warehouseCode, threshold, cancellationToken);
            return Ok(report);
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
        [FromQuery] DateTime? toDate,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var from = ToUtc(fromDate ?? DateTime.UtcNow.AddDays(-30));
            var to = ToUtc(toDate ?? DateTime.UtcNow);

            var report = await _reportService.GetPaymentSummaryAsync(from, to, cancellationToken);
            return Ok(report);
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
        [FromQuery] int topCount = 10,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var from = ToUtc(fromDate ?? DateTime.UtcNow.AddDays(-30));
            var to = ToUtc(toDate ?? DateTime.UtcNow);

            var report = await _reportService.GetTopCustomersAsync(from, to, topCount, cancellationToken);
            return Ok(report);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating top customers report");
            return StatusCode(500, new ErrorResponseDto { Message = $"Error generating report: {ex.Message}" });
        }
    }
}
