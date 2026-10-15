using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Authentication;
using ShopInventory.Features.Statements.Queries.GetCustomerStatement;
using ShopInventory.Features.Statements.Queries.GenerateStatement;
using ShopInventory.Models;

namespace ShopInventory.Controllers;

/// <remarks>
/// reports.view: every customer's balance and ledger, on /customers (Admin, Cashier, StockController,
/// Manager). Under "ApiAccess" alone any staff login could read any customer's statement. The customer
/// portal and the statement email job call through the Web's key alone, a service call that
/// RequirePermission admits.
/// </remarks>
[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
[RequirePermission(Permission.ViewReports)]
public class StatementController(IMediator mediator) : ApiControllerBase
{
    /// <summary>
    /// Get customer statement
    /// </summary>
    [HttpGet("{cardCode}")]
    public async Task<IActionResult> GetStatement(
        string cardCode,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        [FromQuery] List<string>? cardCodes = null,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GetCustomerStatementQuery(cardCode, fromDate, toDate, cardCodes), cancellationToken);
        return result.Match(Ok, Problem);
    }

    /// <summary>
    /// Generate a customer statement as a PDF
    /// </summary>
    [HttpGet("generate/{cardCode}")]
    [HttpGet("{cardCode}/pdf")]
    public async Task<IActionResult> GenerateStatement(
        string cardCode,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        [FromQuery] List<string>? cardCodes = null,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(new GenerateStatementQuery(cardCode, fromDate, toDate, cardCodes), cancellationToken);
        return result.Match(
            value => File(value.PdfBytes, "application/pdf", value.FileName),
            errors => Problem(errors));
    }
}
