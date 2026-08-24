using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Mobile;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Invoices.Queries.GetInvoicesByCustomer;

namespace ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesCustomerInvoices;

/// <summary>
/// The handset's half of the channel drill-down: check the account may look outside its own route,
/// then hand the read to the invoice slice that already knows how to walk SAP.
/// </summary>
/// <remarks>
/// <see cref="GetInvoicesByCustomerQuery"/> is sent with <c>RestrictToAssignedCustomers: false</c>
/// deliberately. Its own scoping is the mobile assigned-customer rule, which would refuse nearly
/// every customer in a channel — the gate that belongs here is the role one, and it is applied above
/// rather than delegated.
/// </remarks>
public sealed class GetVanSalesCustomerInvoicesHandler(
    ApplicationDbContext db,
    IMediator mediator,
    ILogger<GetVanSalesCustomerInvoicesHandler> logger
) : IRequestHandler<GetVanSalesCustomerInvoicesQuery, ErrorOr<InvoiceDateResponseDto>>
{
    public async Task<ErrorOr<InvoiceDateResponseDto>> Handle(
        GetVanSalesCustomerInvoicesQuery query,
        CancellationToken cancellationToken)
    {
        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == query.UserId, cancellationToken);

        if (user is null)
        {
            return Error.NotFound("VanSalesCompatibility.UserNotFound", "User was not found.");
        }

        if (!user.IsActive)
        {
            return Error.Unauthorized("VanSalesCompatibility.UserInactive", "User is not active.");
        }

        if (!ChannelCustomerAccess.MaySeeWholeChannel(user.Role))
        {
            logger.LogInformation(
                "Refused off-route customer invoices for {Username} in role {Role}", user.Username, user.Role);

            return Error.Forbidden("VanSalesCompatibility.ChannelNotPermitted", ChannelCustomerAccess.Refusal);
        }

        if (string.IsNullOrWhiteSpace(query.Code))
        {
            return Error.Validation("VanSalesCompatibility.CustomerCodeRequired", "A customer code is required.");
        }

        return await mediator.Send(
            new GetInvoicesByCustomerQuery(
                query.Code.Trim(),
                query.From,
                query.To,
                query.Page,
                query.PageSize,
                RequestingUserId: user.Id,
                RestrictToAssignedCustomers: false),
            cancellationToken);
    }
}
