using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Features.VanSalesCustomerAuth;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesOrders.Commands.RegisterVanSalesCustomerDevice;

/// <summary>
/// Records a handset's push token against the signed-in customer.
/// </summary>
/// <remarks>
/// Keyed on the token, which is what Firebase actually addresses. A token that turns up against a
/// different account has moved — a shared handset, or a shop sold — and is reassigned rather than
/// duplicated, because leaving the old row would send the new owner's order updates to the previous
/// customer's app and the previous customer's to nobody.
/// </remarks>
public sealed class RegisterVanSalesCustomerDeviceHandler(
    ApplicationDbContext context,
    IAuditService auditService,
    ILogger<RegisterVanSalesCustomerDeviceHandler> logger)
    : IRequestHandler<RegisterVanSalesCustomerDeviceCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        RegisterVanSalesCustomerDeviceCommand command,
        CancellationToken cancellationToken)
    {
        var accountExists = await context.VanSalesCustomerAccounts
            .AsNoTracking()
            .AnyAsync(a => a.Id == command.AccountId && a.IsActive, cancellationToken);

        if (!accountExists)
        {
            await VanSalesCustomerAuditTrail.RecordAsync(auditService, new VanSalesCustomerAuditRow(
                AuditActions.RegisterVanSalesCustomerDevice,
                null,
                command.AccountId,
                "Refused a push device registration: the account is inactive or gone.",
                Success: false,
                Errors.VanSalesCustomerAuth.AccountInactive.Description));

            return Errors.VanSalesCustomerAuth.AccountInactive;
        }

        var token = command.DeviceToken!.Trim();
        var now = DateTime.UtcNow;

        // Held so the row can say the handset changed hands. Afterwards the device row shows only
        // where it ended up, and a shop sold or a handset passed on looks the same as a re-register.
        int? movedFrom = null;

        var existing = await context.VanSalesCustomerDevices
            .FirstOrDefaultAsync(d => d.DeviceToken == token, cancellationToken);

        if (existing is null)
        {
            context.VanSalesCustomerDevices.Add(new VanSalesCustomerDeviceEntity
            {
                VanSalesCustomerAccountId = command.AccountId,
                DeviceToken = token,
                DeviceId = command.DeviceId,
                DeviceName = command.DeviceName,
                AppVersion = command.AppVersion,
                RegisteredAt = now,
                LastActiveAt = now
            });
        }
        else
        {
            movedFrom = existing.VanSalesCustomerAccountId == command.AccountId
                ? null
                : existing.VanSalesCustomerAccountId;

            existing.VanSalesCustomerAccountId = command.AccountId;
            existing.DeviceId = command.DeviceId ?? existing.DeviceId;
            existing.DeviceName = command.DeviceName ?? existing.DeviceName;
            existing.AppVersion = command.AppVersion ?? existing.AppVersion;
            existing.LastActiveAt = now;

            // Re-registering revives a token that was revoked on sign-out. The handset is telling
            // us it is live again, which is better evidence than the flag.
            existing.IsRevoked = false;
        }

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Registered a push device for van sales customer account {AccountId}.",
            command.AccountId);

        await VanSalesCustomerAuditTrail.RecordAsync(auditService, new VanSalesCustomerAuditRow(
            AuditActions.RegisterVanSalesCustomerDevice,
            null,
            command.AccountId,
            movedFrom is { } previous
                ? $"Registered push device {command.DeviceName ?? command.DeviceId ?? "(unnamed)"}, "
                    + $"taken over from account {previous}."
                : $"Registered push device {command.DeviceName ?? command.DeviceId ?? "(unnamed)"}.",
            Success: true));

        return Result.Success;
    }
}
