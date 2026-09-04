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
using ShopInventory.Features.VanSalesCompatibility.Commands.DeleteVanSalesCustomer;
using ShopInventory.Features.VanSalesCompatibility.Commands.UpdateVanSalesCustomer;
using ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesCustomerHistory;
using ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesChannelCustomers;
using ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesCustomerInvoices;
using ShopInventory.Common.Mobile;
using ShopInventory.Features.VanSalesCompatibility.Commands.CreateVanSalesSalesOrder;
using ShopInventory.Features.VanSalesCompatibility.Commands.CreateVanSalesTransferRequest;
using ShopInventory.Features.VanSalesCompatibility.Commands.PostVanSalesAttendance;
using ShopInventory.Features.VanSalesCompatibility.Commands.StartVanSalesDay;
using ShopInventory.Features.VanSalesCompatibility.Commands.EndVanSalesDay;
using ShopInventory.Features.VanSalesCompatibility.Commands.ConfirmVanSalesTransferRequest;
using ShopInventory.Features.VanSalesCompatibility.Commands.IngestVanSalesOfflineSales;
using ShopInventory.Features.VanSalesCompatibility.Commands.RecordVanSalesFiscalDayClose;
using ShopInventory.Features.VanSalesCompatibility.Commands.ReportVanSalesStockPosition;
using ShopInventory.Features.VanSalesCompatibility.Commands.UploadVanSalesPod;
using ShopInventory.Features.VanSalesCompatibility.Commands.UploadVanSalesPodFile;
using ShopInventory.Middleware;
using ShopInventory.Features.VanSalesCompatibility.Commands.LoginVanSales;
using ShopInventory.Features.VanSalesCompatibility.Commands.RefreshVanSales;
using ShopInventory.Features.VanSalesCompatibility.Commands.ConvertVanSalesSalesOrderToInvoice;
using ShopInventory.Features.RouteCustomers.Commands.CreateRouteCustomer;
using ShopInventory.Features.VanSalesCompatibility;
using ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesAttendance;
using ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesAttendanceByDate;
using ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesAttendanceStatus;
using ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesCurrentDay;
using ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesCustomers;
using ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesFiscal;
using ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesFiscalLease;
using ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesOrderHistory;
using ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesSalesOrderHistory;
using ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesTransferRequests;
using ShopInventory.Models;

namespace ShopInventory.Controllers;

[ServiceFilter(typeof(VanSalesAuditFilter))]
[Route("api/vansales")]
public class VanSalesCompatibilityController(IMediator mediator) : ApiControllerBase
{
    /// <summary>
    /// Sign a van sales handset in; rate-limited under the auth policy
    /// </summary>
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

    /// <summary>
    /// Refresh a van sales handset's tokens; rate-limited under the auth policy
    /// </summary>
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

    /// <summary>
    /// Change own password
    /// </summary>
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

    /// <summary>
    /// The caller's own calls
    /// </summary>
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

    /// <summary>
    /// Query parameter is value
    /// </summary>
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

    /// <summary>
    /// Whether the caller is checked in
    /// </summary>
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

    /// <summary>
    /// Check in or out
    /// </summary>
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

    // ── The trading day ──────────────────────────────────────────────────
    //
    // The departure compliance record: out of the depot in the morning, back in the evening. Sits
    // beside attendance rather than inside it because it is a different unit — attendance counts
    // visits, this bounds the day they happened in and carries the facts no visit knows (the truck,
    // the odometer, the takings counted at the end).

    /// <summary>
    /// The open trading day
    /// </summary>
    [HttpGet("day/current")]
    [Authorize(Policy = "ApiAccess")]
    [RequirePermission(Permission.ManageTimesheets)]
    public async Task<IActionResult> GetCurrentDay(CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(new GetVanSalesCurrentDayQuery(userId.Value), cancellationToken);
        return result.Match(
            value => Ok(value),
            errors => Ok(VanSalesRouteDayMapper.Failure(GetLegacyErrorMessage(errors))));
    }

    /// <summary>
    /// Out of the depot: truck, route, opening odometer
    /// </summary>
    [HttpPost("day/start")]
    [Authorize(Policy = "ApiAccess")]
    [RequirePermission(Permission.ManageTimesheets)]
    public async Task<IActionResult> StartDay(
        [FromBody] VanSalesStartDayRequest request,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(new StartVanSalesDayCommand(request, userId.Value), cancellationToken);
        return result.Match(
            value => Ok(value),
            errors => Ok(VanSalesRouteDayMapper.Failure(GetLegacyErrorMessage(errors))));
    }

