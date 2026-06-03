using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Features.Statements.Queries.GetCustomerStatement;
using ShopInventory.Features.Statements.Queries.GenerateStatement;

namespace ShopInventory.Controllers;

[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
public class StatementController(IMediator mediator) : ApiControllerBase
{
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
