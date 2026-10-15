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
    /// <summary>
    /// Find batches by number
    /// </summary>
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

    /// <summary>
    /// Change a batch's status
    /// </summary>
    /// <remarks>
    /// The lab's call: releasing or locking a batch decides whether it can be sold, so it takes the
    /// roles /lab/batch-status admits (Admin, Lab) and the API user key. Under "ApiAccess" alone any
    /// staff login could release a batch the lab had locked.
    /// </remarks>
    [HttpPatch("{batchEntryId:int}/status")]
    [Authorize(Roles = "Admin,ApiUser,Lab")]
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