    /// <summary>
    /// Back in: closing odometer and the takings counted
    /// </summary>
    [HttpPost("day/end")]
    [Authorize(Policy = "ApiAccess")]
    [RequirePermission(Permission.ManageTimesheets)]
    public async Task<IActionResult> EndDay(
        [FromBody] VanSalesEndDayRequest request,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(new EndVanSalesDayCommand(request, userId.Value), cancellationToken);
        return result.Match(
            value => Ok(value),
            errors => Ok(VanSalesRouteDayMapper.Failure(GetLegacyErrorMessage(errors))));
    }

    /// <summary>
    /// The shops on the caller's route
    /// </summary>
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

    /// <summary>
    /// Create a route customer
    /// </summary>
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

    /// <summary>
    /// Corrects the contact details the handset holds for a shop on its own route.
    /// </summary>
    /// <remarks>
    /// Narrower than the administrator's update: the route, the code and the active flag are read off
    /// the row rather than taken from the body, so a handset cannot move a shop to another van,
    /// rename the identity its sales are filed under, or perform the removal through this.
    /// </remarks>
    [HttpPut("customer/{code}")]
    [Authorize(Policy = "ApiAccess")]
    [RequirePermission(Permission.EditCustomers)]
    public async Task<IActionResult> UpdateCustomer(
        string code,
        [FromBody] VanSalesUpdateCustomerRequest request,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(
            new UpdateVanSalesCustomerCommand(userId.Value, code, request),
            cancellationToken);

        return result.Match(
            value => Ok(new VanSalesEnvelope<VanSalesShopDto> { Success = value }),
            errors => Problem(errors));
    }

    /// <summary>
    /// What one shop on the handset's route has bought, and what it still has on order.
    /// </summary>
    /// <remarks>
    /// The same detail the office's route customer report reads, so the rep standing in the shop and
    /// the office looking at the route are never told two different things about it.
    /// </remarks>
    /// <summary>
    /// Every General Trade customer in the company, and then the invoices SAP holds against one.
    /// </summary>
    /// <remarks>
    /// The only customer reads on this controller that are not scoped to the signed-in rep's route, so
    /// both are gated on role inside their handlers — see
    /// <see cref="ShopInventory.Common.Mobile.ChannelCustomerAccess"/>.
    ///
    /// <para>Both carry <c>ViewCustomers</c> rather than <c>ViewInvoices</c>, and that is not an
    /// oversight. It is what <c>customer/{code}/history</c> beside them already does for the same
    /// question about a route customer, and a stock controller — one of the two roles allowed here —
    /// holds <c>ViewCustomers</c> but not <c>ViewInvoices</c>. Gating on invoices would have meant
    /// widening that role's rights across the whole platform to open one handset screen.</para>
    ///
    /// <para>The channel is fixed rather than taken from the route. Nothing yet asks for a second one,
    /// and a path segment a caller chooses would be a filter over the customer book that no permission
    /// covers; widening it later is a parameter here and a constant in
    /// <see cref="ShopInventory.Common.Mobile.ChannelCustomerAccess"/>.</para>
    /// </remarks>
    [HttpGet("customer/general-trade")]
    [Authorize(Policy = "ApiAccess")]
    [RequirePermission(Permission.ViewCustomers)]
    public async Task<IActionResult> GetGeneralTradeCustomers(CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(
            new GetVanSalesChannelCustomersQuery(userId.Value, ChannelCustomerAccess.GeneralTrade),
            cancellationToken);

        return result.Match(
            value => Ok(new VanSalesEnvelope<List<VanSalesChannelCustomerDto>> { Success = value }),
            errors => Problem(errors));
    }

    /// <summary>
    /// The invoices SAP holds against one customer, whoever raised them.
    /// </summary>
    /// <remarks>
    /// Route-order matters: this sits after <c>customer/general-trade</c> so that literal segment is
    /// never captured as a customer code.
    /// </remarks>
    [HttpGet("customer/{code}/invoices")]
    [Authorize(Policy = "ApiAccess")]
    [RequirePermission(Permission.ViewCustomers)]
    public async Task<IActionResult> GetCustomerInvoices(
        string code,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(
            new GetVanSalesCustomerInvoicesQuery(userId.Value, code, from, to, page, pageSize),
            cancellationToken);

        return result.Match(
            value => Ok(new VanSalesEnvelope<InvoiceDateResponseDto> { Success = value }),
            errors => Problem(errors));
    }

