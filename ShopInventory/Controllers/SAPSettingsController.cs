using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.DTOs;
using ShopInventory.Features.SapConfiguration.Queries.GetSAPSettings;
using ShopInventory.Features.SapConfiguration.Commands.UpdateSAPSettings;
using ShopInventory.Features.SapConfiguration.Commands.TestSAPConnection;

namespace ShopInventory.Controllers;

[Route("api/sap-settings")]
[Authorize(Policy = "AdminOnly")]
public class SAPSettingsController(IMediator mediator) : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetSettings(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetSAPSettingsQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPut]
    public async Task<IActionResult> UpdateSettings([FromBody] UpdateSAPSettingsRequest request, CancellationToken cancellationToken)
    {
        var userName = User.Identity?.Name ?? "Unknown";
        var result = await mediator.Send(new UpdateSAPSettingsCommand(request, userName), cancellationToken);
        return result.Match(value => Ok(new { message = value.Message, connectionTestPassed = value.ConnectionTestPassed }), errors => Problem(errors));
    }

    [HttpPost("test-connection")]
    public async Task<IActionResult> TestConnection([FromBody] TestSAPConnectionRequest? request, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new TestSAPConnectionCommand(request), cancellationToken);
        return result.Match(value => Ok(new { connected = value.Connected, message = value.Message }), errors => Problem(errors));
    }
}
