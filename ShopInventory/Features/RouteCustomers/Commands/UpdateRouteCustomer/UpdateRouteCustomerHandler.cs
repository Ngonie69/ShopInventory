using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Vending;

namespace ShopInventory.Features.RouteCustomers.Commands.UpdateRouteCustomer;

public sealed class UpdateRouteCustomerHandler(
    ApplicationDbContext context
) : IRequestHandler<UpdateRouteCustomerCommand, ErrorOr<RouteCustomerDto>>
{
    public async Task<ErrorOr<RouteCustomerDto>> Handle(
        UpdateRouteCustomerCommand command,
        CancellationToken cancellationToken)
    {
        var routeCustomer = await context.RouteCustomers
            .AsTracking()
            .Include(customer => customer.CreatedByUser)
            .FirstOrDefaultAsync(customer => customer.Id == command.Id, cancellationToken);

        if (routeCustomer is null)
        {
            return Errors.RouteCustomers.NotFound(command.Id);
        }

        var assignedBusinessPartnerCode = NullIfWhiteSpace(command.Request.AssignedBusinessPartnerCode);
        if (string.IsNullOrWhiteSpace(assignedBusinessPartnerCode))
        {
            return Errors.RouteCustomers.RouteBusinessPartnerRequired;
        }

        var name = NullIfWhiteSpace(command.Request.Name);
        if (string.IsNullOrWhiteSpace(name))
        {
            return Errors.RouteCustomers.NameRequired;
        }

        var code = NormalizeCode(command.Request.Code) ?? routeCustomer.Code;

        // Held to the vending code convention only when the code or the depot changes, so a vendor
        // saved under an older code can still have its phone number corrected where it is. A move
        // between depots on different warehouses is refused rather than re-coded: the till invoices by
        // the code, and the code names the depot.
        var codeChanged = !string.Equals(code, routeCustomer.Code, StringComparison.OrdinalIgnoreCase);
        var depotChanged = !string.Equals(assignedBusinessPartnerCode, routeCustomer.AssignedBusinessPartnerCode, StringComparison.OrdinalIgnoreCase);
        if (codeChanged || depotChanged)
        {
            var depot = await VendingDepots.FindAsync(context, assignedBusinessPartnerCode, cancellationToken);
            if (depot is not null)
            {
                if (depot.Prefix is null)
                {
                    return Errors.Vending.DepotCannotNumberVendors(depot.BusinessPartnerCode, depot.Problem!);
                }

                if (!VendorCodeConvention.TryParse(code, out var prefix, out _) ||
                    !string.Equals(prefix, depot.Prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return Errors.Vending.VendorCodeDoesNotFitDepot(code, depot.BusinessPartnerCode, depot.Prefix);
                }

                var heldElsewhere = await context.RouteCustomers
                    .AsNoTracking()
                    .Where(customer => customer.Id != command.Id
                        && customer.Code == code
                        && customer.AssignedBusinessPartnerCode != assignedBusinessPartnerCode)
                    .Select(customer => customer.AssignedBusinessPartnerCode)
                    .FirstOrDefaultAsync(cancellationToken);

                if (heldElsewhere is not null)
                {
                    return Errors.Vending.VendorCodeTaken(code, heldElsewhere);
                }
            }
        }

        var existingCodes = await context.RouteCustomers
            .AsNoTracking()
            .Where(customer => customer.Id != command.Id
                && customer.AssignedBusinessPartnerCode == assignedBusinessPartnerCode)
            .Select(customer => customer.Code)
            .ToListAsync(cancellationToken);

        if (existingCodes.Any(existingCode => string.Equals(existingCode, code, StringComparison.OrdinalIgnoreCase)))
        {
            return Errors.RouteCustomers.CodeAlreadyExists(assignedBusinessPartnerCode, code);
        }

        routeCustomer.AssignedBusinessPartnerCode = assignedBusinessPartnerCode;
        routeCustomer.Code = code;
        routeCustomer.Name = name;
        routeCustomer.Surname = NullIfWhiteSpace(command.Request.Surname);
        routeCustomer.Phone = NullIfWhiteSpace(command.Request.Phone);
        routeCustomer.Email = NullIfWhiteSpace(command.Request.Email);
        routeCustomer.Address = NullIfWhiteSpace(command.Request.Address);
        routeCustomer.VatNumber = NullIfWhiteSpace(command.Request.VatNumber);
        routeCustomer.IsActive = command.Request.IsActive;
        routeCustomer.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync(cancellationToken);

        return new RouteCustomerDto
        {
            Id = routeCustomer.Id,
            AssignedBusinessPartnerCode = routeCustomer.AssignedBusinessPartnerCode,
            Code = routeCustomer.Code,
            Name = routeCustomer.Name,
            Surname = routeCustomer.Surname,
            Phone = routeCustomer.Phone,
            Email = routeCustomer.Email,
            Address = routeCustomer.Address,
            VatNumber = routeCustomer.VatNumber,
            IsActive = routeCustomer.IsActive,
            CreatedByUserId = routeCustomer.CreatedByUserId,
            CreatedByUserName = routeCustomer.CreatedByUser?.Username,
            CreatedAt = routeCustomer.CreatedAt,
            UpdatedAt = routeCustomer.UpdatedAt
        };
    }

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToUpperInvariant();
    }
}