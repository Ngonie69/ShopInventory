using ErrorOr;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ShopInventory.Authentication;
using ShopInventory.Common.Security;
using ShopInventory.DTOs;
using ShopInventory.Features.VanSalesCompatibility.Commands.CreateVanSalesDirectInvoice;
using ShopInventory.Features.VanSalesCompatibility.Commands.ChangeVanSalesPassword;
using ShopInventory.Features.VanSalesCompatibility.Commands.CreateVanSalesSalesOrder;
using ShopInventory.Features.VanSalesCompatibility.Commands.CreateVanSalesTransferRequest;
using ShopInventory.Features.VanSalesCompatibility.Commands.PostVanSalesAttendance;
using ShopInventory.Features.VanSalesCompatibility.Commands.ConfirmVanSalesTransferRequest;
using ShopInventory.Features.VanSalesCompatibility.Commands.UploadVanSalesPod;
using ShopInventory.Features.VanSalesCompatibility.Commands.LoginVanSales;
using ShopInventory.Features.VanSalesCompatibility.Commands.RefreshVanSales;
using ShopInventory.Features.VanSalesCompatibility.Commands.ConvertVanSalesSalesOrderToInvoice;
using ShopInventory.Features.RouteCustomers.Commands.CreateRouteCustomer;
using ShopInventory.Features.VanSalesCompatibility;
using ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesAttendance;
using ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesAttendanceByDate;
using ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesAttendanceStatus;
using ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesCustomers;
using ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesFiscal;
using ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesOrderHistory;
using ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesSalesOrderHistory;
using ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesTransferRequests;
using ShopInventory.Models;

namespace ShopInventory.Controllers;

