using System.Security.Claims;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ShopInventory.DTOs;
using ShopInventory.Features.VanSalesCustomerAuth;
using ShopInventory.Features.VanSalesCustomerAuth.Commands.LogoutVanSalesCustomer;
using ShopInventory.Features.VanSalesCustomerAuth.Commands.RefreshVanSalesCustomerSession;
using ShopInventory.Features.VanSalesCustomerAuth.Commands.RequestVanSalesCustomerOtp;
using ShopInventory.Features.VanSalesCustomerAuth.Commands.SignInVanSalesCustomer;
using ShopInventory.Features.VanSalesCustomerAuth.Commands.VerifyVanSalesCustomerOtp;
using ShopInventory.Models;

namespace ShopInventory.Controllers;

/// <summary>
/// Sign-in for van sales customers ordering on their own phones.
/// </summary>
/// <remarks>
/// The class-level policy is the default, and the four <c>[AllowAnonymous]</c> actions below are the
/// only exceptions: three because a customer has no session yet, and refresh because its whole
/// purpose is to be callable once the access token has expired. Everything else added to this
/// controller inherits the policy — which is the intended direction for a mistake to fall.
/// </remarks>
[Route("api/van-sales-customer/auth")]
[Authorize(Policy = "VanSalesCustomerAccess")]
public class VanSalesCustomerAuthController(IMediator mediator) : ApiControllerBase
{
    /// <summary>
    /// Exchange a phone number and its password for a session.
    /// </summary>
    /// <remarks>
    /// What the ordering app uses. The code endpoints below remain for accounts that have no
    /// password yet and as the way back in when one is forgotten.
    /// </remarks>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [ProducesResponseType(typeof(VanSalesCustomerSessionResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> SignIn(
        [FromBody] VanSalesCustomerSignInRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new SignInVanSalesCustomerCommand(
                request.PhoneNumber,
                request.Password,
                request.DeviceId,
                request.DeviceName,
                GetIpAddress()),
            cancellationToken);

        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>
    /// Send a sign-in code to a phone number.
    /// </summary>
    /// <remarks>
    /// Answers 200 with the same body for every well-formed number, whether or not it belongs to a
    /// customer. See <see cref="RequestVanSalesCustomerOtpHandler"/> — the uniformity is the
    /// feature, not an omission.
    /// </remarks>
    [HttpPost("otp/request")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [ProducesResponseType(typeof(RequestVanSalesCustomerOtpResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> RequestOtp(
        [FromBody] VanSalesCustomerOtpRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new RequestVanSalesCustomerOtpCommand(request.PhoneNumber, GetIpAddress()),
            cancellationToken);

        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>Exchange a code for a session.</summary>
    [HttpPost("otp/verify")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [ProducesResponseType(typeof(VanSalesCustomerSessionResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> VerifyOtp(
        [FromBody] VanSalesCustomerOtpVerificationRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new VerifyVanSalesCustomerOtpCommand(
                request.PhoneNumber,
                request.Code,
                request.DeviceId,
                request.DeviceName,
                GetIpAddress()),
            cancellationToken);

        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>Rotate a refresh token for a new session.</summary>
    [HttpPost("token/refresh")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [ProducesResponseType(typeof(VanSalesCustomerSessionResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> Refresh(
        [FromBody] VanSalesCustomerRefreshRequest request,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(
            new RefreshVanSalesCustomerSessionCommand(
                request.RefreshToken,
                request.DeviceId,
                request.DeviceName,
                GetIpAddress()),
            cancellationToken);

        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    /// <summary>End the calling customer's session.</summary>
    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(
        [FromBody] VanSalesCustomerLogoutRequest request,
        CancellationToken cancellationToken)
    {
        var accountId = GetAuthenticatedCustomerAccountId();
        if (accountId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(
            new LogoutVanSalesCustomerCommand(accountId.Value, request.RefreshToken, request.DeviceId),
            cancellationToken);

        return result.Match(_ => NoContent(), errors => Problem(errors));
    }

    private string GetIpAddress() =>
        HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>
    /// The calling customer, taken from the token and from nowhere else.
    /// </summary>
    /// <remarks>
    /// Never read the account from the request body. A customer id a caller can supply is a customer
    /// id a caller can change, and the endpoints below it would happily act on someone else's
    /// account.
    /// </remarks>
    private int? GetAuthenticatedCustomerAccountId()
    {
        var claim = User.FindFirstValue(VanSalesCustomerClaims.AccountId);
        return int.TryParse(claim, out var accountId) ? accountId : null;
    }
}
