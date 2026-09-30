using System.Globalization;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Components;

/// <summary>Where the fiscalisation console's band takes its figures from.</summary>
public enum FiscalBandSource
{
    /// <summary>The provider could not be read, so neither system's figures are shown as current.</summary>
    Unknown,

    /// <summary>REVMax is the live provider: its device and this system's filing log on it.</summary>
    Revmax,

    /// <summary>The in-house platform is the live provider: its devices, as they answer now.</summary>
    Platform
}

/// <summary>One figure in the band: a label, the figure, and the line under it.</summary>
public sealed record FiscalBandFact(string Label, string Value, string Note);

/// <summary>The device line under the band's figures.</summary>
/// <param name="Name">The device, or the platform's devices, by id.</param>
/// <param name="Serial">A serial, where one device is named; null otherwise.</param>
/// <param name="Detail">The taxpayer under REVMax, the branches under the platform.</param>
/// <param name="Status">The chip's text: the fiscal day, or the worst thing about the devices.</param>
/// <param name="Family">A Nocturne family for the status chip: <c>good</c>, <c>warn</c> or <c>bad</c>.</param>
/// <param name="LinksToDevices">True to link to the Devices panel, false to link to Fiscal days.</param>
public sealed record FiscalBandDevice(
    string Name,
    string? Serial,
    string Detail,
    string Status,
    string Family,
    bool LinksToDevices);

/// <summary>Everything the band's right-hand pane draws.</summary>
public sealed record FiscalConsoleBandView(
    FiscalBandSource Source,
    IReadOnlyList<FiscalBandFact> Facts,
    FiscalBandDevice? Device);

/// <summary>
/// Decides what the fiscalisation console's band says about filing, and on whose authority.
/// </summary>
/// <remarks>
/// The band sits above every tab, so whatever it names reads as where receipts are going now. Since the
/// cut-over on 2026-09-30 that is the platform, and a band still naming REVMax device 22862 and its last
/// filing date read as though REVMax were the live path. So the band follows the configured provider:
/// under REVMax it shows REVMax, under the platform it shows the platform's own devices, and REVMax's
/// figures stay in Compliance evidence as history.
///
/// The platform has no totals route. What it does answer, per device, is its fiscal day, the counter of
/// receipts on that day and the date of its last receipt — so under the platform the band reports those,
/// read from the same device list the Devices panel draws, rather than a 30-day count from this system's
/// own log that would be a different measurement under a similar label.
///
/// Out of the page so it can be tested. It decides which system the page says is live, which is the one
/// thing on the band that must not be wrong.
/// </remarks>
public static class FiscalConsoleBand
{
    /// <summary>ZIMRA's word for an open fiscal day, as the platform reports it.</summary>
    private const string PlatformDayOpen = "FiscalDayOpened";

    /// <summary>Device ids named one by one before the line switches to a count.</summary>
    private const int NamedDeviceLimit = 4;

    /// <summary>Which system is live, as the API reported it with the REVMax read.</summary>
    /// <remarks>
    /// Null is unknown rather than either provider. Guessing REVMax is the mistake this class exists to
    /// stop, and guessing the platform would be the same mistake the other way round.
    /// </remarks>
    public static FiscalBandSource SourceOf(RevmaxActivityResponse? revmax) => revmax switch
    {
        null => FiscalBandSource.Unknown,
        { IsLiveProvider: true } => FiscalBandSource.Revmax,
        _ => FiscalBandSource.Platform
    };

    /// <param name="revmax">The REVMax read, which also carries the configured provider. Null when unread.</param>
    /// <param name="revmaxPending">Whether that read is still under way, so null means "reading", not "failed".</param>
    /// <param name="devices">The platform devices. Null when unread — never an empty list standing in for one.</param>
    /// <param name="devicesPending">Whether a device read is under way, so null means "reading", not "failed".</param>
    /// <param name="days">The fiscal day lifecycle, or null when it could not be read.</param>
    /// <param name="nowLocal">The reader's clock, for "today".</param>
    public static FiscalConsoleBandView Build(
        RevmaxActivityResponse? revmax,
        bool revmaxPending,
        IReadOnlyList<FiscalConsoleDeviceResponse>? devices,
        bool devicesPending,
        FiscalDayStateListResponse? days,
        DateTime nowLocal)
    {
        var source = SourceOf(revmax);

        return source switch
        {
            FiscalBandSource.Revmax => BuildRevmax(revmax!, days, nowLocal),
            FiscalBandSource.Platform => BuildPlatform(devices, devicesPending, days, nowLocal),
            _ => Unknown(revmaxPending ? "reading the provider…" : "provider not read", days)
        };
    }

