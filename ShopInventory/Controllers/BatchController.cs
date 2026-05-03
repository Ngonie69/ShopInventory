using ErrorOr;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.DTOs;
using ShopInventory.Features.Batches.Commands.UpdateBatchStatus;
using ShopInventory.Features.Batches.Queries.SearchBatches;

namespace ShopInventory.Controllers;

[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
public class BatchController(IMediator mediator) : ApiControllerBase
{
    [HttpGet("search")]
    [ProducesResponseType(typeof(BatchSearchResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Search(
        [FromQuery] string term,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new SearchBatchesQuery(term), cancellationToken);

        return result.Match(
            value => Ok(value),
            errors => Problem(errors));
    }

    [HttpPatch("{batchEntryId:int}/status")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> UpdateStatus(
        int batchEntryId,
        [FromBody] UpdateBatchStatusRequestDto request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new UpdateBatchStatusCommand(batchEntryId, request.Status),
            cancellationToken);

        return result.Match<IActionResult>(
            _ => NoContent(),
            errors => Problem(errors));
    }
}