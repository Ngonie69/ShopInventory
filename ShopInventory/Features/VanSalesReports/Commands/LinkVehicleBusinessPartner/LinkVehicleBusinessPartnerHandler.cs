using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Common.Telematics;
using ShopInventory.Data;
using ShopInventory.Features.VanSalesReports.Queries.GetTelematicsVehicles;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesReports.Commands.LinkVehicleBusinessPartner;

public sealed class LinkVehicleBusinessPartnerHandler(
    ApplicationDbContext db,
    IAuditService auditService,
    ILogger<LinkVehicleBusinessPartnerHandler> logger
) : IRequestHandler<LinkVehicleBusinessPartnerCommand, ErrorOr<TelematicsVehicleDto>>
{
    public async Task<ErrorOr<TelematicsVehicleDto>> Handle(
        LinkVehicleBusinessPartnerCommand command, CancellationToken cancellationToken)
    {
        var plate = TelematicsRegistration.Normalize(command.Registration);

        if (plate is null)
        {
            return Error.Validation("Fleet.RegistrationRequired", "Name the vehicle to link.");
        }

        var vehicle = await db.TelematicsVehicles
            .FirstOrDefaultAsync(row => row.RegistrationNormalized == plate, cancellationToken);

        if (vehicle is null)
        {
            return Error.NotFound(
                "Fleet.VehicleNotFound",
                $"{command.Registration} is not in the telematics fleet.");
        }

        var code = string.IsNullOrWhiteSpace(command.BusinessPartnerCode)
            ? null
            : command.BusinessPartnerCode.Trim();

        // Checked against the canonical set rather than accepted as typed. Linking a truck to an
        // ordinary customer would make the fleet page read that customer's whole trade as this
        // van's takings — a wrong figure that looks entirely plausible.
        if (code is not null && !VanSalesAccounts.IsVanSalesAccount(code))
        {
            return Error.Validation(
                "Fleet.NotAVanAccount",
                $"{code} is not a van sales account. The van accounts are "
                + $"{string.Join(", ", VanSalesAccounts.CardCodes.Order())}.");
        }

        // One account, one truck. Two vehicles sharing an account would each report that
        // account's full takings, so the same money would be counted twice on one page.
        if (code is not null)
        {
            var taken = await db.TelematicsVehicles
                .AsNoTracking()
                .Where(row => row.BusinessPartnerCode == code
                              && row.RegistrationNormalized != plate)
                .Select(row => row.Registration ?? row.RegistrationNormalized)
                .FirstOrDefaultAsync(cancellationToken);

            if (taken is not null)
            {
                return Error.Conflict(
                    "Fleet.AccountAlreadyLinked",
                    $"{code} is already linked to {taken}. Unlink it there first.");
            }
        }

        var previous = vehicle.BusinessPartnerCode;

        vehicle.BusinessPartnerCode = code;
        vehicle.BusinessPartnerName = code is null
            ? null
            : string.IsNullOrWhiteSpace(command.BusinessPartnerName)
                ? null
                : command.BusinessPartnerName.Trim();

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Vehicle {Registration} linked to van account {Account} (was {Previous})",
            plate, code ?? "(none)", previous ?? "(none)");

        try
        {
            await auditService.LogAsync(
                code is null ? "VehicleAccountUnlinked" : "VehicleAccountLinked",
                "TelematicsVehicle",
                plate,
                code is null
                    ? $"{plate} unlinked from {previous ?? "(none)"}"
                    : $"{plate} linked to {code}"
                      + (previous is null ? "" : $", was {previous}"),
                true);
        }
        catch
        {
            // The audit trail is not worth failing the link over.
        }

        return new TelematicsVehicleDto(
            vehicle.Registration ?? vehicle.RegistrationNormalized,
            vehicle.RegistrationNormalized,
            vehicle.ClientVehicleName,
            string.Join(" ", new[] { vehicle.Manufacturer, vehicle.Model }
                .Where(part => !string.IsNullOrWhiteSpace(part))) is { Length: > 0 } text
                ? text
                : null,
            vehicle.HasAnyFuelSensor,
            vehicle.HasTemperatureProbe,
            vehicle.IsActiveInFleet,
            !vehicle.IsActiveInFleet ? "no longer in the fleet"
                : vehicle.TerminalInRepair ? "tracker in repair"
                : vehicle.IsUnderMaintenance ? "under maintenance"
                : null);
    }
}
