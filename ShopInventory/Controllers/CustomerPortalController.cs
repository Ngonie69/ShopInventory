using MediatR;
using ShopInventory.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Features.CustomerPortal.Commands.BulkRegisterCustomers;
using ShopInventory.Features.CustomerPortal.Commands.GeneratePasswordHash;
using ShopInventory.Features.CustomerPortal.Commands.RegisterCustomer;

namespace ShopInventory.Controllers;

/// <summary>
/// Admin endpoints for managing customer portal users
/// </summary>
[Route("api/[controller]")]
[Authorize(Policy = "AdminOnly")]
public class CustomerPortalController(IMediator mediator) : ApiControllerBase
{
    /// <summary>
    /// Register a new customer portal user
    /// </summary>
    [HttpPost("register")]
    [ProducesResponseType(typeof(CustomerRegistrationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RegisterCustomer([FromBody] CustomerRegistrationRequest request, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new RegisterCustomerCommand(
            request.CardCode,
            request.CardName,
            request.Email,
            request.Password), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

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

    /// <summary>
    /// Bulk register customers from SAP Business Partners
    /// </summary>
    [HttpPost("bulk-register")]
    [ProducesResponseType(typeof(BulkRegistrationResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> BulkRegisterCustomers([FromBody] BulkRegistrationRequest request, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new BulkRegisterCustomersCommand(
            request.DefaultPassword!,
            request.Customers), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }
}
