using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Services;

namespace ShopInventory.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
public class IncomingPaymentController : ControllerBase
{
    private readonly ISAPServiceLayerClient _sapClient;
    private readonly SAPSettings _settings;
    private readonly ILogger<IncomingPaymentController> _logger;

    public IncomingPaymentController(
        ISAPServiceLayerClient sapClient,
        IOptions<SAPSettings> settings,
        ILogger<IncomingPaymentController> logger)
    {
        _sapClient = sapClient;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Creates a new incoming payment in SAP Business One
    /// </summary>
    /// <param name="request">The payment creation request</param>
    /// <returns>The created payment</returns>
    [HttpPost]
    [ProducesResponseType(typeof(IncomingPaymentCreatedResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> CreateIncomingPayment(
        [FromBody] CreateIncomingPaymentRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });
            }

            // Validate the model
            if (!ModelState.IsValid)
            {
                return BadRequest(new ErrorResponseDto
                {
                    Message = "Validation failed",
                    Errors = ModelState.Values
                        .SelectMany(v => v.Errors)
                        .Select(e => e.ErrorMessage)
                        .ToList()
                });
            }

            // CRITICAL: Additional validation for negative amounts
            var amountErrors = ValidatePaymentAmounts(request);
            if (amountErrors.Count > 0)
            {
                _logger.LogWarning("Payment amount validation failed: {Errors}", string.Join(", ", amountErrors));
                return BadRequest(new ErrorResponseDto
                {
                    Message = "Amount validation failed - negative amounts are not allowed",
                    Errors = amountErrors
                });
            }

            var payment = await _sapClient.CreateIncomingPaymentAsync(request, cancellationToken);

            _logger.LogInformation("Incoming payment created successfully. DocEntry: {DocEntry}, DocNum: {DocNum}, Customer: {CardCode}, Total: {Total}",
                payment.DocEntry, payment.DocNum, payment.CardCode, payment.DocTotal);

            return CreatedAtAction(
                nameof(GetIncomingPaymentByDocEntry),
                new { docEntry = payment.DocEntry },
                new IncomingPaymentCreatedResponseDto
                {
                    Message = "Incoming payment created successfully",
                    Payment = payment.ToDto()
                });
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Validation error creating incoming payment");
            return BadRequest(new ErrorResponseDto { Message = "Validation error", Errors = ex.Message.Split("; ").ToList() });
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError(ex, "Timeout connecting to SAP Service Layer");
            return StatusCode(504, new ErrorResponseDto { Message = "Connection to SAP Service Layer timed out" });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return StatusCode(502, new ErrorResponseDto { Message = "Unable to connect to SAP Service Layer", Errors = new List<string> { ex.Message } });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating incoming payment");
            return StatusCode(500, new ErrorResponseDto { Message = "Error creating incoming payment", Errors = new List<string> { ex.Message } });
        }
    }

    /// <summary>
    /// Validates that all payment amounts are non-negative
    /// </summary>
    private List<string> ValidatePaymentAmounts(CreateIncomingPaymentRequest request)
    {
        var errors = new List<string>();

        if (request.CashSum < 0)
        {
            errors.Add($"Cash sum cannot be negative. Current value: {request.CashSum}");
        }

        if (request.TransferSum < 0)
        {
            errors.Add($"Transfer sum cannot be negative. Current value: {request.TransferSum}");
        }

        if (request.CheckSum < 0)
        {
            errors.Add($"Check sum cannot be negative. Current value: {request.CheckSum}");
        }

        if (request.CreditSum < 0)
        {
            errors.Add($"Credit sum cannot be negative. Current value: {request.CreditSum}");
        }

        // Validate total payment is positive
        var totalPayment = request.CashSum + request.TransferSum + request.CheckSum + request.CreditSum;
        if (totalPayment <= 0)
        {
            errors.Add("At least one payment amount must be greater than zero");
        }

        // Validate payment invoices
        if (request.PaymentInvoices != null)
        {
            for (int i = 0; i < request.PaymentInvoices.Count; i++)
            {
                var inv = request.PaymentInvoices[i];
                if (inv.SumApplied < 0)
                {
                    errors.Add($"Invoice {i + 1}: Sum applied cannot be negative. Current value: {inv.SumApplied}");
                }
            }
        }

        // Validate payment checks
        if (request.PaymentChecks != null)
        {
            for (int i = 0; i < request.PaymentChecks.Count; i++)
            {
                var chk = request.PaymentChecks[i];
                if (chk.CheckSum < 0)
                {
                    errors.Add($"Check {i + 1}: Check sum cannot be negative. Current value: {chk.CheckSum}");
                }
            }
        }

        // Validate payment credit cards
        if (request.PaymentCreditCards != null)
        {
            for (int i = 0; i < request.PaymentCreditCards.Count; i++)
            {
                var cc = request.PaymentCreditCards[i];
                if (cc.CreditSum < 0)
                {
                    errors.Add($"Credit card {i + 1}: Credit sum cannot be negative. Current value: {cc.CreditSum}");
                }
            }
        }

        return errors;
    }