[ServiceFilter(typeof(VanSalesAuditFilter))]
[Route("api/vansales")]
public class VanSalesCompatibilityController(IMediator mediator) : ApiControllerBase
{
    [HttpPost("auth/login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Login(
        [FromBody] AuthLoginRequest request,
        CancellationToken cancellationToken)
    {
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var result = await mediator.Send(new LoginVanSalesCommand(request, ipAddress), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPost("auth/refresh")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Refresh(
        [FromBody] RefreshTokenRequest request,
        CancellationToken cancellationToken)
    {
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var result = await mediator.Send(new RefreshVanSalesCommand(request, ipAddress), cancellationToken);
        return result.Match(value => Ok(value), errors => Problem(errors));
    }

    [HttpPost("auth/password")]
    [Authorize(Policy = "ApiAccess")]
    public async Task<IActionResult> ChangePassword(
        [FromBody] VanSalesPasswordChangeRequest request,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(new ChangeVanSalesPasswordCommand(request, userId.Value), cancellationToken);
        return result.Match(
            value => Ok(new VanSalesEnvelope<string> { Success = value }),
            errors => Problem(errors));
    }

    [HttpGet("attendance")]
    [Authorize(Policy = "ApiAccess")]
    [RequirePermission(Permission.ViewTimesheets)]
    public async Task<IActionResult> GetAttendance(CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(new GetVanSalesAttendanceQuery(userId.Value), cancellationToken);
        return result.Match(
            value => Ok(value),
            errors => Ok(VanSalesAttendanceMapper.MapListFailure(GetLegacyErrorMessage(errors))));
    }

    [HttpGet("attendance/date")]
    [Authorize(Policy = "ApiAccess")]
    [RequirePermission(Permission.ViewTimesheets)]
    public async Task<IActionResult> GetAttendanceByDate(
        [FromQuery(Name = "value")] string value,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(new GetVanSalesAttendanceByDateQuery(userId.Value, value), cancellationToken);
        return result.Match(
            value => Ok(value),
            errors => Ok(VanSalesAttendanceMapper.MapByDateFailure(GetLegacyErrorMessage(errors))));
    }

    [HttpGet("attendance/status")]
    [Authorize(Policy = "ApiAccess")]
    [RequirePermission(Permission.ManageTimesheets)]
    public async Task<IActionResult> GetAttendanceStatus(CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(new GetVanSalesAttendanceStatusQuery(userId.Value), cancellationToken);
        return result.Match(
            value => Ok(value),
            errors => Ok(VanSalesAttendanceMapper.MapStatusFailure(GetLegacyErrorMessage(errors))));
    }

    [HttpPost("attendance")]
    [Authorize(Policy = "ApiAccess")]
    [RequirePermission(Permission.ManageTimesheets)]
    public async Task<IActionResult> PostAttendance(
        [FromBody] VanSalesAttendanceRequest request,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(new PostVanSalesAttendanceCommand(request, userId.Value), cancellationToken);
        return result.Match(
            value => Ok(value),
            errors => Ok(VanSalesAttendanceMapper.MapCheckFailure(GetLegacyErrorMessage(errors))));
    }

    [HttpGet("customer")]
    [Authorize(Policy = "ApiAccess")]
    [RequirePermission(Permission.ViewCustomers)]
    public async Task<IActionResult> GetCustomers(CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(new GetVanSalesCustomersQuery(userId.Value), cancellationToken);
        return result.Match(
            value => Ok(new VanSalesEnvelope<List<VanSalesShopDto>> { Success = value }),
            errors => Problem(errors));
    }

    [HttpPost("customer")]
    [Authorize(Policy = "ApiAccess")]
    [RequirePermission(Permission.CreateCustomers)]
    public async Task<IActionResult> CreateCustomer(
        [FromBody] VanSalesCreateCustomerRequest request,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
        {
            return Unauthorized();
        }

        var createRequest = new CreateRouteCustomerRequest
        {
            Code = request.Code,
            Name = request.Name,
            Phone = request.Phone,
            Email = request.Email,
            Address = request.Address,
            VatNumber = request.VatNumber,
            IsActive = !request.Status.HasValue || request.Status.Value != 0
        };

        var result = await mediator.Send(new CreateRouteCustomerCommand(createRequest, userId.Value), cancellationToken);
        return result.Match(
            value => Ok(new VanSalesEnvelope<VanSalesShopDto>
            {
                Success = new VanSalesShopDto
                {
                    Id = VanSalesCompatibilityMapper.EncodeCompatibilityId(value.Code),
                    Code = value.Code,
                    Name = value.Name,
                    Phone = value.Phone ?? string.Empty,
                    Email = value.Email ?? string.Empty,
                    Address = value.Address ?? string.Empty,
                    BpNumber = value.AssignedBusinessPartnerCode,
                    VatNumber = value.VatNumber ?? string.Empty,
                    Status = value.IsActive ? 1 : 0,
                    CreatedAt = value.CreatedAt.ToString("O")
                }
            }),
            errors => Problem(errors));
    }

    [HttpPost("sales-order")]
    [Authorize(Policy = "ApiAccess")]
    [RequirePermission(Permission.CreateSalesOrders)]
    public async Task<IActionResult> CreateSalesOrder(
        [FromBody] VanSalesOrderRequest request,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(new CreateVanSalesSalesOrderCommand(request, userId.Value), cancellationToken);
        return result.Match(
            value => Ok(new VanSalesEnvelope<VanSalesLegacyOrderDto> { Success = value }),
            errors => Problem(errors));
    }

    [HttpPost("sales-order/history")]
    [Authorize(Policy = "ApiAccess")]
    [RequirePermission(Permission.ViewSalesOrders)]
    public async Task<IActionResult> GetSalesOrderHistory(
        [FromBody] VanSalesOrderSearchRequest request,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(new GetVanSalesSalesOrderHistoryQuery(userId.Value, request), cancellationToken);
        return result.Match(
            value => Ok(new VanSalesEnvelope<List<VanSalesLegacyOrderDto>> { Success = value }),
            errors => Problem(errors));
    }

    [HttpPost("order/history")]
    [Authorize(Policy = "ApiAccess")]
    [RequirePermission(Permission.ViewInvoices)]
    public async Task<IActionResult> GetOrderHistory(
        [FromBody] VanSalesOrderSearchRequest request,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(new GetVanSalesOrderHistoryQuery(userId.Value, request), cancellationToken);
        return result.Match(
            value => Ok(new VanSalesEnvelope<List<VanSalesLegacyOrderDto>> { Success = value }),
            errors => Problem(errors));
    }

    [HttpGet("fiscal")]
    [Authorize(Policy = "ApiAccess")]
    [RequirePermission(Permission.ViewInvoices)]
    public async Task<IActionResult> GetFiscalInfo(CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(new GetVanSalesFiscalQuery(userId.Value), cancellationToken);
        return result.Match(
            value => Ok(new VanSalesEnvelope<VanSalesLegacyFiscalDto> { Success = value }),
            errors => Problem(errors));
    }

    [HttpPost("pod")]
    [Authorize(Policy = "ApiAccess")]
    [RequirePermission(Permission.ViewInvoices)]
    public async Task<IActionResult> UploadPod(
        [FromBody] VanSalesPodUploadRequest request,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(new UploadVanSalesPodCommand(request, userId.Value), cancellationToken);
        return result.Match(
            value => Ok(new VanSalesEnvelope<string> { Success = value }),
            errors => Problem(errors));
    }

    [HttpPost("order")]
    [HttpPost("order/with-batches")]
    [Authorize(Policy = "ApiAccess")]
    [RequirePermission(Permission.CreateInvoices)]
    public async Task<IActionResult> CreateDirectInvoice(
        [FromBody] VanSalesOrderRequest request,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(new CreateVanSalesDirectInvoiceCommand(request, userId.Value), cancellationToken);
        return result.Match(
            value => value.WasQueued ? Accepted(value) : Ok(value),
            errors => Problem(errors));
    }

    [HttpPost("order/convert-to-invoice")]
    [Authorize(Policy = "ApiAccess")]
    [RequirePermission(Permission.CreateInvoices)]
    public async Task<IActionResult> ConvertSalesOrderToInvoice(
        [FromBody] VanSalesOrderRequest request,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(new ConvertVanSalesSalesOrderToInvoiceCommand(request, userId.Value), cancellationToken);
        return result.Match(value => Accepted(value), errors => Problem(errors));
    }

    [HttpPost("inventory/request")]
    [Authorize(Policy = "ApiAccess")]
    [RequirePermission(Permission.TransferInventory)]
    public async Task<IActionResult> CreateTransferRequest(
        [FromBody] VanSalesTransferRequest request,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(new CreateVanSalesTransferRequestCommand(request, userId.Value), cancellationToken);
        return result.Match(
            value => StatusCode(StatusCodes.Status201Created, value),
            errors => Problem(errors));
    }

    [HttpGet("inventory/request")]
    [Authorize(Policy = "ApiAccess")]
    [RequirePermission(Permission.TransferInventory)]
    public async Task<IActionResult> GetTransferRequests(CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(new GetVanSalesTransferRequestsQuery(userId.Value), cancellationToken);
        return result.Match(
            value => Ok(new VanSalesEnvelope<List<VanSalesLegacyInventoryOrderDto>> { Success = value }),
            errors => Problem(errors));
    }

    private static string GetLegacyErrorMessage(List<Error> errors)
    {
        return errors.Count > 0
            ? errors[0].Description
            : "Request failed.";
    }

    [HttpPost("inventory/confirm")]
    [Authorize(Policy = "ApiAccess")]
    [RequirePermission(Permission.TransferInventory)]
    public async Task<IActionResult> ConfirmTransferRequest(
        [FromBody] VanSalesTransferApprovalRequest request,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(new ConfirmVanSalesTransferRequestCommand(request, userId.Value), cancellationToken);
        return result.Match(
            value => Ok(new VanSalesEnvelope<string> { Success = value }),
            errors => Problem(errors));
    }
}