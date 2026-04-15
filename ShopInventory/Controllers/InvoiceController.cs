using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Features.Invoices.Commands.CreateInvoice;
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
using ShopInventory.Features.Invoices.Queries.GetPagedInvoices;
using ShopInventory.Features.Invoices.Queries.GetPodDashboard;
using ShopInventory.Features.Invoices.Queries.GetPodUploadStatus;
using ShopInventory.Features.Invoices.Queries.ValidateInvoice;

namespace ShopInventory.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
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
        var result = await mediator.Send(
            new CreateInvoiceCommand(request, autoAllocateBatches, allocationStrategy), cancellationToken);

        return result.Match(
            invoice => CreatedAtAction(nameof(GetInvoiceByDocEntry), new { docEntry = invoice.Invoice?.DocEntry }, invoice),
            Problem);
    }

    [HttpGet("{itemCode}/batches/{warehouseCode}")]
    [Authorize(Roles = "Admin,Cashier,StockController,DepotController,Manager")]
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
    [Authorize(Roles = "Admin,Cashier,StockController,DepotController,Manager")]
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
    [Authorize(Roles = "Admin,Cashier,StockController,DepotController,Manager")]
    [ProducesResponseType(typeof(InvoiceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInvoiceByDocNum(
        int docNum,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetInvoiceByDocNumQuery(docNum), cancellationToken);
        return result.Match(Ok, Problem);
    }

    [HttpGet("{docEntry:int}/pdf")]
    [Authorize(Roles = "Admin,Cashier,StockController,DepotController,Manager")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadInvoicePdf(
        int docEntry,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new DownloadInvoicePdfQuery(docEntry), cancellationToken);
        return result.Match(
            pdf => File(pdf.PdfBytes, "application/pdf", pdf.FileName),
            Problem);
    }

    [HttpGet("customer/{cardCode}")]
    [Authorize(Roles = "Admin,Cashier,StockController,DepotController,Manager")]
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
        var result = await mediator.Send(
            new GetInvoicesByCustomerQuery(cardCode, fromDate, toDate, page, pageSize), cancellationToken);

        return result.Match(Ok, Problem);
    }

    [HttpGet("{docEntry:int}/attachments")]
    [Authorize(Roles = "Admin,Cashier,PodOperator,Driver,SalesRep")]
    [ProducesResponseType(typeof(DocumentAttachmentListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetInvoiceAttachments(
        int docEntry,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetInvoiceAttachmentsQuery(docEntry), cancellationToken);
        return result.Match(Ok, Problem);
    }

    [HttpGet("{docEntry:int}/attachments/{attachmentId:int}/download")]
    [Authorize(Roles = "Admin,Cashier,PodOperator,Driver,SalesRep")]
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
            file => File(file.Stream!, file.FileName ?? "attachment", file.MimeType ?? "application/octet-stream"),
            Problem);
    }

    [HttpPost("{docEntry:int}/pod")]
    [Authorize(Roles = "Admin,Cashier,PodOperator,Driver,SalesRep")]
    [ProducesResponseType(typeof(DocumentAttachmentDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    [RequestSizeLimit(20 * 1024 * 1024)]
    public async Task<IActionResult> UploadPod(
        int docEntry,
        IFormFile file,
        [FromForm] string? description = null,
        [FromForm] string? uploadedByUsername = null,
        CancellationToken cancellationToken = default)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new ErrorResponseDto { Message = "No file uploaded" });

        var allowedTypes = new[] { "image/jpeg", "image/png", "image/webp", "application/pdf" };
        if (!allowedTypes.Contains(file.ContentType, StringComparer.OrdinalIgnoreCase))
            return BadRequest(new ErrorResponseDto { Message = "Invalid file type. Only JPEG, PNG, WebP images and PDF files are allowed." });

        using var stream = file.OpenReadStream();
        var result = await mediator.Send(
            new UploadPodCommand(docEntry, stream, file.FileName, file.ContentType, description, uploadedByUsername, GetUserId()),
            cancellationToken);

        return result.Match(
            attachment => CreatedAtAction(nameof(GetInvoiceAttachments), new { docEntry }, attachment),
            Problem);
    }

    [HttpGet("pods")]
    [Authorize(Roles = "Admin,Cashier,PodOperator,Driver,SalesRep")]
    [ProducesResponseType(typeof(PodAttachmentListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllPods(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? cardCode = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        [FromQuery] string? search = null,
        CancellationToken cancellationToken = default)
    {
        Guid? uploadedByUserId = User.IsInRole("Driver") ? GetUserId() : null;

        var result = await mediator.Send(
            new GetAllPodsQuery(page, pageSize, cardCode, fromDate, toDate, search, uploadedByUserId),
            cancellationToken);

        return result.Match(Ok, Problem);
    }

    [HttpGet("pod-upload-status")]
    [Authorize(Roles = "Admin,Cashier,PodOperator,Driver,SalesRep")]
    [ProducesResponseType(typeof(PodUploadStatusReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetPodUploadStatus(
        [FromQuery] DateTime fromDate,
        [FromQuery] DateTime toDate,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(
            new GetPodUploadStatusQuery(fromDate, toDate), cancellationToken);

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
    [Authorize(Roles = "Admin,Cashier,StockController,DepotController,Manager")]
    [ProducesResponseType(typeof(InvoiceDateResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetInvoicesByDateRange(
        [FromQuery] DateTime fromDate,
        [FromQuery] DateTime toDate,
        CancellationToken cancellationToken = default,
        [FromQuery] int? page = null,
        [FromQuery] int? pageSize = null)
    {
        var result = await mediator.Send(
            new GetInvoicesByDateRangeQuery(fromDate, toDate, page, pageSize), cancellationToken);

        return result.Match(Ok, Problem);
    }

    [HttpGet("paged")]
    [Authorize(Roles = "Admin,Cashier,StockController,DepotController,Manager")]
    [ProducesResponseType(typeof(InvoiceListResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetPagedInvoices(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] int? docNum = null,
        [FromQuery] string? cardCode = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(
            new GetPagedInvoicesQuery(page, pageSize, docNum, cardCode, fromDate, toDate), cancellationToken);

        return result.Match(Ok, Problem);
    }

    private Guid? GetUserId()
    {
        var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrEmpty(userIdClaim) && Guid.TryParse(userIdClaim, out var userId) && userId != Guid.Empty)
            return userId;
        return null;
    }
}