    /// <summary>
    /// Gets all incoming payments with pagination
    /// </summary>
    /// <param name="page">Page number (default: 1)</param>
    /// <param name="pageSize">Number of items per page (default: 20)</param>
    /// <returns>Paginated list of incoming payments</returns>
    [HttpGet]
    [ProducesResponseType(typeof(IncomingPaymentListResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetIncomingPayments(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });
            }

            if (page < 1) page = 1;
            if (pageSize < 1) pageSize = 20;
            if (pageSize > 100) pageSize = 100;

            var payments = await _sapClient.GetPagedIncomingPaymentsAsync(page, pageSize, cancellationToken);

            _logger.LogInformation("Retrieved {Count} incoming payments (page {Page})", payments.Count, page);

            return Ok(new IncomingPaymentListResponseDto
            {
                Page = page,
                PageSize = pageSize,
                Count = payments.Count,
                HasMore = payments.Count == pageSize,
                Payments = payments.ToDto()
            });
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError(ex, "Timeout connecting to SAP Service Layer");
            return StatusCode(504, new ErrorResponseDto { Message = "Connection to SAP Service Layer timed out" });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return StatusCode(502, new ErrorResponseDto { Message = "Unable to connect to SAP Service Layer", Errors = new List<string> { ex.Message } });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving incoming payments");
            return StatusCode(500, new ErrorResponseDto { Message = "An error occurred while retrieving incoming payments", Errors = new List<string> { ex.Message } });
        }
    }

    /// <summary>
    /// Gets a specific incoming payment by DocEntry
    /// </summary>
    /// <param name="docEntry">The document entry number</param>
    /// <returns>The incoming payment details</returns>
    [HttpGet("{docEntry:int}")]
    [ProducesResponseType(typeof(IncomingPaymentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetIncomingPaymentByDocEntry(
        int docEntry,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });
            }

            var payment = await _sapClient.GetIncomingPaymentByDocEntryAsync(docEntry, cancellationToken);

            if (payment == null)
            {
                return NotFound(new ErrorResponseDto { Message = $"Incoming payment with DocEntry {docEntry} not found" });
            }

            _logger.LogInformation("Retrieved incoming payment DocEntry: {DocEntry}, DocNum: {DocNum}", payment.DocEntry, payment.DocNum);

            return Ok(payment.ToDto());
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError(ex, "Timeout connecting to SAP Service Layer");
            return StatusCode(504, new ErrorResponseDto { Message = "Connection to SAP Service Layer timed out" });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return StatusCode(502, new ErrorResponseDto { Message = "Unable to connect to SAP Service Layer", Errors = new List<string> { ex.Message } });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving incoming payment {DocEntry}", docEntry);
            return StatusCode(500, new ErrorResponseDto { Message = "An error occurred while retrieving the incoming payment", Errors = new List<string> { ex.Message } });
        }
    }

    /// <summary>
    /// Gets an incoming payment by DocNum
    /// </summary>
    /// <param name="docNum">The document number</param>
    /// <returns>The incoming payment details</returns>
    [HttpGet("docnum/{docNum:int}")]
    [ProducesResponseType(typeof(IncomingPaymentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetIncomingPaymentByDocNum(
        int docNum,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });
            }

            var payment = await _sapClient.GetIncomingPaymentByDocNumAsync(docNum, cancellationToken);

            if (payment == null)
            {
                return NotFound(new ErrorResponseDto { Message = $"Incoming payment with DocNum {docNum} not found" });
            }

            _logger.LogInformation("Retrieved incoming payment by DocNum: {DocNum}, DocEntry: {DocEntry}", payment.DocNum, payment.DocEntry);

            return Ok(payment.ToDto());
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError(ex, "Timeout connecting to SAP Service Layer");
            return StatusCode(504, new ErrorResponseDto { Message = "Connection to SAP Service Layer timed out" });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return StatusCode(502, new ErrorResponseDto { Message = "Unable to connect to SAP Service Layer", Errors = new List<string> { ex.Message } });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving incoming payment by DocNum {DocNum}", docNum);
            return StatusCode(500, new ErrorResponseDto { Message = "An error occurred while retrieving the incoming payment", Errors = new List<string> { ex.Message } });
        }
    }

    /// <summary>
    /// Gets incoming payments for a specific customer
    /// </summary>
    /// <param name="cardCode">The customer card code</param>
    /// <returns>List of incoming payments for the customer</returns>
    [HttpGet("customer/{cardCode}")]
    [ProducesResponseType(typeof(List<IncomingPaymentDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetIncomingPaymentsByCustomer(
        string cardCode,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });
            }

            if (string.IsNullOrWhiteSpace(cardCode))
            {
                return BadRequest(new ErrorResponseDto { Message = "Customer code is required" });
            }

            var payments = await _sapClient.GetIncomingPaymentsByCustomerAsync(cardCode, cancellationToken);

            _logger.LogInformation("Retrieved {Count} incoming payments for customer {CardCode}", payments.Count, cardCode);

            return Ok(new
            {
                CardCode = cardCode,
                Count = payments.Count,
                Payments = payments.ToDto()
            });
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError(ex, "Timeout connecting to SAP Service Layer");
            return StatusCode(504, new ErrorResponseDto { Message = "Connection to SAP Service Layer timed out" });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return StatusCode(502, new ErrorResponseDto { Message = "Unable to connect to SAP Service Layer", Errors = new List<string> { ex.Message } });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving incoming payments for customer {CardCode}", cardCode);
            return StatusCode(500, new ErrorResponseDto { Message = "An error occurred while retrieving incoming payments", Errors = new List<string> { ex.Message } });
        }
    }

    /// <summary>
    /// Gets incoming payments within a date range
    /// </summary>
    /// <param name="fromDate">Start date (yyyy-MM-dd)</param>
    /// <param name="toDate">End date (yyyy-MM-dd)</param>
    /// <returns>List of incoming payments within the date range</returns>
    [HttpGet("daterange")]
    [ProducesResponseType(typeof(IncomingPaymentDateResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetIncomingPaymentsByDateRange(
        [FromQuery] DateTime fromDate,
        [FromQuery] DateTime toDate,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });
            }

            if (fromDate > toDate)
            {
                return BadRequest(new ErrorResponseDto { Message = "From date must be less than or equal to To date" });
            }

            var payments = await _sapClient.GetIncomingPaymentsByDateRangeAsync(fromDate, toDate, cancellationToken);

            _logger.LogInformation("Retrieved {Count} incoming payments from {FromDate} to {ToDate}",
                payments.Count, fromDate.ToString("yyyy-MM-dd"), toDate.ToString("yyyy-MM-dd"));

            return Ok(new IncomingPaymentDateResponseDto
            {
                FromDate = fromDate.ToString("yyyy-MM-dd"),
                ToDate = toDate.ToString("yyyy-MM-dd"),
                Count = payments.Count,
                Payments = payments.ToDto()
            });
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError(ex, "Timeout connecting to SAP Service Layer");
            return StatusCode(504, new ErrorResponseDto { Message = "Connection to SAP Service Layer timed out" });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return StatusCode(502, new ErrorResponseDto { Message = "Unable to connect to SAP Service Layer", Errors = new List<string> { ex.Message } });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving incoming payments by date range");
            return StatusCode(500, new ErrorResponseDto { Message = "An error occurred while retrieving incoming payments", Errors = new List<string> { ex.Message } });
        }
    }

    /// <summary>
    /// Gets today's incoming payments
    /// </summary>
    /// <returns>List of today's incoming payments</returns>
    [HttpGet("today")]
    [ProducesResponseType(typeof(IncomingPaymentDateResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetTodaysIncomingPayments(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_settings.Enabled)
            {
                return StatusCode(503, new ErrorResponseDto { Message = "SAP integration is disabled" });
            }

            var today = DateTime.Today;
            var payments = await _sapClient.GetIncomingPaymentsByDateRangeAsync(today, today, cancellationToken);

            _logger.LogInformation("Retrieved {Count} incoming payments for today ({Date})", payments.Count, today.ToString("yyyy-MM-dd"));

            return Ok(new IncomingPaymentDateResponseDto
            {
                Date = today.ToString("yyyy-MM-dd"),
                Count = payments.Count,
                Payments = payments.ToDto()
            });
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError(ex, "Timeout connecting to SAP Service Layer");
            return StatusCode(504, new ErrorResponseDto { Message = "Connection to SAP Service Layer timed out" });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return StatusCode(502, new ErrorResponseDto { Message = "Unable to connect to SAP Service Layer", Errors = new List<string> { ex.Message } });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving today's incoming payments");
            return StatusCode(500, new ErrorResponseDto { Message = "An error occurred while retrieving incoming payments", Errors = new List<string> { ex.Message } });
        }
    }
}