    private static FiscalConsoleBandView Unknown(string note, FiscalDayStateListResponse? days) =>
        new(
            FiscalBandSource.Unknown,
            [
                new FiscalBandFact("Receipts filed", "—", note),
                FiscalDays(days),
                new FiscalBandFact("Last filed", "—", note)
            ],
            null);

    private static FiscalConsoleBandView BuildRevmax(
        RevmaxActivityResponse revmax,
        FiscalDayStateListResponse? days,
        DateTime nowLocal)
    {
        var totals = revmax.Totals;

        var facts = new[]
        {
            new FiscalBandFact(
                "Receipts filed",
                totals.ReceiptsFiled.ToString("N0", CultureInfo.CurrentCulture),
                totals.FirstReceiptGlobalNo is null
                    ? "no receipt numbers in this window"
                    : $"no. {totals.FirstReceiptGlobalNo} to {totals.LastReceiptGlobalNo}"),
            new FiscalBandFact(
                "Documents filed",
                totals.DocumentsFiled.ToString("N0", CultureInfo.CurrentCulture),
                "counted once each"),
            FiscalDays(days),
            new FiscalBandFact(
                "Last filed",
                totals.LastAtUtc is null ? "—" : totals.LastAtUtc.Value.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture),
                totals.LastAtUtc is null ? "nothing filed in this window" : Day(totals.LastAtUtc.Value.ToLocalTime(), nowLocal))
        };

        FiscalBandDevice? device = null;

        if (revmax.Device is { } card)
        {
            var status = string.IsNullOrWhiteSpace(card.FiscalDayStatus) ? "Day unknown" : card.FiscalDayStatus;

            if (card.LastFiscalDayNo is { } dayNo)
            {
                status = $"{status} {dayNo}";
            }

            var detail = string.IsNullOrWhiteSpace(card.CompanyName) ? "taxpayer unstated" : card.CompanyName;

            if (!string.IsNullOrWhiteSpace(card.Tin))
            {
                detail = $"{detail} · TIN {card.Tin}";
            }

            // Matched on the device's own text: anything not recognisably open is drawn as not open, and
            // the amber that results is a prompt to look — the right outcome for a status nobody expected.
            var open = card.FiscalDayStatus?.Contains("Open", StringComparison.OrdinalIgnoreCase) == true;

            device = new FiscalBandDevice(
                $"Device {card.DeviceId ?? "—"}",
                string.IsNullOrWhiteSpace(card.SerialNumber) ? null : card.SerialNumber,
                detail,
                status,
                open ? "good" : "warn",
                LinksToDevices: false);
        }

