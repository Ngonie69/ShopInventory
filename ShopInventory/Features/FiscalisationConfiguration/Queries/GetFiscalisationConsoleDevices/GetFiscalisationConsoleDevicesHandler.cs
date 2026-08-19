using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Features.FiscalisationConfiguration.Queries.GetFiscalisationConsoleDevices;

/// <summary>
/// Assembles the console's device section.
/// </summary>
/// <remarks>
/// The device list is built from what this application has actually seen — handsets registered against a
/// device, nominations, receipts, fiscal days — rather than asked of the platform, which has no
/// "list devices" route on the API-key surface. The configured default device is added on top so a
/// freshly installed system still shows the one device it will fiscalise on.
///
/// A device the platform will not answer for is still returned, with the reason. Dropping it would make
/// an outage look like a shorter fleet, which is the failure mode this whole page exists to prevent.
/// </remarks>
public sealed class GetFiscalisationConsoleDevicesHandler(
    ApplicationDbContext db,
    IFiscalisationApiClient client,
    IOptionsMonitor<FiscalisationSettings> settings,
    ILogger<GetFiscalisationConsoleDevicesHandler> logger
) : IRequestHandler<GetFiscalisationConsoleDevicesQuery, ErrorOr<List<FiscalConsoleDeviceDto>>>
{
    public async Task<ErrorOr<List<FiscalConsoleDeviceDto>>> Handle(
        GetFiscalisationConsoleDevicesQuery query,
        CancellationToken cancellationToken)
    {
        var current = settings.CurrentValue;
        var deviceIds = await CollectDeviceIdsAsync(current.DefaultDeviceId, cancellationToken);

        if (deviceIds.Count == 0)
        {
            return new List<FiscalConsoleDeviceDto>();
        }

        var leases = await db.FiscalDeviceOfflineLeases
            .AsNoTracking()
            .Where(lease => deviceIds.Contains(lease.DeviceId))
            .ToDictionaryAsync(lease => lease.DeviceId, cancellationToken);

        var receiptCounts = await CountReceiptsByStatusAsync(deviceIds, cancellationToken);
        var chainBreaks = await FindChainBreaksAsync(deviceIds, cancellationToken);

        var devices = new List<FiscalConsoleDeviceDto>(deviceIds.Count);

        foreach (var deviceId in deviceIds)
        {
            var (config, status, platformError) = await ReadPlatformAsync(deviceId, current, cancellationToken);

            leases.TryGetValue(deviceId, out var lease);
            chainBreaks.TryGetValue(deviceId, out var chainBreak);

            devices.Add(BuildDevice(deviceId, config, status, platformError, lease, receiptCounts, chainBreak));
        }

        return devices;
    }

    /// <summary>
    /// Every device id this application has a reason to know about, in ascending order.
    /// </summary>
    /// <remarks>
    /// The receipt scan is restricted to rows that carry a fiscal chain. Every sale ever made shares
    /// this table, and the great majority are <see cref="DesktopSaleReceiptIngestStatus.NotApplicable"/>
    /// with no device at all; the restriction is also what lets the scan lead with the composite index's
    /// first column instead of walking the table.
    /// </remarks>
    private async Task<List<int>> CollectDeviceIdsAsync(int defaultDeviceId, CancellationToken cancellationToken)
    {
        // Inactive handsets included, unlike the offline signing overview. That screen is nominating
        // someone and so wants people who can be nominated; this one is listing devices, and a device
        // whose only handset was deactivated still exists, still has a chain, and may still be holding
        // receipts nobody has handed over.
        var fromHandsets = await db.Users
            .AsNoTracking()
            .Where(user => user.FiscalDeviceId != null && user.FiscalDeviceId > 0)
            .Select(user => user.FiscalDeviceId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

        var fromLeases = await db.FiscalDeviceOfflineLeases
            .AsNoTracking()
            .Select(lease => lease.DeviceId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var fromReceipts = await db.DesktopSales
            .AsNoTracking()
            .Where(sale =>
                sale.ReceiptIngestStatus != DesktopSaleReceiptIngestStatus.NotApplicable &&
                sale.FiscalDeviceId != null &&
                sale.FiscalDeviceId > 0)
            .Select(sale => sale.FiscalDeviceId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

        var fromFiscalDays = await db.FiscalDayStates
            .AsNoTracking()
            .Select(day => day.DeviceId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var deviceIds = fromHandsets
            .Concat(fromLeases)
            .Concat(fromReceipts)
            .Concat(fromFiscalDays)
            .ToHashSet();

        // 0 means "whichever device the platform is set up on", which is a submission instruction rather
        // than a device this console can describe.
        if (defaultDeviceId > 0)
        {
            deviceIds.Add(defaultDeviceId);
        }

        return deviceIds.Order().ToList();
    }

    private async Task<Dictionary<(int DeviceId, DesktopSaleReceiptIngestStatus Status), int>> CountReceiptsByStatusAsync(
        List<int> deviceIds,
        CancellationToken cancellationToken)
    {
        var counts = await db.DesktopSales
            .AsNoTracking()
            .Where(sale =>
                sale.ReceiptIngestStatus != DesktopSaleReceiptIngestStatus.NotApplicable &&
                sale.ReceiptIngestStatus != DesktopSaleReceiptIngestStatus.Ingested &&
                sale.FiscalDeviceId != null &&
                deviceIds.Contains(sale.FiscalDeviceId.Value))
            .GroupBy(sale => new { DeviceId = sale.FiscalDeviceId!.Value, sale.ReceiptIngestStatus })
            .Select(group => new
            {
                group.Key.DeviceId,
                group.Key.ReceiptIngestStatus,
                Count = group.Count()
            })
            .ToListAsync(cancellationToken);

        return counts.ToDictionary(row => (row.DeviceId, row.ReceiptIngestStatus), row => row.Count);
    }

    /// <summary>
    /// The first break in each device's chain, and how many of its receipts are stuck behind it.
    /// </summary>
    /// <remarks>
    /// The first one, by receipt number, is the only one worth naming. A break stops the sequence, so the
    /// breaks after it are consequences of this one and reporting them as separate faults would multiply
    /// one stopped van into a screen of them.
    /// </remarks>
    private async Task<Dictionary<int, ChainBreak>> FindChainBreaksAsync(
        List<int> deviceIds,
        CancellationToken cancellationToken)
    {
        var broken = await db.DesktopSales
            .AsNoTracking()
            .Where(sale =>
                sale.ReceiptIngestStatus == DesktopSaleReceiptIngestStatus.ChainBroken &&
                sale.FiscalDeviceId != null &&
                deviceIds.Contains(sale.FiscalDeviceId.Value))
            .Select(sale => new
            {
                DeviceId = sale.FiscalDeviceId!.Value,
                sale.ReceiptGlobalNo,
                sale.ReceiptIngestError
            })
            .ToListAsync(cancellationToken);

        if (broken.Count == 0)
        {
            return [];
        }

        var firstBreaks = broken
            .GroupBy(row => row.DeviceId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(row => row.ReceiptGlobalNo ?? int.MaxValue)
                    .First());

        // A List rather than the dictionary's key collection: EF translates List.Contains to an IN
        // clause and has no such translation for a KeyCollection, which would fall back to evaluating
        // the predicate over every row of the table.
        var brokenDeviceIds = firstBreaks.Keys.ToList();

        var blocked = await db.DesktopSales
            .AsNoTracking()
            .Where(sale =>
                sale.ReceiptIngestStatus == DesktopSaleReceiptIngestStatus.Pending &&
                sale.FiscalDeviceId != null &&
                brokenDeviceIds.Contains(sale.FiscalDeviceId.Value))
            .Select(sale => new { DeviceId = sale.FiscalDeviceId!.Value, sale.ReceiptGlobalNo })
            .ToListAsync(cancellationToken);

        return firstBreaks.ToDictionary(
            entry => entry.Key,
            entry => new ChainBreak(
                entry.Value.ReceiptGlobalNo,
                entry.Value.ReceiptIngestError,
                blocked.Count(row =>
                    row.DeviceId == entry.Key &&
                    row.ReceiptGlobalNo > (entry.Value.ReceiptGlobalNo ?? int.MinValue))));
    }

    /// <summary>
    /// Asks the platform for the device's configuration and its live day, tolerating either failing.
    /// </summary>
    private async Task<(FiscalConfigApiResponse? Config, FiscalStatusApiResponse? Status, string? Error)> ReadPlatformAsync(
        int deviceId,
        FiscalisationSettings current,
        CancellationToken cancellationToken)
    {
        if (!current.Enabled)
        {
            return (null, null, "Fiscalisation is switched off, so the platform was not asked about this device.");
        }

        if (string.IsNullOrWhiteSpace(current.ApiKey))
        {
            return (null, null, "No fiscalisation API key is configured, so the platform cannot be asked about this device.");
        }

        FiscalConfigApiResponse? config = null;
        FiscalStatusApiResponse? status = null;
        string? error = null;

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
            error = Describe(ex);
            logger.LogWarning(ex, "The fiscalisation platform would not describe device {DeviceId}.", deviceId);
            return (null, null, error);
        }

        try
        {
            status = await client.GetFiscalStatusAsync(deviceId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The device is described but its day is not, which is worth saying rather than leaving the
            // day columns blank as though the device had never opened one.
            error = $"The device answered, but its fiscal day did not: {Describe(ex)}";
            logger.LogWarning(ex, "The fiscalisation platform would not report the fiscal day for device {DeviceId}.", deviceId);
        }

        return (config, status, error);
    }

    private static string Describe(Exception exception) => exception switch
    {
        FiscalisationApiException api when !string.IsNullOrWhiteSpace(api.ErrorCode) =>
            $"{api.ErrorCode}: {api.Message}",
        FiscalisationApiException api => api.Message,
        _ => exception.Message
    };

    private static FiscalConsoleDeviceDto BuildDevice(
        int deviceId,
        FiscalConfigApiResponse? config,
        FiscalStatusApiResponse? status,
        string? platformError,
        FiscalDeviceOfflineLeaseEntity? lease,
        Dictionary<(int DeviceId, DesktopSaleReceiptIngestStatus Status), int> receiptCounts,
        ChainBreak? chainBreak)
    {
        var (hoursElapsed, percentOfMax) = MeasureFiscalDay(status?.FiscalDayOpened, config?.TaxPayerDayMaxHrs);

        return new FiscalConsoleDeviceDto(
            DeviceId: deviceId,
            Reachable: config is not null,
            PlatformError: platformError,
            SerialNumber: config?.DeviceSerialNo,
            BranchName: config?.DeviceBranchName,
            OperatingMode: config?.DeviceOperatingMode,
            CertificateValidTill: config?.CertificateValidTill,
            CertificateDaysRemaining: config is null
                ? null
                : (int)Math.Floor((config.CertificateValidTill - DateTime.UtcNow).TotalDays),
            TaxPayerDayMaxHrs: config?.TaxPayerDayMaxHrs,
            FiscalDayNo: status?.FiscalDayNo,
            FiscalDayStatus: status?.FiscalDayStatus,
            FiscalDayOpened: status?.FiscalDayOpened,
            FiscalDayHoursElapsed: hoursElapsed,
            FiscalDayPercentOfMax: percentOfMax,
            LastReceiptDate: status?.LastReceiptDate,
            LastReceiptGlobalNo: status?.LastReceiptGlobalNo,
            LastReceiptCounter: status?.LastReceiptCounter,
            OfflineSigningHolder: lease?.HolderUserId is null ? null : lease.HolderLabel,
            OfflineSigningHolderPendingSales: lease?.HolderPendingSales,
            OfflineSigningHolderLastSeenAtUtc: lease?.HolderLastSeenAtUtc,
            AwaitingHandover: Count(receiptCounts, deviceId, DesktopSaleReceiptIngestStatus.Pending),
            FailedHandover: Count(receiptCounts, deviceId, DesktopSaleReceiptIngestStatus.Failed),
            Unsignable: Count(receiptCounts, deviceId, DesktopSaleReceiptIngestStatus.Unsignable),
            Unstamped: Count(receiptCounts, deviceId, DesktopSaleReceiptIngestStatus.Unstamped),
            ChainBroken: chainBreak is not null,
            ChainBrokenAtReceiptGlobalNo: chainBreak?.ReceiptGlobalNo,
            ChainBrokenError: chainBreak?.Error,
            BlockedBehindChainBreak: chainBreak?.BlockedBehind ?? 0);
    }

    /// <summary>
    /// How long the fiscal day has been open, in the taxpayer's own clock.
    /// </summary>
    /// <remarks>
    /// Measured in CAT because that is the clock the limit is expressed in and the one the device opened
    /// the day against. Converting either side moves the deadline.
    /// </remarks>
    private static (double? Hours, int? PercentOfMax) MeasureFiscalDay(DateTime? openedAtLocal, int? maxHours)
    {
        if (openedAtLocal is null)
        {
            return (null, null);
        }

        var elapsed = (AuditService.ToCAT(DateTime.UtcNow) - DateTime.SpecifyKind(openedAtLocal.Value, DateTimeKind.Unspecified))
            .TotalHours;

        if (elapsed < 0)
        {
            elapsed = 0;
        }

        var hours = Math.Round(elapsed, 1);

        return maxHours is null or <= 0
            ? (hours, null)
            : (hours, (int)Math.Round(elapsed / maxHours.Value * 100));
    }

    private static int Count(
        Dictionary<(int DeviceId, DesktopSaleReceiptIngestStatus Status), int> counts,
        int deviceId,
        DesktopSaleReceiptIngestStatus status)
        => counts.TryGetValue((deviceId, status), out var count) ? count : 0;

    private sealed record ChainBreak(int? ReceiptGlobalNo, string? Error, int BlockedBehind);
}
