using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Features.Crates.Commands.UploadInvoiceCratePod;
using ShopInventory.Features.Invoices.Commands.CreateInvoice;
using ShopInventory.Features.Invoices.Commands.FiscalizeInvoice;
using ShopInventory.Features.Invoices.Commands.UploadPod;
using ShopInventory.Features.Invoices.Queries.DownloadInvoiceAttachment;
using ShopInventory.Features.Invoices.Queries.DownloadInvoicePdf;
using ShopInventory.Features.Invoices.Queries.GetAllPods;
using ShopInventory.Features.Invoices.Queries.GetAvailableBatches;
using ShopInventory.Features.Invoices.Queries.GetInvoiceAttachments;
using ShopInventory.Features.Invoices.Queries.GetInvoiceByDocEntry;
using ShopInventory.Features.Invoices.Queries.GetInvoiceByDocNum;
using ShopInventory.Features.Invoices.Queries.GetInvoicesByCustomer;
using ShopInventory.Features.Invoices.Queries.GetInvoicesByDateRange;
using ShopInventory.Features.Invoices.Queries.GetOpenInvoicesByCustomers;
using ShopInventory.Features.Invoices.Queries.GetPagedInvoices;
using ShopInventory.Features.Invoices.Queries.GetPodDashboard;
using ShopInventory.Features.Invoices.Queries.GetPodUploadStatus;
using ShopInventory.Features.Invoices.Queries.ValidateInvoice;
using ShopInventory.Features.Invoices.Queries.ValidateBulkPods;
using ShopInventory.Services;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

using ShopInventory.Middleware;

