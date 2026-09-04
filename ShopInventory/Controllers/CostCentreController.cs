using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Features.CostCentres.Queries.GetCostCentres;
using ShopInventory.Features.CostCentres.Queries.GetCostCentresByDimension;
using ShopInventory.Features.CostCentres.Queries.GetCostCentreByCode;

namespace ShopInventory.Controllers;

[Route("api/[controller]")]
[Authorize(Policy = "ApiAccess")]
public class CostCentreController(IMediator mediator) : ApiControllerBase
{
    /// <summary>
    /// Get all active cost centres from SAP
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetCostCentres(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetCostCentresQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Filter by dimension (1-5)
    /// </summary>
    [HttpGet("dimension/{dimension:int}")]
    public async Task<IActionResult> GetCostCentresByDimension(int dimension, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetCostCentresByDimensionQuery(dimension), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Get specific cost centre
    /// </summary>
    [HttpGet("{centerCode}")]
    public async Task<IActionResult> GetCostCentreByCode(string centerCode, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetCostCentreByCodeQuery(centerCode), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }
}