        return new FiscalConsoleBandView(FiscalBandSource.Revmax, facts, device);
    }

    private static FiscalConsoleBandView BuildPlatform(
        IReadOnlyList<FiscalConsoleDeviceResponse>? devices,
        bool pending,
        FiscalDayStateListResponse? days,
        DateTime nowLocal)
    {
        if (devices is null)
        {
            // Not read yet, or the read failed. Either way nothing is known about the devices, and an
            // empty list drawn as "no platform device known" would be a confident wrong answer. A list
            // read earlier is still drawn while a refresh runs, as the REVMax figures always were.
            var note = pending ? "reading the platform…" : "device list not read";

            return new FiscalConsoleBandView(
                FiscalBandSource.Platform,
                [
                    new FiscalBandFact("Receipts this fiscal day", "—", note),
                    new FiscalBandFact("Devices answering", "—", note),
                    FiscalDays(days),
                    new FiscalBandFact("Last receipt", "—", note)
                ],
                null);
        }

        var answering = devices.Where(device => device.Reachable).ToList();
        var openDays = answering.Where(IsPlatformDayOpen).ToList();

        var receipts = devices.Count == 0 || answering.Count == 0
            ? new FiscalBandFact("Receipts this fiscal day", "—",
                devices.Count == 0 ? "no platform device known" : "no device answered")
            : new FiscalBandFact(
                "Receipts this fiscal day",
                openDays.Sum(device => device.LastReceiptCounter ?? 0).ToString("N0", CultureInfo.CurrentCulture),
                openDays.Count switch
                {
                    0 => "no fiscal day open",
                    1 => "on 1 open day",
                    var count => $"across {count} open days"
                });

        var answeringFact = devices.Count == 0
            ? new FiscalBandFact("Devices answering", "—", "no platform device known")
            : new FiscalBandFact(
                "Devices answering",
                $"{answering.Count} of {devices.Count}",
                answering.Count == 0 ? "the platform is silent" : Modes(answering));

        // Latest by the platform's own receipt date. That date is the device's wall clock, the same clock
        // the Devices panel prints it in, so it is not converted here either.
        var latest = answering
            .Where(device => device.LastReceiptDate is not null)
            .OrderByDescending(device => device.LastReceiptDate)
            .FirstOrDefault();

        var lastReceipt = latest is null
            ? new FiscalBandFact("Last receipt", "—", devices.Count == 0 ? "no platform device known" : "no receipt reported")
            : new FiscalBandFact(
                "Last receipt",
                latest.LastReceiptDate!.Value.ToString("HH:mm", CultureInfo.CurrentCulture),
                $"{Day(latest.LastReceiptDate.Value, nowLocal)} · device {latest.DeviceId}");

        FiscalBandFact[] facts = [receipts, answeringFact, FiscalDays(days), lastReceipt];

        return new FiscalConsoleBandView(
            FiscalBandSource.Platform,
            facts,
            devices.Count == 0 ? null : PlatformDevice(devices, answering, openDays));
    }

    private static FiscalBandDevice PlatformDevice(
        IReadOnlyList<FiscalConsoleDeviceResponse> devices,
        List<FiscalConsoleDeviceResponse> answering,
        List<FiscalConsoleDeviceResponse> openDays)
    {
        var ids = devices.Select(device => device.DeviceId.ToString(CultureInfo.InvariantCulture)).ToList();

        var name = devices.Count switch
        {
            1 => $"Platform device {ids[0]}",
            <= NamedDeviceLimit => $"Platform devices {string.Join(", ", ids)}",
            var count => $"{count} platform devices"
        };

        var branches = devices
            .Select(device => device.BranchName)
            .Where(branch => !string.IsNullOrWhiteSpace(branch))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var detail = branches.Count == 0 ? "branch unstated" : string.Join(" · ", branches);

        // Worst first, and one chip: the Devices panel carries the detail, and this line only has to say
        // whether it is worth opening.
        var broken = devices.Count(device => device.ChainBroken);
        var silent = devices.Count - answering.Count;

        var (status, family) =
            broken > 0 ? (broken == 1 ? "Chain broken" : $"{broken} chains broken", "bad")
            : silent > 0 ? ($"{silent} not answering", "warn")
            : openDays.Count == answering.Count ? (openDays.Count == 1 ? "Day open" : $"{openDays.Count} days open", "good")
            : ($"{openDays.Count} of {answering.Count} days open", "warn");

        return new FiscalBandDevice(
            name,
            devices.Count == 1 && !string.IsNullOrWhiteSpace(devices[0].SerialNumber) ? devices[0].SerialNumber : null,
            detail,
            status,
            family,
            LinksToDevices: true);
    }

    /// <summary>The fiscal day lifecycle, which is this system's own table under either provider.</summary>
    private static FiscalBandFact FiscalDays(FiscalDayStateListResponse? days) =>
        days is null
            ? new FiscalBandFact("Fiscal days", "—", "not read")
            : new FiscalBandFact(
                "Fiscal days",
                days.TotalCount.ToString("N0", CultureInfo.CurrentCulture),
                days.OutstandingCount == 0 ? "all submitted" : $"{days.OutstandingCount:N0} not submitted");

    private static bool IsPlatformDayOpen(FiscalConsoleDeviceResponse device) =>
        string.Equals(device.FiscalDayStatus, PlatformDayOpen, StringComparison.OrdinalIgnoreCase);

    /// <summary>"3 Online", or "2 Online · 1 Offline" — how ZIMRA registered the devices that answered.</summary>
    private static string Modes(List<FiscalConsoleDeviceResponse> answering) =>
        string.Join(" · ", answering
            .GroupBy(device => string.IsNullOrWhiteSpace(device.OperatingMode) ? "mode unstated" : device.OperatingMode)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Count()} {group.Key}"));

    private static string Day(DateTime local, DateTime nowLocal) =>
        local.Date == nowLocal.Date
            ? $"today, {local.ToString("dd MMM", CultureInfo.CurrentCulture)}"
            : local.ToString("dd MMM yyyy", CultureInfo.CurrentCulture);
}
