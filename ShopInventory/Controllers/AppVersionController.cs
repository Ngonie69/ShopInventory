using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.DTOs;
using ShopInventory.Features.AppVersion.Commands.UpdateMobileVersionPolicySettings;
using ShopInventory.Features.AppVersion.Queries.GetMobileVersionPolicy;
using ShopInventory.Features.AppVersion.Queries.GetMobileVersionPolicySettings;

namespace ShopInventory.Controllers;

[Route("api/[controller]")]
public class AppVersionController(IMediator mediator) : ApiControllerBase
{
    [HttpGet("mobile")]
    [AllowAnonymous]
    public async Task<IActionResult> GetMobileVersionPolicy(
        [FromHeader(Name = "X-App-Platform")] string? platformHeader,
        [FromHeader(Name = "X-App-Version")] string? versionHeader,
        [FromQuery] string? platform,
        [FromQuery(Name = "currentVersion")] string? currentVersion,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new GetMobileVersionPolicyQuery(platform ?? platformHeader, currentVersion ?? versionHeader),
            cancellationToken);

        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpGet("mobile/settings")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> GetMobileVersionPolicySettings(CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetMobileVersionPolicySettingsQuery(), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPut("mobile/settings")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> UpdateMobileVersionPolicySettings(
        [FromBody] UpdateMobileVersionPolicySettingsRequest request,
        CancellationToken cancellationToken)
    {
        var userName = User.Identity?.Name ?? "Unknown";
        var result = await mediator.Send(new UpdateMobileVersionPolicySettingsCommand(request, userName), cancellationToken);
        return result.Match(value => Ok(new { message = value.Message }), errors => Problem(errors));
    }
}