    /// <summary>
    /// What one shop has bought and still has on order, the detail the office's route customer report reads
    /// </summary>
    [HttpGet("customer/{code}/history")]
    [Authorize(Policy = "ApiAccess")]
    [RequirePermission(Permission.ViewCustomers)]
    public async Task<IActionResult> GetCustomerHistory(
        string code,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(
            new GetVanSalesCustomerHistoryQuery(userId.Value, code, from, to),
            cancellationToken);

        return result.Match(
            value => Ok(new VanSalesEnvelope<RouteCustomerSalesDetailDto> { Success = value }),
            errors => Problem(errors));
    }

    /// <summary>
    /// Takes a shop the rep no longer services off their route.
    /// </summary>
    /// <remarks>
    /// By code, because a handset is never given the route customer id — the customer payload carries
    /// a compatibility id derived from the code instead. The row is deactivated rather than removed,
    /// so the route keeps its trading history.
    /// </remarks>
    [HttpDelete("customer/{code}")]
    [Authorize(Policy = "ApiAccess")]
    [RequirePermission(Permission.DeleteCustomers)]
    public async Task<IActionResult> DeleteCustomer(
        string code,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(new DeleteVanSalesCustomerCommand(userId.Value, code), cancellationToken);
        return result.Match(
            _ => Ok(new VanSalesEnvelope<string> { Success = code }),
            errors => Problem(errors));
    }

    /// <summary>
    /// Create a sales order
    /// </summary>
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

    /// <summary>
    /// Search — a POST because the filter is a body
    /// </summary>
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

    /// <summary>
    /// Invoice history; also a POST
    /// </summary>
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

    /// <summary>
    /// Fiscal device details for the handset
    /// </summary>
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

    /// <summary>
    /// Issues this handset's fiscal lease: the device it signs receipts as, the open fiscal day, the
    /// sequence position to sign from, and the tax on every item it might sell.
    ///
    /// Returned bare rather than wrapped in <see cref="VanSalesEnvelope{T}"/>, unlike the legacy routes
    /// beside it — nothing legacy consumes this, and the handset parses it directly.
    /// </summary>
    /// <param name="cancellationToken">Cancels the lease lookup if the handset drops the request.</param>
    /// <param name="pendingSales">
    /// Signed receipts the handset is still carrying. Optional, and absent from builds that predate the
    /// nomination — see <see cref="GetVanSalesFiscalLeaseQuery"/> for why it is asked for here.
    /// </param>
    [HttpGet("fiscal/lease")]
    [Authorize(Policy = "ApiAccess")]
    [RequirePermission(Permission.CreateInvoices)]
    public async Task<IActionResult> GetFiscalLease(
        CancellationToken cancellationToken,
        [FromQuery] int? pendingSales = null)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(
            new GetVanSalesFiscalLeaseQuery(userId.Value, pendingSales), cancellationToken);

