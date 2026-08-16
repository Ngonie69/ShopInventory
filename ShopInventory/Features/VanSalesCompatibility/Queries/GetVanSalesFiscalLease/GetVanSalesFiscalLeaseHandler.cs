using System.Globalization;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Features.VanSalesCompatibility.Queries.GetVanSalesFiscalLease;

/// <summary>
/// Issues a van handset the fiscal lease it needs to sign its own receipts while offline.
///
/// The handset cannot ask anything once it loses signal, so this reply has to carry every fact a receipt
/// depends on: who it signs as, which day it signs into, the number to start from, and the tax on every
/// item it might sell. What it cannot carry is certainty about the future — the day may close, the
/// certificate may expire — so the handset re-checks all of that against its own clock before each sale.
///
/// Where a fact is missing, this handler leaves it out rather than filling it in. An item with no VAT
/// group, or a group with no tax id for this taxpayer, is simply absent from
/// <see cref="VanSalesFiscalLeaseDto.ItemTaxes"/>, and the handset then refuses to sell it offline by
/// name. A plausible default here would instead print a receipt with the wrong tax on it, which nothing
/// downstream can detect and no one finds until the fiscal day's file is rejected.
/// </summary>
public sealed class GetVanSalesFiscalLeaseHandler(
    ApplicationDbContext db,
    IFiscalDeviceConfigCache configCache,
    IFiscalisationApiClient fiscalisationClient,
    ISAPServiceLayerClient sapClient,
    IOptions<FiscalisationSettings> fiscalisationOptions,
    ILogger<GetVanSalesFiscalLeaseHandler> logger
) : IRequestHandler<GetVanSalesFiscalLeaseQuery, ErrorOr<VanSalesFiscalLeaseDto>>
{
    private const string FiscalDayOpenedStatus = "FiscalDayOpened";

    /// <summary>Local wall clock, no offset — see <see cref="VanSalesFiscalLeaseDto.FiscalDayOpenedAt"/>.</summary>
    private const string LocalDateFormat = "yyyy-MM-dd'T'HH:mm:ss";

    public async Task<ErrorOr<VanSalesFiscalLeaseDto>> Handle(
        GetVanSalesFiscalLeaseQuery query,
        CancellationToken cancellationToken)
    {
        var settings = fiscalisationOptions.Value;

        if (!settings.Enabled)
        {
            return Error.Failure(
                "VanSalesCompatibility.FiscalisationDisabled",
                "Fiscalisation is switched off on this server, so no offline lease can be issued.");
        }

        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == query.UserId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            return Error.Unauthorized("VanSalesCompatibility.Unauthenticated", "User is not authenticated.");
        }

        var deviceId = user.FiscalDeviceId ?? 0;
        if (deviceId <= 0)
        {
            // Not an error state so much as an un-provisioned one: this handset has never been registered
            // with ZIMRA, so there is no chain for it to sign into.
            return Error.Validation(
                "VanSalesCompatibility.NoFiscalDevice",
                "This user's handset is not registered as a fiscal device, so it cannot trade offline.");
        }

        // Only the nominated handset may sign on this device's chain, and it is settled before the
        // platform is troubled for config or status: a handset that may not sign has no use for either.
        var nomination = await db.FiscalDeviceOfflineLeases
            .FirstOrDefaultAsync(row => row.DeviceId == deviceId, cancellationToken);

        if (nomination?.HolderUserId is null)
        {
            return Error.Validation(
                "VanSalesCompatibility.OfflineLeaseUnassigned",
                "No handset is nominated to sign offline on this fiscal device. The office gives it to " +
                "one van at a time.");
        }

        if (nomination.HolderUserId != query.UserId)
        {
            var holder = string.IsNullOrWhiteSpace(nomination.HolderLabel)
                ? "Another handset"
                : nomination.HolderLabel;

            return Error.Validation(
                "VanSalesCompatibility.OfflineLeaseHeldElsewhere",
                $"{holder} is signing offline on this fiscal device today, and only one handset can. Ask " +
                "the office to move it before driving out of coverage.");
        }

        // The holder checking in. Written from a query, which is not the pattern — but what it records is
        // the handset's own queue depth, and this is the only moment the server hears it. Recorded before
        // the platform calls below so a check-in still lands when the platform is unreachable.
        RecordHolderCheckIn(nomination, query.PendingSales);
        await db.SaveChangesAsync(cancellationToken);

        var config = await configCache.TryGetAsync(deviceId, cancellationToken);
        if (config is null)
        {
            return Error.Failure(
                "VanSalesCompatibility.FiscalConfigUnavailable",
                $"The fiscal configuration for device {deviceId} could not be read.");
        }

        FiscalStatusApiResponse status;

        try
        {
            status = await fiscalisationClient.GetFiscalStatusAsync(deviceId, cancellationToken);
        }
        catch (FiscalisationApiException ex)
        {
            logger.LogWarning(
                ex,
                "Could not read fiscal status for device {DeviceId} while issuing a van sales lease.",
                deviceId);

            return Error.Failure(
                "VanSalesCompatibility.FiscalStatusUnavailable",
                $"The fiscal day status for device {deviceId} could not be read: {ex.Message}");
        }

        var taxes = VanSalesFiscalLeaseMapper.BuildTaxes(config, DateTime.Now);
        var itemTaxes = await BuildItemTaxesAsync(settings, taxes, cancellationToken);

        var lease = new VanSalesFiscalLeaseDto
        {
            DeviceId = deviceId,
            DeviceSerialNo = config.DeviceSerialNo,
            QrUrl = config.QrUrl,

            FiscalDayNo = status.FiscalDayNo,
            FiscalDayOpenedAt = FormatLocal(status.FiscalDayOpened),
            FiscalDayOpen = string.Equals(
                status.FiscalDayStatus?.Trim(),
                FiscalDayOpenedStatus,
                StringComparison.OrdinalIgnoreCase),
            TaxPayerDayMaxHrs = config.TaxPayerDayMaxHrs,
            CertificateValidTill = FormatLocal(config.CertificateValidTill),

            // The lease hands over the next free position, not the last used one. The handset signs from
            // here and never asks again until it reconnects, so an off-by-one is a duplicated receipt
            // number rather than a cosmetic slip.
            NextGlobalNo = status.LastReceiptGlobalNo + 1,
            NextCounter = status.LastReceiptCounter + 1,

            Taxes = taxes,
            ItemTaxes = itemTaxes
        };

        logger.LogInformation(
            "Issued a fiscal lease for device {DeviceId} to user {UserId}: day {FiscalDayNo} " +
            "({DayStatus}), next receipt {NextGlobalNo}/{NextCounter}, {TaxCount} taxes, {ItemCount} items.",
            deviceId,
            query.UserId,
            lease.FiscalDayNo,
            status.FiscalDayStatus,
            lease.NextGlobalNo,
            lease.NextCounter,
            lease.Taxes.Count,
            lease.ItemTaxes.Count);

        return lease;
    }

    /// <summary>
    /// Notes that the holder has signal and how much it is still carrying.
    /// </summary>
    /// <remarks>
    /// <paramref name="pendingSales"/> being null is recorded as null and not as zero. The difference
    /// decides whether the office can hand this device to another van without stranding signed receipts,
    /// and a handset that never said anything has not said there is nothing.
    ///
    /// The release stamp is cleared whenever the queue is not empty, so it always means "was clear at this
    /// moment" rather than "was clear once".
    /// </remarks>
    private static void RecordHolderCheckIn(FiscalDeviceOfflineLeaseEntity nomination, int? pendingSales)
    {
        var now = DateTime.UtcNow;

        nomination.HolderLastSeenAtUtc = now;
        nomination.HolderPendingSales = pendingSales;
        nomination.ReleasedAtUtc = pendingSales == 0 ? now : null;
        nomination.UpdatedAt = now;
    }

    /// <summary>
    /// Reads SAP's VAT groups and hands them to <see cref="VanSalesFiscalLeaseMapper"/>.
    /// </summary>
    private async Task<List<VanSalesFiscalItemTaxDto>> BuildItemTaxesAsync(
        FiscalisationSettings settings,
        List<VanSalesFiscalTaxDto> taxes,
        CancellationToken cancellationToken)
    {
        Dictionary<string, string> vatGroupsByItem;

        try
        {
            vatGroupsByItem = await sapClient.GetItemVatGroupsAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A lease with no item map is still worth issuing: the handset keeps whatever map it already
            // holds, and refuses anything it cannot account for. Failing the whole call would instead
            // leave a van that has signal right now with nothing at all.
            logger.LogWarning(ex, "Could not read item VAT groups from SAP while issuing a fiscal lease.");
            return [];
        }

        var itemTaxes = VanSalesFiscalLeaseMapper.BuildItemTaxes(
            vatGroupsByItem, settings, taxes, out var unmappedGroups);

        if (unmappedGroups.Count > 0)
        {
            logger.LogWarning(
                "Left {UnmappedCount} VAT group(s) out of the fiscal lease because no FDMS tax id " +
                "resolves for them: {Groups}. Items in those groups cannot be sold offline.",
                unmappedGroups.Count,
                string.Join(", ", unmappedGroups.Order()));
        }

        return itemTaxes;
    }

    private static string? FormatLocal(DateTime? value) =>
        value is null || value.Value == default
            ? null
            : value.Value.ToString(LocalDateFormat, CultureInfo.InvariantCulture);
}
