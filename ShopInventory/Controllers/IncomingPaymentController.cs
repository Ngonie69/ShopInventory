using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.DTOs;
using ShopInventory.Services;
using ShopInventory.Features.IncomingPayments.Commands.CreateIncomingPayment;
using ShopInventory.Features.IncomingPayments.Commands.UploadPaymentAttachment;
using ShopInventory.Features.IncomingPayments.Queries.GetPagedPayments;
using ShopInventory.Features.IncomingPayments.Queries.GetPaymentByDocEntry;
using ShopInventory.Features.IncomingPayments.Queries.GetPaymentByDocNum;
using ShopInventory.Features.IncomingPayments.Queries.GetPaymentsByCustomer;
using ShopInventory.Features.IncomingPayments.Queries.GetPaymentsByDateRange;
using ShopInventory.Features.IncomingPayments.Queries.GetTodaysPayments;

namespace ShopInventory.Controllers;

[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
public class IncomingPaymentController(IMediator mediator) : ApiControllerBase
{
    /// <summary>
    /// Creates a new incoming payment in SAP Business One
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(IncomingPaymentCreatedResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> CreateIncomingPayment(
        [FromBody] CreateIncomingPaymentRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new CreateIncomingPaymentCommand(request), cancellationToken);
        return result.Match(
            value => CreatedAtAction(nameof(GetIncomingPaymentByDocEntry), new { docEntry = value.Payment!.DocEntry }, value),
            errors => Problem(errors));
    }

    /// <summary>
    /// Upload an attachment for an incoming payment
    /// </summary>
    [HttpPost("{docEntry:int}/attachment")]
    [ProducesResponseType(typeof(DocumentAttachmentDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    [RequestSizeLimit(20 * 1024 * 1024)]
    public async Task<IActionResult> UploadAttachment(
        int docEntry,
        IFormFile file,
        [FromForm] string? description = null,
        CancellationToken cancellationToken = default)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new ErrorResponseDto { Message = "No file uploaded" });

        var allowedTypes = new[] { "image/jpeg", "image/png", "image/webp", "application/pdf" };
        if (!allowedTypes.Contains(file.ContentType, StringComparer.OrdinalIgnoreCase))
            return BadRequest(new ErrorResponseDto { Message = "Invalid file type. Only JPEG, PNG, WebP images and PDF files are allowed." });

        var userId = Guid.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var uid) ? uid : Guid.Empty;
        using var stream = file.OpenReadStream();

        var result = await mediator.Send(
            new UploadPaymentAttachmentCommand(docEntry, stream, file.FileName, file.ContentType, description, userId),
            cancellationToken);

        return result.Match(
            value => Created($"api/incomingpayment/{docEntry}/attachment/{value.Id}", value),
            errors => Problem(errors));
    }

    /// <summary>
    /// Gets all incoming payments with pagination
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IncomingPaymentListResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetIncomingPayments(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetPagedPaymentsQuery(page, pageSize), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Gets a specific incoming payment by DocEntry
    /// </summary>
    [HttpGet("{docEntry:int}")]
    [ProducesResponseType(typeof(IncomingPaymentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetIncomingPaymentByDocEntry(
        int docEntry,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetPaymentByDocEntryQuery(docEntry), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Gets an incoming payment by DocNum
    /// </summary>
    [HttpGet("docnum/{docNum:int}")]
    [ProducesResponseType(typeof(IncomingPaymentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetIncomingPaymentByDocNum(
        int docNum,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetPaymentByDocNumQuery(docNum), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Gets incoming payments for a specific customer
    /// </summary>
    [HttpGet("customer/{cardCode}")]
    [ProducesResponseType(typeof(List<IncomingPaymentDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetIncomingPaymentsByCustomer(
        string cardCode,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetPaymentsByCustomerQuery(cardCode), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Gets incoming payments within a date range
    /// </summary>
    [HttpGet("daterange")]
    [ProducesResponseType(typeof(IncomingPaymentDateResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetIncomingPaymentsByDateRange(
        [FromQuery] DateTime fromDate,
        [FromQuery] DateTime toDate,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetPaymentsByDateRangeQuery(fromDate, toDate), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Gets today's incoming payments
    /// </summary>
    [HttpGet("today")]
    [ProducesResponseType(typeof(IncomingPaymentDateResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetTodaysIncomingPayments(CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetTodaysPaymentsQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }
}
    /// <param name="request">The payment creation request</param>