namespace ShopInventory.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "ApiAccessWithOperator")]
public class InvoiceController(ISender mediator) : ApiControllerBase
{
    [HttpPost]
    [Authorize(Roles = "Admin,Cashier")]
    [ProducesResponseType(typeof(InvoiceCreatedResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(BatchStockValidationResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> CreateInvoice(
        [FromBody] CreateInvoiceRequest request,
        [FromQuery] bool autoAllocateBatches = true,
        [FromQuery] BatchAllocationStrategy allocationStrategy = BatchAllocationStrategy.FEFO,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.ClientRequestId) && Request.Headers.TryGetValue("Idempotency-Key", out var idempotencyValues))
        {
            request.ClientRequestId = idempotencyValues.FirstOrDefault();
        }

        var result = await mediator.Send(
            new CreateInvoiceCommand(request, autoAllocateBatches, allocationStrategy, GetUserId(), GetUsername()), cancellationToken);

        return result.Match(
            invoice => CreatedAtAction(nameof(GetInvoiceByDocEntry), new { docEntry = invoice.Invoice?.DocEntry }, invoice),
            Problem);
    }

    [HttpGet("{itemCode}/batches/{warehouseCode}")]
    [Authorize(Roles = "Admin,Cashier,StockController,Manager")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAvailableBatches(
        string itemCode,
        string warehouseCode,
        [FromQuery] BatchAllocationStrategy strategy = BatchAllocationStrategy.FEFO,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(
            new GetAvailableBatchesQuery(itemCode, warehouseCode, strategy), cancellationToken);

        return result.Match(Ok, Problem);
    }

    [HttpPost("validate")]
    [Authorize(Roles = "Admin,Cashier")]
    [ProducesResponseType(typeof(BatchAllocationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(BatchStockValidationResponseDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ValidateInvoice(
        [FromBody] CreateInvoiceRequest request,
        [FromQuery] bool autoAllocateBatches = true,
        [FromQuery] BatchAllocationStrategy allocationStrategy = BatchAllocationStrategy.FEFO,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(
            new ValidateInvoiceQuery(request, autoAllocateBatches, allocationStrategy), cancellationToken);

        return result.Match(Ok, Problem);
    }

    [HttpGet("{docEntry:int}")]
    [Authorize(Roles = "Admin,Cashier,StockController,Manager")]
    [ProducesResponseType(typeof(InvoiceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInvoiceByDocEntry(
        int docEntry,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetInvoiceByDocEntryQuery(docEntry), cancellationToken);
        return result.Match(Ok, Problem);
    }

    [HttpGet("by-docnum/{docNum:int}")]
    [Authorize(Roles = "Admin,Cashier,StockController,Manager,Driver,PodOperator,Operator,ApiUser")]
    [ProducesResponseType(typeof(InvoiceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInvoiceByDocNum(
        int docNum,
        CancellationToken cancellationToken = default)
    {
        var restrictToAssignedCustomers = User.IsInRole("Driver") || User.IsInRole("Operator");
        var result = await mediator.Send(
            new GetInvoiceByDocNumQuery(
                docNum,
                restrictToAssignedCustomers ? GetUserId() : null,
                restrictToAssignedCustomers),
            cancellationToken);
        return result.Match(Ok, Problem);
    }

    [HttpPost("{docEntry:int}/fiscalize")]
    [Authorize(Roles = "Admin,Cashier")]
    [ProducesResponseType(typeof(FiscalizationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> FiscalizeInvoice(
        int docEntry,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(
            new FiscalizeInvoiceCommand(docEntry, GetUserId(), GetUsername()),
            cancellationToken);

        return result.Match(Ok, Problem);
    }

    [HttpPost("pods/validate-bulk")]
    // Validates a whole batch of documents against SAP in one call — bulk by definition.
    [SapBackgroundWork]
    [Authorize(Roles = "Admin,Cashier,PodOperator,Operator,Driver,SalesRep")]
    [ProducesResponseType(typeof(BulkPodValidationResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ValidateBulkPods(
        [FromBody] BulkPodValidationRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(
            new ValidateBulkPodsQuery(request.DocNums, request.SalesOrderDocNums),
            cancellationToken);
        return result.Match(Ok, Problem);
    }

    [HttpGet("{docEntry:int}/pdf")]
    [Authorize(Roles = "Admin,Cashier,StockController,Manager")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadInvoicePdf(
        int docEntry,
        [FromQuery] string? fiscalQrCode = null,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new DownloadInvoicePdfQuery(docEntry, fiscalQrCode), cancellationToken);
        return result.Match(
            pdf => File(pdf.PdfBytes, "application/pdf", pdf.FileName),
            Problem);
    }

    [HttpGet("customer/{cardCode}")]
    [Authorize(Roles = "Admin,Cashier,StockController,Manager,Driver,PodOperator")]
    [ProducesResponseType(typeof(InvoiceDateResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetInvoicesByCustomer(
        string cardCode,
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate,
        CancellationToken cancellationToken = default,
        [FromQuery] int? page = null,
        [FromQuery] int? pageSize = null)
    {
        var restrictToAssignedCustomers = User.IsInRole("Driver");
        var result = await mediator.Send(
            new GetInvoicesByCustomerQuery(
                cardCode,
                fromDate,
                toDate,
                page,
                pageSize,
                restrictToAssignedCustomers ? GetUserId() : null,
                restrictToAssignedCustomers),
            cancellationToken);

        return result.Match(Ok, Problem);
    }

    /// <summary>
    /// Open invoices for one or more accounts, filtered by SAP rather than by the caller.
    /// </summary>
    /// <remarks>
    /// The customer portal asks "what does this customer still owe" for every linked account at
    /// once. Answering it through <c>customer/{cardCode}</c> with no dates meant one unbounded walk
    /// of each account's entire invoice history per account; this is one bounded walk for the set.
    /// </remarks>
    [HttpGet("open")]
    [Authorize(Roles = "Admin,Cashier,StockController,Manager,PodOperator")]
    [ProducesResponseType(typeof(InvoiceDateResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetOpenInvoicesByCustomers(
        [FromQuery] List<string> cardCodes,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(
            new GetOpenInvoicesByCustomersQuery(cardCodes ?? []),
            cancellationToken);

        return result.Match(Ok, Problem);
    }

    [HttpGet("{docEntry:int}/attachments")]
    [Authorize(Roles = "Admin,Cashier,PodOperator,Operator,Driver,SalesRep")]
    [ProducesResponseType(typeof(DocumentAttachmentListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetInvoiceAttachments(
        int docEntry,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetInvoiceAttachmentsQuery(docEntry), cancellationToken);
        return result.Match(Ok, Problem);
    }

    [HttpGet("{docEntry:int}/attachments/{attachmentId:int}/download")]
    [Authorize(Roles = "Admin,Cashier,PodOperator,Operator,Driver,SalesRep")]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadInvoiceAttachment(
        int docEntry,
        int attachmentId,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(
            new DownloadInvoiceAttachmentQuery(docEntry, attachmentId), cancellationToken);

        return result.Match(
            file => File(file.Stream!, file.MimeType ?? "application/octet-stream", file.FileName ?? "attachment"),
            Problem);
    }

    [HttpPost("{docEntry:int}/pod")]
    [Authorize(Roles = "Admin,Cashier,PodOperator,Operator,Driver,SalesRep")]
    [ProducesResponseType(typeof(DocumentAttachmentDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    [MaxRequestBodySize(20 * 1024 * 1024)]
    public async Task<IActionResult> UploadPod(
        int docEntry,
        IFormFile file,
        [FromForm] string? description = null,
        [FromForm] string? uploadedByUsername = null,
        [FromForm] string? externalReference = null,
        CancellationToken cancellationToken = default)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new ErrorResponseDto { Message = "No file uploaded" });

        var allowedTypes = new[] { "image/jpeg", "image/png", "image/webp", "application/pdf" };
        if (!allowedTypes.Contains(file.ContentType, StringComparer.OrdinalIgnoreCase))
            return BadRequest(new ErrorResponseDto { Message = "Invalid file type. Only JPEG, PNG, WebP images and PDF files are allowed." });

        var effectiveUploadedByUsername = string.IsNullOrWhiteSpace(uploadedByUsername)
            ? User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name
            : uploadedByUsername.Trim();

        using var stream = file.OpenReadStream();
        var result = await mediator.Send(
            new UploadPodCommand(docEntry, stream, file.FileName, file.ContentType, description, effectiveUploadedByUsername, externalReference, GetUserId()),
            cancellationToken);

        return result.Match(
            attachment => CreatedAtAction(nameof(GetInvoiceAttachments), new { docEntry }, attachment),
            Problem);
    }

    [HttpPost("{docEntry:int}/crate-pod")]
    [Authorize(Roles = "Admin,Manager,Merchandiser,PodOperator,Operator,Driver")]
    [ProducesResponseType(typeof(CratePodSubmissionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    [MaxRequestBodySize(20 * 1024 * 1024)]
    public async Task<IActionResult> UploadInvoiceCratePod(
        int docEntry,
        [FromForm] int? invoiceDocNum,
        [FromForm] decimal quantity,
        [FromForm] string? submissionRole,
        [FromForm] string? notes,
        [FromForm] string? clientRequestId,
        IFormFile file,
        CancellationToken cancellationToken = default)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest(new ErrorResponseDto { Message = "A crate POD document is required." });
        }

        var allowedTypes = new[] { "image/jpeg", "image/png", "image/webp", "application/pdf" };
        if (!allowedTypes.Contains(file.ContentType, StringComparer.OrdinalIgnoreCase))
        {
            return BadRequest(new ErrorResponseDto { Message = "Invalid file type. Only JPEG, PNG, WebP images and PDF files are allowed." });
        }

        using var stream = file.OpenReadStream();
        var result = await mediator.Send(
            new UploadInvoiceCratePodCommand(
                docEntry,
                invoiceDocNum,
                submissionRole,
                quantity,
                notes,
                stream,
                file.FileName,
                file.ContentType,
                GetUserId(),
                ResolveClientRequestId(clientRequestId)),
            cancellationToken);

        return result.Match(Ok, Problem);
    }

    [HttpGet("pods")]
    [Authorize(Roles = "Admin,Cashier,PodOperator,Operator,Driver,SalesRep")]
    [ProducesResponseType(typeof(PodAttachmentListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllPods(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? cardCode = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        [FromQuery] string? search = null,
        [FromQuery] string? uploadedByUsername = null,
        [FromQuery] string? uploadedFromLocation = null,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId();

        // Service callers (the customer portal) authenticate with an API key and carry
        // no user identity, so they must name the accounts they are asking about.
        if (userId == null && string.IsNullOrWhiteSpace(cardCode))
            return Unauthorized();

        Guid? uploadedByUserId = User.IsInRole("Driver") ? userId : null;

        var result = await mediator.Send(
            new GetAllPodsQuery(page, pageSize, cardCode, fromDate, toDate, search, uploadedByUsername, uploadedFromLocation, uploadedByUserId, userId),
            cancellationToken);

        return result.Match(Ok, Problem);
    }

    [HttpGet("pod-upload-status")]
    [Authorize(Roles = "Admin,Cashier,PodOperator,Driver,SalesRep,ApiUser")]
    [ProducesResponseType(typeof(PodUploadStatusReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetPodUploadStatus(
        [FromQuery] DateTime fromDate,
        [FromQuery] DateTime toDate,
        [FromQuery] bool includeCreditNoteActivity = false,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId();
        if (userId == null && !User.IsInRole(ApplicationRoles.Admin) && !User.IsInRole(ApplicationRoles.ApiUser))
            return Unauthorized();

        var result = await mediator.Send(
            new GetPodUploadStatusQuery(fromDate, toDate, userId, includeCreditNoteActivity), cancellationToken);

        return result.Match(Ok, Problem);
    }

    [HttpGet("pod-dashboard")]
    [Authorize(Roles = "Admin,Cashier,PodOperator,Driver,SalesRep")]
    [ProducesResponseType(typeof(PodDashboardDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetPodDashboard(CancellationToken cancellationToken = default)
    {
        var userId = GetUserId();
        if (userId == null)
            return Unauthorized();

        var result = await mediator.Send(new GetPodDashboardQuery(userId.Value), cancellationToken);
        return result.Match(Ok, Problem);
    }

    [HttpGet("date-range")]
    [Authorize(Roles = "Admin,Cashier,StockController,Manager")]
    [ProducesResponseType(typeof(InvoiceDateResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetInvoicesByDateRange(
        [FromQuery] DateTime fromDate,
        [FromQuery] DateTime toDate,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(
            new GetInvoicesByDateRangeQuery(fromDate, toDate, page, pageSize), cancellationToken);

        return result.Match(Ok, Problem);
    }

    [HttpGet("paged")]
    [Authorize(Roles = "Admin,Cashier,StockController,Manager")]
    [ProducesResponseType(typeof(InvoiceListResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetPagedInvoices(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] int? docNum = null,
        [FromQuery] string? cardCode = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        [FromQuery] bool? vanSalesOnly = null,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(
            new GetPagedInvoicesQuery(page, pageSize, docNum, cardCode, fromDate, toDate, vanSalesOnly), cancellationToken);

        return result.Match(Ok, Problem);
    }

    /// <summary>
    /// Takes the crate POD idempotency key from the form field, falling back to the header.
    /// </summary>
    /// <remarks>
    /// Mirrors <see cref="CratesController"/>, because the two routes reach the same handler and it
    /// keys its durable replay on this value. The form field is the one to send: the header alone is
    /// what <see cref="Middleware.IdempotencyMiddleware"/> intercepts on, and for this route it
    /// defers to the handler rather than answering, so a header-carried key gains nothing the field
    /// does not already give and stays out of the middleware's per-instance cache.
    /// </remarks>
    private string? ResolveClientRequestId(string? clientRequestId)
    {
        if (!string.IsNullOrWhiteSpace(clientRequestId))
        {
            return clientRequestId.Trim();
        }

        return Request.Headers.TryGetValue("Idempotency-Key", out var headerValues)
            ? headerValues.FirstOrDefault()?.Trim()
            : null;
    }

    private Guid? GetUserId()
    {
        var candidateValues = User.FindAll(ClaimTypes.NameIdentifier)
            .Select(claim => claim.Value)
            .Concat(User.FindAll(JwtRegisteredClaimNames.Sub).Select(claim => claim.Value));

        foreach (var candidateValue in candidateValues)
        {
            if (Guid.TryParse(candidateValue, out var userId) && userId != Guid.Empty)
                return userId;
        }

        return null;
    }

    private string? GetUsername()
        => User.Identity?.Name ?? User.FindFirst(ClaimTypes.Name)?.Value;
}
