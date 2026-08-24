using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Mobile;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesChannelCustomers;

public sealed class GetVanSalesChannelCustomersHandler(
    ApplicationDbContext db,
    ISAPServiceLayerClient sapClient,
    ILogger<GetVanSalesChannelCustomersHandler> logger
) : IRequestHandler<GetVanSalesChannelCustomersQuery, ErrorOr<List<VanSalesChannelCustomerDto>>>
{
    public async Task<ErrorOr<List<VanSalesChannelCustomerDto>>> Handle(
        GetVanSalesChannelCustomersQuery query,
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
                "Refused channel customer listing for {Username} in role {Role}", user.Username, user.Role);

            return Error.Forbidden("VanSalesCompatibility.ChannelNotPermitted", ChannelCustomerAccess.Refusal);
        }

        if (string.IsNullOrWhiteSpace(query.Channel))
        {
            return Error.Validation("VanSalesCompatibility.ChannelRequired", "A channel is required.");
        }

        // Throws where the UDF is not defined on OCRD in this company database, and that is deliberate:
        // see GetCustomersByChannelAsync. An empty list here means a channel nobody is in, which is a
        // different fact and reads differently on the handset.
        var partners = await sapClient.GetCustomersByChannelAsync(query.Channel, cancellationToken);

        var customers = partners
            .Where(partner => !string.IsNullOrWhiteSpace(partner.CardCode))
            .Select(partner => new VanSalesChannelCustomerDto
            {
                Code = partner.CardCode!.Trim(),
                Name = partner.CardName?.Trim() ?? string.Empty,
                Channel = partner.Channel?.Trim() ?? string.Empty,
                Phone = partner.Phone1?.Trim() ?? string.Empty,
                Address = partner.Address?.Trim() ?? string.Empty,
                City = partner.City?.Trim() ?? string.Empty,
                Currency = partner.Currency?.Trim() ?? string.Empty,
                Balance = partner.Balance ?? 0m,
                Active = partner.IsActive
            })
            .OrderBy(customer => customer.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        logger.LogInformation(
            "Listed {Count} customer(s) in channel {Channel} for {Username}",
            customers.Count, query.Channel, user.Username);

        return customers;
    }
}
