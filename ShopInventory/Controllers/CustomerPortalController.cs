using MediatR;
using ShopInventory.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Features.CustomerPortal.Commands.GeneratePasswordHash;

namespace ShopInventory.Controllers;

/// <summary>
/// Development helper for customer portal passwords.
/// </summary>
/// <remarks>
/// Portal accounts themselves are not managed here. CustomerPortalUser belongs to the Web app's
/// database, which this API cannot reach, and the Web app creates and maintains them for real on its
/// Customer Portal Management page. This controller once carried register and bulk-register actions
/// that hashed a password, discarded it, created nothing and reported success anyway; they were
/// removed rather than implemented, because an account written to this database is one the portal's
/// own login (ShopInventory.Web CustomerAuthService) would never find.
///
/// What remains is the one action that does what it says: it returns a hash to paste into that other
/// database by hand, which is only useful precisely because the two are separate.
/// </remarks>
[Route("api/[controller]")]
[Authorize(Policy = "AdminOnly")]
public class CustomerPortalController(IMediator mediator) : ApiControllerBase
{
    /// <summary>
    /// Generate password hash for a customer (development only)
    /// </summary>
    [HttpPost("generate-hash")]
    [ProducesResponseType(typeof(PasswordHashResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GeneratePasswordHash([FromBody] GenerateHashRequest request, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GeneratePasswordHashCommand(request.Password), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }
}
