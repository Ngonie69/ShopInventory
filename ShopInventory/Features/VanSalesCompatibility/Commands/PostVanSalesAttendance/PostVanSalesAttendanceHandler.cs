using System.Globalization;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Timesheets.Commands.CheckIn;
using ShopInventory.Features.Timesheets.Commands.CheckOut;
using ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesCustomers;

namespace ShopInventory.Features.VanSalesCompatibility.Commands.PostVanSalesAttendance;

public sealed class PostVanSalesAttendanceHandler(
    ApplicationDbContext db,
    IMediator mediator
) : IRequestHandler<PostVanSalesAttendanceCommand, ErrorOr<VanSalesAttendanceCheckResponse>>
{
    public async Task<ErrorOr<VanSalesAttendanceCheckResponse>> Handle(
        PostVanSalesAttendanceCommand command,
        CancellationToken cancellationToken)
    {
        var normalizedType = command.Request.Type?.Trim().ToUpperInvariant();
        if (normalizedType is not "IN" and not "OUT")
        {
            return Error.Validation(
                "VanSalesCompatibility.InvalidAttendanceType",
                "Invalid type. Must be 'IN' or 'OUT'.");
        }

        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == command.UserId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            return Error.Unauthorized("VanSalesCompatibility.Unauthenticated", "User is not authenticated.");
        }

        var latitude = ParseCoordinate(command.Request.Latitude);
        var longitude = ParseCoordinate(command.Request.Longitude);

        if (normalizedType == "IN")
        {
            if (!command.Request.CustomerId.HasValue || command.Request.CustomerId.Value <= 0)
            {
                return Error.Validation(
                    "VanSalesCompatibility.InvalidAttendanceCustomer",
                    "Customer ID is required for check-in.");
            }

            var shopsResult = await mediator.Send(new GetVanSalesCustomersQuery(user.Id), cancellationToken);
            if (shopsResult.IsError)
            {
                return shopsResult.Errors;
            }

            var shop = shopsResult.Value
                .FirstOrDefault(candidate => candidate.Id == command.Request.CustomerId.Value);

            if (shop is null)
            {
                return Error.Validation(
                    "VanSalesCompatibility.InvalidAttendanceCustomer",
                    "The selected customer is not assigned to the current user.");
            }

            var checkInResult = await mediator.Send(
                new CheckInCommand(
                    user.Id,
                    user.Username,
                    shop.Code,
                    shop.Name,
                    latitude,
                    longitude,
                    null),
                cancellationToken);

            if (checkInResult.IsError)
            {
                return checkInResult.Errors;
            }

            return VanSalesAttendanceMapper.MapCheckInResponse(
                checkInResult.Value,
                user,
                shop,
                command.Request.Van);
        }

        var checkOutResult = await mediator.Send(
            new CheckOutCommand(
                user.Id,
                user.Username,
                latitude,
                longitude,
                null),
            cancellationToken);

        if (checkOutResult.IsError)
        {
            return checkOutResult.Errors;
        }

        return VanSalesAttendanceMapper.MapCheckOutResponse(
            checkOutResult.Value,
            user,
            command.Request.Van);
    }

    private static double? ParseCoordinate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }
}