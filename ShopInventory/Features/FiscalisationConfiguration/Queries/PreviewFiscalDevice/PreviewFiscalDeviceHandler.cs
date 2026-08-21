using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Features.FiscalisationConfiguration.Queries.PreviewFiscalDevice;

/// <summary>
/// Reads a fiscal device off the platform so the office can see what it is about to hand a van.
/// </summary>
/// <remarks>
/// <para>
/// Answers for device ids this application has never seen, which is what separates it from the
/// Fiscalisation console's device list — that one describes devices already in use, and a device being
/// registered for the first time is by definition not one of them.
/// </para>
/// <para>
/// Nothing is written. The verdict comes from <see cref="FiscalDeviceRegistration"/>, which is where the
/// rules live and where they are tested.
/// </para>
/// </remarks>
public sealed class PreviewFiscalDeviceHandler(
    ApplicationDbContext db,
    IFiscalisationApiClient client,
    IOptionsMonitor<FiscalisationSettings> settings,
    ILogger<PreviewFiscalDeviceHandler> logger)
    : IRequestHandler<PreviewFiscalDeviceQuery, ErrorOr<FiscalDevicePreviewDto>>
{
    public async Task<ErrorOr<FiscalDevicePreviewDto>> Handle(
        PreviewFiscalDeviceQuery query,
        CancellationToken cancellationToken)
    {
        var current = settings.CurrentValue;

        // Whoever already carries this device. Deactivated accounts included: the id ZIMRA registered
        // does not lapse with the account, and the unique index counts them too, so a preview that left
        // them out would promise a registration the save then refuses.
        var holder = await db.Users
            .AsNoTracking()
            .Where(user => user.FiscalDeviceId == query.DeviceId)
            .OrderBy(user => user.Username)
            .FirstOrDefaultAsync(cancellationToken);

        User? target = null;

        if (query.HandsetUserId is { } handsetId)
        {
            target = await db.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(user => user.Id == handsetId, cancellationToken);

            if (target is null)
            {
                return Error.Validation(
                    "FiscalDeviceRegistration.HandsetUnknown",
                    "That handset account no longer exists.");
            }
        }

        var (config, status, platformError) = await ReadPlatformAsync(query.DeviceId, current, cancellationToken);

        var findings = FiscalDeviceRegistration.Inspect(new FiscalDeviceRegistrationInput(
            DeviceId: query.DeviceId,
            PinnedDefaultDeviceId: current.DefaultDeviceId,
            PlatformReachable: config is not null,
            PlatformError: platformError,
            DeviceSerialNo: config?.DeviceSerialNo,
            OperatingMode: config?.DeviceOperatingMode,
            CertificateValidTill: config?.CertificateValidTill,
            CurrentHolderLabel: holder is null ? null : OfflineSigningLeaseMapper.Label(holder),
            Target: target is null
                ? null
                : new FiscalDeviceRegistrationTarget(
                    Label: OfflineSigningLeaseMapper.Label(target),
                    IsActive: target.IsActive,
                    RoleSupportsDevice: ApplicationRoles.SupportsFiscalDevice(target.Role),
                    AlreadyHoldsThisDevice: target.FiscalDeviceId == query.DeviceId),
            NowUtc: DateTime.UtcNow));

        return new FiscalDevicePreviewDto
        {
            DeviceId = query.DeviceId,
            Reachable = config is not null,
            PlatformError = platformError,
            SerialNumber = config?.DeviceSerialNo,
            BranchName = config?.DeviceBranchName,
            OperatingMode = config?.DeviceOperatingMode,
            TaxPayerName = config?.TaxPayerName,
            CertificateValidTill = config?.CertificateValidTill,
            CertificateDaysRemaining = config is null
                ? null
                : (int)Math.Floor((config.CertificateValidTill - DateTime.UtcNow).TotalDays),
            FiscalDayNo = status?.FiscalDayNo,
            FiscalDayStatus = status?.FiscalDayStatus,
            CurrentHolderUserId = holder?.Id,
            CurrentHolderLabel = holder is null ? null : OfflineSigningLeaseMapper.Label(holder),
            PinnedDefaultDeviceId = current.DefaultDeviceId,
            CanRegister = target is not null && !FiscalDeviceRegistration.IsBlocked(findings),
            CanRelease = target is null && holder is not null,
            Findings = findings
                .Select(finding => new FiscalDeviceRegistrationFindingDto
                {
                    Severity = finding.Severity.ToString(),
                    Code = finding.Code,
                    Message = finding.Message
                })
                .ToList()
        };
    }

    /// <summary>
    /// Reads config then status, reporting what failed rather than throwing.
    /// </summary>
    /// <remarks>
    /// Config is the one that decides: without it there is no serial, no operating mode and no
    /// certificate, and <see cref="FiscalDeviceRegistration"/> refuses a device it cannot describe. The
    /// status call is allowed to fail on its own — a device whose day cannot be read is still a device
    /// worth registering, and the day is reported empty rather than the whole preview failing.
    /// </remarks>
    private async Task<(FiscalConfigApiResponse? Config, FiscalStatusApiResponse? Status, string? Error)> ReadPlatformAsync(
        int deviceId,
        FiscalisationSettings current,
        CancellationToken cancellationToken)
    {
        if (deviceId <= 0)
        {
            return (null, null, null);
        }

        if (!current.Enabled)
        {
            return (null, null, "Fiscalisation is switched off on this server, so the platform was not asked.");
        }

        if (string.IsNullOrWhiteSpace(current.ApiKey))
        {
            return (null, null, "No fiscalisation API key is configured, so the platform cannot be asked about this device.");
        }

        FiscalConfigApiResponse? config;

        try
        {
            config = await client.GetFiscalConfigAsync(deviceId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The fiscalisation platform would not describe device {DeviceId}.", deviceId);
            return (null, null, Describe(ex));
        }

        try
        {
            var status = await client.GetFiscalStatusAsync(deviceId, cancellationToken);
            return (config, status, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "The fiscalisation platform would not report the fiscal day for device {DeviceId}.", deviceId);

            return (config, null, $"The device answered, but its fiscal day did not: {Describe(ex)}");
        }
    }

    private static string Describe(Exception exception) => exception switch
    {
        FiscalisationApiException api when !string.IsNullOrWhiteSpace(api.ErrorCode) =>
            $"{api.ErrorCode}: {api.Message}",
        FiscalisationApiException api => api.Message,
        _ => exception.Message
    };
}
