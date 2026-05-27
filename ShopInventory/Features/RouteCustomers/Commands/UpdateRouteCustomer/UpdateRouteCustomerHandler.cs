using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;

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