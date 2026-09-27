using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Authentication;
using ShopInventory.DTOs;
using ShopInventory.Features.CountVariance.Queries.GetCountingDocuments;
using ShopInventory.Features.CountVariance.Queries.GetCountVariance;
using ShopInventory.Features.CountVariance.Queries.GetVanCountVariance;
using ShopInventory.Models;

namespace ShopInventory.Controllers;

/// <summary>
/// Count variance: what an SAP inventory count came up short or over, valued at selling price.
/// </summary>
[Route("api/count-variance")]
[Authorize(Policy = "ApiAccess")]
[Produces("application/json")]
public sealed class CountVarianceController(IMediator mediator) : ApiControllerBase
{
    /// <summary>
    /// SAP inventory counts, newest first, without their lines. <c>status</c> is open (the default),
    /// closed or all; <c>search</c> matches a document number or the count's remarks.
    /// </summary>
    [HttpGet("documents")]
    [RequirePermission(Permission.ViewStock)]
    [ProducesResponseType(typeof(CountingDocumentListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDocuments(
        [FromQuery] string? status = null,
        [FromQuery] string? search = null,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetCountingDocumentsQuery(status, search), cancellationToken);
        return result.Match(Ok, Problem);
    }

    /// <summary>
    /// Every van's latest count dated between <c>fromDate</c> and <c>toDate</c> (inclusive, at most
    /// 92 days), valued at the van sales price list excluding VAT: per van, per item, and in total.
    /// </summary>
    [HttpGet("vans")]
    [RequirePermission(Permission.ViewStock)]
    [ProducesResponseType(typeof(VanCountVarianceReportDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetVans(
        [FromQuery] DateTime fromDate,
        [FromQuery] DateTime toDate,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetVanCountVarianceQuery(fromDate, toDate), cancellationToken);
        return result.Match(Ok, Problem);
    }

    /// <summary>
    /// One count's variance, line by line, valued excluding VAT at the van sales price list.
    /// </summary>
    [HttpGet("{documentEntry:int}")]
    [RequirePermission(Permission.ViewStock)]
    [ProducesResponseType(typeof(CountVarianceReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetReport(int documentEntry, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetCountVarianceQuery(documentEntry), cancellationToken);
        return result.Match(Ok, Problem);
    }
}
