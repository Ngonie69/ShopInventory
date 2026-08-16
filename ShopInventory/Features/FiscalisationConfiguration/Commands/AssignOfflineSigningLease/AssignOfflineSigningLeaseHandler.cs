using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.FiscalisationConfiguration.Commands.AssignOfflineSigningLease;

/// <summary>
/// Nominates the one handset allowed to sign receipts offline on a fiscal device.
///
/// The whole point is the refusal in the middle. A fiscal device is a single hash-chained sequence of
/// receipts, so moving offline signing from one van to another while the first is still carrying signed
/// receipts does not split the work — it forks the chain. The next holder is handed a starting position
/// that does not account for the receipts still sitting on the outgoing handset, signs over numbers that
/// are already spent, and FDMS refuses the fiscal day when the file goes up, hours later, with every
/// customer already served.
///
/// So a handover waits for the outgoing handset to report an empty queue. <see cref="AssignOfflineSigningLeaseCommand.Force"/>
/// exists because a handset that is lost or broken will never report anything, and the office still has to
/// be able to put a van back on the road — but it is a decision someone makes, not a default.
/// </summary>
public sealed class AssignOfflineSigningLeaseHandler(
    ApplicationDbContext db,
    ILogger<AssignOfflineSigningLeaseHandler> logger)
    : IRequestHandler<AssignOfflineSigningLeaseCommand, ErrorOr<FiscalDeviceOfflineLeaseDto>>
{
    public async Task<ErrorOr<FiscalDeviceOfflineLeaseDto>> Handle(
        AssignOfflineSigningLeaseCommand command,
        CancellationToken cancellationToken)
    {
        if (command.DeviceId <= 0)
        {
            return Error.Validation(
                "OfflineSigningLease.DeviceRequired",
                "A fiscal device id is needed to nominate a handset for it.");
        }

        User? holder = null;

        if (command.HolderUserId is not null)
        {
            holder = await db.Users
                .FirstOrDefaultAsync(candidate => candidate.Id == command.HolderUserId, cancellationToken);

            if (holder is null || !holder.IsActive)
            {
                return Error.Validation(
                    "OfflineSigningLease.HolderUnknown",
                    "That user is not an active handset user, so it cannot be nominated to sign offline.");
            }

            // Nominating a user whose handset signs as a different device would read as permission and
            // grant nothing: the lease it collects is for the device on its own user record.
            var holderDevice = holder.FiscalDeviceId ?? 0;
            if (holderDevice != command.DeviceId)
            {
                return Error.Validation(
                    "OfflineSigningLease.HolderWrongDevice",
                    holderDevice <= 0
                        ? $"{OfflineSigningLeaseMapper.Label(holder)} is not registered against any fiscal " +
                          "device, so nominating it for this one would grant nothing."
                        : $"{OfflineSigningLeaseMapper.Label(holder)} signs as fiscal device {holderDevice}, " +
                          $"not {command.DeviceId}.");
            }
        }

        var nomination = await db.FiscalDeviceOfflineLeases
            .FirstOrDefaultAsync(row => row.DeviceId == command.DeviceId, cancellationToken);

        if (nomination is null)
        {
            nomination = new FiscalDeviceOfflineLeaseEntity { DeviceId = command.DeviceId };
            db.FiscalDeviceOfflineLeases.Add(nomination);
        }

        var outgoing = nomination.HolderUserId;
        var changingHands = outgoing != command.HolderUserId;

        if (changingHands && outgoing is not null && !nomination.CanHandOver && !command.Force)
        {
            return Error.Conflict("OfflineSigningLease.HolderStillCarrying", DescribeRisk(nomination));
        }

        Apply(nomination, command, holder, resetCheckIn: changingHands);

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Offline signing on fiscal device {DeviceId} is now {Holder}, set by {Actor}{Forced}.",
            command.DeviceId,
            nomination.HolderLabel ?? "nobody",
            command.ActorName,
            command.Force && changingHands ? " (forced)" : string.Empty);

        return OfflineSigningLeaseMapper.ToDto(nomination);
    }

    private static void Apply(
        FiscalDeviceOfflineLeaseEntity nomination,
        AssignOfflineSigningLeaseCommand command,
        User? holder,
        bool resetCheckIn)
    {
        var now = DateTime.UtcNow;

        nomination.HolderUserId = command.HolderUserId;
        nomination.HolderLabel = holder is null ? null : OfflineSigningLeaseMapper.Label(holder);
        nomination.AssignedAtUtc = now;
        nomination.AssignedByUserId = command.ActorId;
        nomination.AssignedByName = command.ActorName;
        nomination.UpdatedAt = now;

        // Only when the device actually changes hands. Re-confirming the same holder must not throw away
        // what it has already told us about its queue, or the next real handover would need forcing for
        // no reason.
        if (resetCheckIn)
        {
            nomination.HolderPendingSales = null;
            nomination.HolderLastSeenAtUtc = null;
            nomination.ReleasedAtUtc = null;
        }
    }

    /// <summary>
    /// Says what is at stake, in the terms the person clicking can act on.
    /// </summary>
    private static string DescribeRisk(FiscalDeviceOfflineLeaseEntity nomination)
    {
        var holder = string.IsNullOrWhiteSpace(nomination.HolderLabel)
            ? "The current handset"
            : nomination.HolderLabel;

        var carrying = nomination.HolderPendingSales switch
        {
            null => $"{holder} has not reported whether it is still carrying signed receipts",
            1 => $"{holder} is still carrying 1 signed receipt that has not reached the server",
            var count => $"{holder} is still carrying {count} signed receipts that have not reached the server"
        };

        return $"{carrying}. Moving offline signing now would leave gaps in this device's receipt chain, " +
               "and ZIMRA refuses a whole fiscal day that has them. Wait for that handset to sync, or " +
               "force the move if it is not coming back.";
    }
}
