using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.DTOs;
using ShopInventory.Features.Crates.Commands.CreateCrateGrv;
using ShopInventory.Features.Crates.Commands.CreateCrateOpeningBalance;
using ShopInventory.Features.Crates.Commands.DeleteCratePod;
using ShopInventory.Features.Crates.Commands.DeleteCrateOpeningBalance;
using ShopInventory.Features.Crates.Commands.EnsureInvoiceCrateTransaction;
using ShopInventory.Features.Crates.Commands.UpdateCrateOpeningBalance;
using ShopInventory.Features.Crates.Commands.UploadCratePod;
using ShopInventory.Features.Crates.Queries.GetCrateGrvs;
using ShopInventory.Features.Crates.Queries.GetCratePods;
using ShopInventory.Features.Crates.Queries.GetCrateTransactions;
using ShopInventory.Features.Crates.Queries.ValidateBulkCratePods;
using System.Security.Claims;
using ShopInventory.Middleware;

namespace ShopInventory.Controllers;

[Route("api/crates")]
[Authorize(Policy = "ApiAccessWithOperator")]
public class CratesController(ISender mediator) : ApiControllerBase
{
    private static readonly HashSet<string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/webp",
        "application/pdf"
    };

    [HttpGet("transactions")]
    [Authorize(Roles = "Admin,Manager,Merchandiser,PodOperator,Operator,Driver,SalesRep")]
    [ProducesResponseType(typeof(List<CrateTransactionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTransactions(
        [FromQuery] string? search = null,
        [FromQuery] string? status = null,
        [FromQuery] string? transactionType = null,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(
            new GetCrateTransactionsQuery(search, status, transactionType, userId.Value),
            cancellationToken);

        return result.Match(Ok, Problem);
    }

    [HttpGet("pods")]
    [Authorize(Roles = "Admin,Manager,Merchandiser,PodOperator,Operator,Driver,SalesRep")]
    [ProducesResponseType(typeof(List<CratePodSubmissionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPods(
        [FromQuery] string? search = null,
        [FromQuery] string? submissionRole = null,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(
            new GetCratePodsQuery(search, submissionRole, userId.Value),
            cancellationToken);

        return result.Match(Ok, Problem);
    }

    [HttpPost("pods/validate-bulk")]
    [Authorize(Roles = "Admin,Manager,Merchandiser,PodOperator,Operator,Driver")]
    [ProducesResponseType(typeof(BulkCratePodValidationResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ValidateBulkPods(
        [FromBody] BulkCratePodValidationRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(
            new ValidateBulkCratePodsQuery(request.InvoiceDocNums, request.SubmissionRole, userId.Value),
            cancellationToken);

        return result.Match(Ok, Problem);
    }

    [HttpPost("transactions/ensure-invoice")]
    [Authorize(Roles = "Admin,Manager,Merchandiser,Driver")]
    [ProducesResponseType(typeof(EnsureInvoiceCrateTransactionResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponseDto), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> EnsureInvoiceTransaction(
        [FromBody] EnsureInvoiceCrateTransactionRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(
            new EnsureInvoiceCrateTransactionCommand(request.InvoiceDocNum, request.ExpectedQuantity, userId.Value),
            cancellationToken);

        return result.Match(Ok, Problem);
    }

    [HttpGet("grvs")]
    [Authorize(Roles = "Admin,Manager,Merchandiser,Driver,SalesRep")]
    [ProducesResponseType(typeof(List<CrateGrvDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetGrvs(
        [FromQuery] string? search = null,
        [FromQuery] string? status = null,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(
            new GetCrateGrvsQuery(search, status, userId.Value),
            cancellationToken);

        return result.Match(Ok, Problem);
    }

    [HttpPost("opening-balances")]
    [Authorize(Roles = "Admin")]
    [MaxRequestBodySize(20 * 1024 * 1024)]
    [ProducesResponseType(typeof(CrateTransactionDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> CreateOpeningBalance(
        [FromForm] string shopCardCode,
        [FromForm] decimal quantity,
        [FromForm] DateTime effectiveDate,
        [FromForm] string? notes,
        IFormFile? file,
        CancellationToken cancellationToken = default)
    {
        if (file is { Length: > 0 } && !AllowedTypes.Contains(file.ContentType))
        {
            return BadRequest(new ErrorResponseDto { Message = "Invalid file type. Only JPEG, PNG, WebP images and PDF files are allowed." });
        }

        using var stream = file is { Length: > 0 } ? file.OpenReadStream() : null;
        var result = await mediator.Send(
            new CreateCrateOpeningBalanceCommand(
                shopCardCode,
                quantity,
                effectiveDate,
                notes,
                stream,
                file?.FileName,
                file?.ContentType,
                GetUserId()),
            cancellationToken);

        return result.Match(Ok, Problem);
    }

    [HttpPut("opening-balances/{crateTransactionId:int}")]
    [Authorize(Roles = "Admin")]
    [MaxRequestBodySize(20 * 1024 * 1024)]
    [ProducesResponseType(typeof(CrateTransactionDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateOpeningBalance(
        int crateTransactionId,
        [FromForm] string shopCardCode,
        [FromForm] decimal quantity,
        [FromForm] DateTime effectiveDate,
        [FromForm] string? notes,
        IFormFile? file,
        CancellationToken cancellationToken = default)
    {
        if (file is { Length: > 0 } && !AllowedTypes.Contains(file.ContentType))
        {
            return BadRequest(new ErrorResponseDto { Message = "Invalid file type. Only JPEG, PNG, WebP images and PDF files are allowed." });
        }

        using var stream = file is { Length: > 0 } ? file.OpenReadStream() : null;
        var result = await mediator.Send(
            new UpdateCrateOpeningBalanceCommand(
                crateTransactionId,
                shopCardCode,
                quantity,
                effectiveDate,
                notes,
                stream,
                file?.FileName,
                file?.ContentType,
                GetUserId()),
            cancellationToken);

        return result.Match(Ok, Problem);
    }

    [HttpDelete("opening-balances/{crateTransactionId:int}")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeleteOpeningBalance(
        int crateTransactionId,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(
            new DeleteCrateOpeningBalanceCommand(crateTransactionId, GetUserId()),
            cancellationToken);

        return result.Match(_ => NoContent(), Problem);
    }

    [HttpPost("transactions/{crateTransactionId:int}/pods")]
    [Authorize(Roles = "Admin,Manager,Merchandiser,PodOperator,Operator,Driver")]
    [MaxRequestBodySize(20 * 1024 * 1024)]
    [ProducesResponseType(typeof(CratePodSubmissionDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> UploadCratePod(
        int crateTransactionId,
        [FromForm] decimal quantity,
        [FromForm] string? submissionRole,
        [FromForm] string? notes,
        IFormFile file,
        CancellationToken cancellationToken = default)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new ErrorResponseDto { Message = "A crate POD document is required." });
        }

        if (!AllowedTypes.Contains(file.ContentType))
        {
            return BadRequest(new ErrorResponseDto { Message = "Invalid file type. Only JPEG, PNG, WebP images and PDF files are allowed." });
        }

        using var stream = file.OpenReadStream();
        var result = await mediator.Send(
            new UploadCratePodCommand(
                crateTransactionId,
                submissionRole,
                quantity,
                notes,
                stream,
                file.FileName,
                file.ContentType,
                GetUserId()),
            cancellationToken);

        return result.Match(Ok, Problem);
    }

    [HttpDelete("pods/{cratePodSubmissionId:int}")]
    [Authorize(Roles = "Admin,Manager,Merchandiser,Operator,Driver")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeletePod(
        int cratePodSubmissionId,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(
            new DeleteCratePodCommand(cratePodSubmissionId, GetUserId()),
            cancellationToken);

        return result.Match(_ => NoContent(), Problem);
    }

    [HttpPost("transactions/{crateTransactionId:int}/grvs")]
    [Authorize(Roles = "Admin,Manager,Merchandiser")]
    [MaxRequestBodySize(20 * 1024 * 1024)]
    [ProducesResponseType(typeof(CrateGrvDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> CreateGrv(
        int crateTransactionId,
        [FromForm] string reason,
        IFormFile file,
        CancellationToken cancellationToken = default)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new ErrorResponseDto { Message = "A GRV document is required." });
        }

        if (!AllowedTypes.Contains(file.ContentType))
        {
            return BadRequest(new ErrorResponseDto { Message = "Invalid file type. Only JPEG, PNG, WebP images and PDF files are allowed." });
        }

        using var stream = file.OpenReadStream();
        var result = await mediator.Send(
            new CreateCrateGrvCommand(
                crateTransactionId,
                reason,
                stream,
                file.FileName,
                file.ContentType,
                GetUserId()),
            cancellationToken);

        return result.Match(Ok, Problem);
    }

    private Guid? GetUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrWhiteSpace(userIdClaim) && Guid.TryParse(userIdClaim, out var userId) && userId != Guid.Empty)
        {
            return userId;
        }

        return null;
    }
}
