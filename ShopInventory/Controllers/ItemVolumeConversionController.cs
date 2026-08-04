using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Features.ItemVolumeConversions.Commands.DeleteItemVolumeConversion;
using ShopInventory.Features.ItemVolumeConversions.Commands.SaveItemVolumeConversion;
using ShopInventory.Features.ItemVolumeConversions.Queries.GetItemVolumeConversions;

namespace ShopInventory.Controllers;

/// <summary>
/// Maintains the litres-per-unit factor the item volume report converts quantities with.
/// </summary>
[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
public class ItemVolumeConversionController(IMediator mediator) : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetConversions(
        [FromQuery] string? search = null,
        [FromQuery] bool includeInactive = true,
        CancellationToken cancellationToken = default)
    {
        var result = await mediator.Send(
            new GetItemVolumeConversionsQuery(search, includeInactive),
            cancellationToken);

        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPut("{itemCode}")]
    public async Task<IActionResult> SaveConversion(
        string itemCode,
        [FromBody] SaveItemVolumeConversionRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new SaveItemVolumeConversionCommand(
                itemCode,
                request.ItemName,
                request.VolumeFactor,
                request.Notes,
                request.IsActive,
                request.UpdatedBy),
            cancellationToken);

        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpDelete("{itemCode}")]
    public async Task<IActionResult> DeleteConversion(string itemCode, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new DeleteItemVolumeConversionCommand(itemCode), cancellationToken);
        return result.Match(_ => NoContent(), errors => Problem(errors));
    }
}

/// <summary>The item code comes from the route, so it is not repeated in the body.</summary>
public sealed class SaveItemVolumeConversionRequest
{
    public string? ItemName { get; set; }
    public decimal VolumeFactor { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
    public string? UpdatedBy { get; set; }
}
