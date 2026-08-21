using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Features.FiscalisationConfiguration.Commands.RegisterFiscalDeviceHandset;

/// <summary>
/// Writes which handset signs as a fiscal device.
/// </summary>
/// <remarks>
/// <para>
/// The device id has lived on the user record since it was added, and User Management can still set it —
/// but as a bare number box that asks the platform nothing. This route exists because the number alone is
/// not enough to know the registration is safe: an Online-mode device belongs to this server, an expired
/// certificate signs nothing, and a device already carried by another account is a forked chain waiting
/// to happen. Every one of those is silent on a handset.
/// </para>
/// <para>
/// Releasing a device is the same call with no handset. It is a separate step from registering rather
/// than a convenience, because the outgoing handset may still be carrying receipts nobody has seen, and
/// that is the moment to say so.
/// </para>
/// </remarks>
public sealed class RegisterFiscalDeviceHandsetHandler(
    ApplicationDbContext db,
    IFiscalisationApiClient client,
    IOptionsMonitor<FiscalisationSettings> settings,
    IMediator mediator,
    ILogger<RegisterFiscalDeviceHandsetHandler> logger)
    : IRequestHandler<RegisterFiscalDeviceHandsetCommand, ErrorOr<FiscalDevicePreviewDto>>
{
    public async Task<ErrorOr<FiscalDevicePreviewDto>> Handle(
        RegisterFiscalDeviceHandsetCommand command,
        CancellationToken cancellationToken)
    {
        if (command.DeviceId <= 0)
        {
            return Error.Validation(
                "FiscalDeviceRegistration.DeviceRequired",
                "A fiscal device id is needed. It is the number ZIMRA registered the device under.");
        }

        var holder = await db.Users
            .Where(user => user.FiscalDeviceId == command.DeviceId)
            .OrderBy(user => user.Username)
            .FirstOrDefaultAsync(cancellationToken);

        var nomination = await db.FiscalDeviceOfflineLeases
            .FirstOrDefaultAsync(row => row.DeviceId == command.DeviceId, cancellationToken);

        // Registering and releasing are separate operations, not two ends of one. A device held by
        // another handset is refused rather than quietly moved: the release is where the outgoing
        // handset's queue is checked, and folding it into the registration would let a device change
        // hands as a side effect of a save aimed at something else.
        if (command.HandsetUserId is { } handsetId)
        {
            var registered = await RegisterAsync(command, handsetId, holder, cancellationToken);
            if (registered.IsError)
            {
                return registered.Errors;
            }
        }
        else
        {
            var release = Release(command, holder, nomination);
            if (release is { } refusal)
            {
                return refusal;
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        return await mediator.Send(
            new Queries.PreviewFiscalDevice.PreviewFiscalDeviceQuery(command.DeviceId, command.HandsetUserId),
            cancellationToken);
    }

    /// <summary>
    /// Takes the device off whoever holds it, refusing when that would strand signed receipts.
    /// </summary>
    /// <remarks>
    /// This is the only way a device leaves a handset, which is what makes it the right place for the
    /// queue check. The outgoing van may be carrying receipts the server has never seen, and once the
    /// device is somewhere else those receipts have no chain to land in.
    ///
    /// The nomination goes with it. A nomination naming a handset that no longer carries the device grants
    /// that handset nothing and refuses every other one, so leaving it behind would switch offline signing
    /// off for the whole device without saying so.
    /// </remarks>
    private ErrorOr<FiscalDevicePreviewDto>? Release(
        RegisterFiscalDeviceHandsetCommand command,
        User? holder,
        FiscalDeviceOfflineLeaseEntity? nomination)
    {
        if (holder is null)
        {
            // Nothing to release. Not an error: the office asked for this device to belong to nobody,
            // and it already does.
            return null;
        }

        var nominated = nomination?.HolderUserId == holder.Id;
        var stillCarrying = nominated && !nomination!.CanHandOver;

        if (stillCarrying && !command.Force)
        {
            return Error.Conflict("FiscalDeviceRegistration.HolderStillCarrying", DescribeRisk(nomination!, holder));
        }

        holder.FiscalDeviceId = null;
        holder.UpdatedAt = DateTime.UtcNow;

        if (nominated)
        {
            ClearNomination(nomination!, command);
        }

        logger.LogWarning(
            "Fiscal device {DeviceId} was released from {Holder} by {Actor}{Forced}.",
            command.DeviceId,
            OfflineSigningLeaseMapper.Label(holder),
            command.ActorName,
            stillCarrying ? " (forced, receipts may be stranded)" : string.Empty);

        return null;
    }

    private async Task<ErrorOr<Success>> RegisterAsync(
        RegisterFiscalDeviceHandsetCommand command,
        Guid handsetId,
        User? outgoingHolder,
        CancellationToken cancellationToken)
    {
        var target = await db.Users.FirstOrDefaultAsync(user => user.Id == handsetId, cancellationToken);

        if (target is null)
        {
            return Error.Validation(
                "FiscalDeviceRegistration.HandsetUnknown",
                "That handset account no longer exists.");
        }

        var current = settings.CurrentValue;
        var (config, platformError) = await ReadConfigAsync(command.DeviceId, current, cancellationToken);

        // A device another handset still carries is refused here, not moved. Releasing it is a separate
        // call, and the reason is that the release is where the outgoing van's queue is checked — see
        // Release. Force does not open this: it releases a lost handset, it does not overwrite a live one.
        var heldByAnother = outgoingHolder is null || outgoingHolder.Id == handsetId
            ? null
            : OfflineSigningLeaseMapper.Label(outgoingHolder);

        var findings = FiscalDeviceRegistration.Inspect(new FiscalDeviceRegistrationInput(
            DeviceId: command.DeviceId,
            PinnedDefaultDeviceId: current.DefaultDeviceId,
            PlatformReachable: config is not null,
            PlatformError: platformError,
            DeviceSerialNo: config?.DeviceSerialNo,
            OperatingMode: config?.DeviceOperatingMode,
            CertificateValidTill: config?.CertificateValidTill,
            CurrentHolderLabel: heldByAnother,
            Target: new FiscalDeviceRegistrationTarget(
                Label: OfflineSigningLeaseMapper.Label(target),
                IsActive: target.IsActive,
                RoleSupportsDevice: ApplicationRoles.SupportsFiscalDevice(target.Role),
                AlreadyHoldsThisDevice: target.FiscalDeviceId == command.DeviceId),
            NowUtc: DateTime.UtcNow));

        var blocker = findings.FirstOrDefault(f => f.Severity == FiscalDeviceRegistrationSeverity.Block);
        if (blocker is not null)
        {
            return Error.Validation($"FiscalDeviceRegistration.{blocker.Code}", blocker.Message);
        }

        target.FiscalDeviceId = command.DeviceId;
        target.UpdatedAt = DateTime.UtcNow;

        logger.LogInformation(
            "Fiscal device {DeviceId} ({Serial}) is now registered to {Handset}, set by {Actor}.",
            command.DeviceId,
            config?.DeviceSerialNo ?? "serial unknown",
            OfflineSigningLeaseMapper.Label(target),
            command.ActorName);

        return Result.Success;
    }

    /// <summary>
    /// Reads the device's configuration, which is what carries the operating mode and the certificate.
    /// </summary>
    /// <remarks>
    /// Uncached on purpose. <see cref="IFiscalDeviceConfigCache"/> holds a device's config for the life of
    /// its certificate, which is right for signing and wrong here: this is the one call whose answer
    /// decides whether a van is handed the device at all, and a stale hit could register a device whose
    /// mode or certificate has since changed.
    /// </remarks>
    private async Task<(FiscalConfigApiResponse? Config, string? Error)> ReadConfigAsync(
        int deviceId,
        FiscalisationSettings current,
        CancellationToken cancellationToken)
    {
        if (!current.Enabled)
        {
            return (null, "Fiscalisation is switched off on this server, so the platform was not asked.");
        }

        if (string.IsNullOrWhiteSpace(current.ApiKey))
        {
            return (null, "No fiscalisation API key is configured, so the platform cannot be asked about this device.");
        }

        try
        {
            return (await client.GetFiscalConfigAsync(deviceId, cancellationToken), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FiscalisationApiException ex)
        {
            logger.LogWarning(ex, "The fiscalisation platform would not describe device {DeviceId}.", deviceId);

            return (null, string.IsNullOrWhiteSpace(ex.ErrorCode) ? ex.Message : $"{ex.ErrorCode}: {ex.Message}");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The fiscalisation platform could not be reached for device {DeviceId}.", deviceId);
            return (null, ex.Message);
        }
    }

    private static void ClearNomination(
        FiscalDeviceOfflineLeaseEntity nomination,
        RegisterFiscalDeviceHandsetCommand command)
    {
        var now = DateTime.UtcNow;

        nomination.HolderUserId = null;
        nomination.HolderLabel = null;
        nomination.AssignedAtUtc = now;
        nomination.AssignedByUserId = command.ActorId;
        nomination.AssignedByName = command.ActorName;
        nomination.HolderPendingSales = null;
        nomination.HolderLastSeenAtUtc = null;
        nomination.ReleasedAtUtc = null;
        nomination.UpdatedAt = now;
    }

    /// <summary>Says what is at stake, in the terms the person clicking can act on.</summary>
    private static string DescribeRisk(FiscalDeviceOfflineLeaseEntity nomination, User holder)
    {
        var label = OfflineSigningLeaseMapper.Label(holder);

        var carrying = nomination.HolderPendingSales switch
        {
            null => $"{label} has not reported whether it is still carrying signed receipts",
            1 => $"{label} is still carrying 1 signed receipt that has not reached the server",
            var count => $"{label} is still carrying {count} signed receipts that have not reached the server"
        };

        return $"{carrying}. Moving this device to another handset now would leave gaps in its receipt "
               + "chain, and ZIMRA refuses a whole fiscal day that has them. Wait for that handset to "
               + "sync, or force the move if it is not coming back.";
    }
}
