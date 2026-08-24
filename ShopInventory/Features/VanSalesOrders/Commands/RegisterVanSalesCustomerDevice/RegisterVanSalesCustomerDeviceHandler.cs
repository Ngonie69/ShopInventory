using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

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
            return Errors.VanSalesCustomerAuth.AccountInactive;
        }

        var token = command.DeviceToken!.Trim();
        var now = DateTime.UtcNow;

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

        return Result.Success;
    }
}