        return result.Match<IActionResult>(Ok, errors => Problem(errors));
    }

    /// <summary>
    /// One page of a delivery note, sent as a file.
    /// </summary>
    /// <remarks>
    /// The same upload the drivers' POD app makes against <c>invoice/{docEntry}/pod</c>, reachable by a
    /// van rep. That route is gated by role and a van rep's role is <c>Sales</c>, which is not on its
    /// list — see <see cref="UploadVanSalesPodFileCommand"/> for why this mirrors it rather than
    /// widening it.
    ///
    /// <para>Preferred over <c>pod</c> beside it, which carries whole photographs as base64 inside a
    /// JSON body: a page arrives here at its own size, one request per page, and each page says whether
    /// it is a further page of the same note.</para>
    /// </remarks>
    [HttpPost("pod/{order:int}/file")]
    [Authorize(Policy = "ApiAccess")]
    [RequirePermission(Permission.ViewInvoices)]
    [MaxRequestBodySize(20 * 1024 * 1024)]
    public async Task<IActionResult> UploadPodFile(
        int order,
        IFormFile file,
        [FromForm] string? description = null,
        [FromForm] string? externalReference = null,
        [FromForm] bool isAdditionalPage = false,
        CancellationToken cancellationToken = default)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
        {
            return Unauthorized();
        }

        if (file is null || file.Length == 0)
        {
            return Problem([Error.Validation(
                "VanSalesCompatibility.MissingPodImages",
                "Please capture the delivery note first.")]);
        }

        // The same list the portal's own POD upload accepts. A handset sends JPEG; the rest are here so
        // a page scanned to PDF or saved as PNG is not refused for the sake of its container.
        var allowed = new[] { "image/jpeg", "image/png", "image/webp", "application/pdf" };
        if (!allowed.Contains(file.ContentType, StringComparer.OrdinalIgnoreCase))
        {
            return Problem([Error.Validation(
                "VanSalesCompatibility.InvalidPodImage",
                "Only JPEG, PNG, WebP images and PDF files can be filed as a delivery note.")]);
        }

        using var stream = file.OpenReadStream();

        var result = await mediator.Send(
            new UploadVanSalesPodFileCommand(
                order,
                stream,
                file.FileName,
                file.ContentType,
                description,
                externalReference,
                isAdditionalPage,
                userId.Value),
            cancellationToken);

        return result.Match(
            value => Ok(new VanSalesEnvelope<DocumentAttachmentDto> { Success = value }),
            errors => Problem(errors));
    }

    /// <summary>
    /// Upload proof of delivery
    /// </summary>
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

    /// <summary>
    /// Direct invoice. 202 when queued rather than posted
    /// </summary>
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

    /// <summary>
    /// Takes custody of sales a van completed and ZIMRA-stamped while offline.
    ///
    /// Distinct from <c>order</c>/<c>order/with-batches</c> in the two ways that matter: nothing here
    /// reaches SAP on the request (the batch is held for the end-of-day posting run), and nothing here is
    /// fiscalised (the customer already holds the printed receipt). Per-sale outcomes are returned so one
    /// bad row cannot strand a van's whole backlog on the handset.
    /// </summary>
    [HttpPost("sales")]
    [Authorize(Policy = "ApiAccess")]
    [RequirePermission(Permission.CreateInvoices)]
    public async Task<IActionResult> IngestOfflineSales(
        [FromBody] VanSalesOfflineSaleBatchRequest request,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(
            new IngestVanSalesOfflineSalesCommand(request, userId.Value), cancellationToken);

        return result.Match<IActionResult>(Ok, errors => Problem(errors));
    }

    /// <summary>
    /// Takes custody of the close a handset signed for its own fiscal day.
    /// </summary>
    /// <remarks>
    /// This is the only route by which a van's fiscal day can be closed. The platform holds the handset's
    /// certificate and not its private key, so it can verify this signature but never produce one — if
    /// the handset does not send this, the day stays open and ZIMRA is never told what it sold.
    ///
    /// Held rather than forwarded on the request: the handset signs its close the moment its day ends,
    /// which is before its last receipts have necessarily reached this service, and the day cannot be
    /// packaged until they have.
    /// </remarks>
    [HttpPost("fiscal/day-close")]
    [Authorize(Policy = "ApiAccess")]
    [RequirePermission(Permission.CreateInvoices)]
    public async Task<IActionResult> RecordFiscalDayClose(
        [FromBody] VanSalesFiscalDayCloseRequest request,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(
            new RecordVanSalesFiscalDayCloseCommand(request, userId.Value), cancellationToken);

        return result.Match<IActionResult>(Ok, errors => Problem(errors));
    }

    /// <summary>
    /// Convert a van sales order to an invoice; always answers 202
    /// </summary>
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

    /// <summary>
    /// Ask the depot for stock. 201
    /// </summary>
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

    /// <summary>
    /// What this van is carrying, as its own handset counts it.
    /// </summary>
    /// <remarks>
    /// The van is the only live source for this. SAP's figure for a van warehouse is a day behind —
    /// the sales that moved it were signed on the handset and are still queued — so the daily snapshot
    /// job, which reads SAP, cannot answer it, and does not visit van warehouses at all unless they are
    /// named in its configured list. The first count of a day is the one kept; see the handler.
    /// </remarks>
    [HttpPost("stock/position")]
    [Authorize(Policy = "ApiAccess")]
    [RequirePermission(Permission.TransferInventory)]
    public async Task<IActionResult> ReportStockPosition(
        [FromBody] VanSalesStockPositionRequest request,
        CancellationToken cancellationToken)
    {
        var userId = UserClaimReader.GetUserId(User);
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await mediator.Send(
            new ReportVanSalesStockPositionCommand(request, userId.Value), cancellationToken);

        return result.Match<IActionResult>(Ok, errors => Problem(errors));
    }

    /// <summary>
    /// The caller's transfer requests
    /// </summary>
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

    /// <summary>
    /// Confirm a transfer into the van
    /// </summary>
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