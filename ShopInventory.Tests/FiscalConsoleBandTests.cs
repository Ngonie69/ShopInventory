using ShopInventory.Web.Components;
using ShopInventory.Web.Services;
using Xunit;

namespace ShopInventory.Tests;

/// <summary>
/// The fiscalisation console's band, and which system it says receipts are going to.
/// </summary>
/// <remarks>
/// After the REVMax → platform cut-over on 2026-09-30 the band still named REVMax device 22862, its serial
/// and a stale "last filed" date above every tab, which read as though REVMax were still the live path.
/// These pin the band to the configured provider: the platform's devices under the platform, REVMax only
/// under REVMax, and neither when the provider could not be read.
/// </remarks>
public sealed class FiscalConsoleBandTests
{
    private static readonly DateTime Now = new(2026, 9, 30, 14, 0, 0, DateTimeKind.Local);

    [Fact]
    public void Under_the_platform_the_band_never_names_the_revmax_device()
    {
        var band = FiscalConsoleBand.Build(
            Revmax(provider: "Platform", live: false),
            revmaxPending: false,
            PlatformFleet(),
            devicesPending: false,
            Days(),
            Now);

        Assert.Equal(FiscalBandSource.Platform, band.Source);

        var text = Flatten(band);
        Assert.DoesNotContain("22862", text);
        Assert.DoesNotContain("8DE6996C0188", text);
        Assert.DoesNotContain("KEFALOS", text);

        Assert.NotNull(band.Device);
        Assert.Equal("Platform devices 46668, 46669, 46670", band.Device!.Name);
        Assert.True(band.Device.LinksToDevices);
    }

    [Fact]
    public void Under_the_platform_the_figures_are_the_platform_devices_own()
    {
        var band = FiscalConsoleBand.Build(
            Revmax(provider: "Platform", live: false),
            revmaxPending: false,
            PlatformFleet(),
            devicesPending: false,
            Days(),
            Now);

        // The counters of the open days only: 46670's day is closed, and its counter belongs to a day
        // that is over.
        var receipts = Fact(band, "Receipts this fiscal day");
        Assert.Equal("17", receipts.Value);
        Assert.Equal("across 2 open days", receipts.Note);

        var answering = Fact(band, "Devices answering");
        Assert.Equal("3 of 3", answering.Value);
        Assert.Equal("3 Online", answering.Note);

        // The latest receipt on any device, in the device's own clock and not converted.
        var last = Fact(band, "Last receipt");
        Assert.Equal("13:42", last.Value);
        Assert.Equal($"today, {Now:dd MMM} · device 46669", last.Note);

        Assert.Equal("2 of 3 days open", band.Device!.Status);
        Assert.Equal("warn", band.Device.Family);
    }

    [Fact]
    public void Every_platform_day_open_is_good()
    {
        var fleet = PlatformFleet();
        fleet[2].FiscalDayStatus = "FiscalDayOpened";

        var band = FiscalConsoleBand.Build(
            Revmax(provider: "Platform", live: false), false, fleet, false, Days(), Now);

        Assert.Equal("3 days open", band.Device!.Status);
        Assert.Equal("good", band.Device.Family);
    }

    [Fact]
    public void A_broken_chain_outranks_everything_on_the_device_line()
    {
        var fleet = PlatformFleet();
        fleet[1].ChainBroken = true;
        fleet[2].Reachable = false;

        var band = FiscalConsoleBand.Build(
            Revmax(provider: "Platform", live: false), false, fleet, false, Days(), Now);

        Assert.Equal("Chain broken", band.Device!.Status);
        Assert.Equal("bad", band.Device.Family);
    }

    [Fact]
    public void A_silent_device_is_counted_not_dropped()
    {
        var fleet = PlatformFleet();
        fleet[0].Reachable = false;

        var band = FiscalConsoleBand.Build(
            Revmax(provider: "Platform", live: false), false, fleet, false, Days(), Now);

        Assert.Equal("2 of 3", Fact(band, "Devices answering").Value);
        Assert.Equal("1 not answering", band.Device!.Status);
        Assert.Equal("warn", band.Device.Family);
    }

    [Fact]
    public void An_unread_device_list_is_not_reported_as_no_devices()
    {
        var failed = FiscalConsoleBand.Build(
            Revmax(provider: "Platform", live: false), false, null, devicesPending: false, Days(), Now);

        Assert.Equal("device list not read", Fact(failed, "Devices answering").Note);
        Assert.Null(failed.Device);

        var reading = FiscalConsoleBand.Build(
            Revmax(provider: "Platform", live: false), false, null, devicesPending: true, Days(), Now);

        Assert.Equal("reading the platform…", Fact(reading, "Devices answering").Note);
    }

    [Fact]
    public void A_list_read_earlier_stays_on_the_band_while_a_refresh_runs()
    {
        var band = FiscalConsoleBand.Build(
            Revmax(provider: "Platform", live: false), false, PlatformFleet(), devicesPending: true, Days(), Now);

        Assert.Equal("3 of 3", Fact(band, "Devices answering").Value);
    }

    [Fact]
    public void Under_revmax_the_band_is_revmax()
    {
        var band = FiscalConsoleBand.Build(
            Revmax(provider: "Revmax", live: true),
            revmaxPending: false,
            PlatformFleet(),
            devicesPending: false,
            Days(),
            Now);

        Assert.Equal(FiscalBandSource.Revmax, band.Source);

        Assert.Equal(1204.ToString("N0"), Fact(band, "Receipts filed").Value);
        Assert.Equal("no. 5001 to 6204", Fact(band, "Receipts filed").Note);
        Assert.Equal(1190.ToString("N0"), Fact(band, "Documents filed").Value);

        Assert.Equal("Device 22862", band.Device!.Name);
        Assert.Equal("8DE6996C0188", band.Device.Serial);
        Assert.Equal("KEFALOS FOODS · TIN 2000123456", band.Device.Detail);
        Assert.Equal("FiscalDayOpened 412", band.Device.Status);
        Assert.Equal("good", band.Device.Family);
        Assert.False(band.Device.LinksToDevices);

        Assert.DoesNotContain("46668", Flatten(band));
    }

    [Fact]
    public void An_unread_provider_names_neither_system()
    {
        var band = FiscalConsoleBand.Build(null, revmaxPending: false, PlatformFleet(), false, Days(), Now);

        Assert.Equal(FiscalBandSource.Unknown, band.Source);
        Assert.Null(band.Device);
        Assert.Equal("provider not read", Fact(band, "Receipts filed").Note);

        var text = Flatten(band);
        Assert.DoesNotContain("22862", text);
        Assert.DoesNotContain("46668", text);
    }

    [Fact]
    public void The_fiscal_day_figure_is_shared_by_both_providers()
    {
        var platform = FiscalConsoleBand.Build(
            Revmax(provider: "Platform", live: false), false, PlatformFleet(), false, Days(), Now);
        var revmax = FiscalConsoleBand.Build(
            Revmax(provider: "Revmax", live: true), false, PlatformFleet(), false, Days(), Now);

        Assert.Equal(Fact(platform, "Fiscal days"), Fact(revmax, "Fiscal days"));
        Assert.Equal("2 not submitted", Fact(platform, "Fiscal days").Note);
    }

    [Fact]
    public void No_tracked_day_is_not_read_as_every_day_submitted()
    {
        // The table holds only handset-signed days. With every device Online it stays empty, and "0 · all
        // submitted" read as a clean bill for days this system never saw.
        var band = FiscalConsoleBand.Build(
            Revmax(provider: "Platform", live: false), false, PlatformFleet(), false,
            new FiscalDayStateListResponse { TotalCount = 0, OutstandingCount = 0 }, Now);

        Assert.Equal("none signed by a handset", Fact(band, "Fiscal days").Note);
    }

    private static FiscalBandFact Fact(FiscalConsoleBandView band, string label) =>
        Assert.Single(band.Facts, fact => fact.Label == label);

    private static string Flatten(FiscalConsoleBandView band) =>
        string.Join(
            " | ",
            band.Facts.SelectMany(fact => new[] { fact.Label, fact.Value, fact.Note })
                .Concat(band.Device is null
                    ? []
                    : [band.Device.Name, band.Device.Serial ?? string.Empty, band.Device.Detail, band.Device.Status]));

    /// <summary>The REVMax read as the API returns it, device 22862 answering either way.</summary>
    private static RevmaxActivityResponse Revmax(string provider, bool live) => new()
    {
        Provider = provider,
        IsLiveProvider = live,
        Enabled = true,
        Device = new RevmaxDeviceResponse
        {
            DeviceId = "22862",
            SerialNumber = "8DE6996C0188",
            CompanyName = "KEFALOS FOODS",
            Tin = "2000123456",
            FiscalDayStatus = "FiscalDayOpened",
            LastFiscalDayNo = 412
        },
        Totals = new RevmaxTotalsResponse
        {
            ReceiptsFiled = 1204,
            DocumentsFiled = 1190,
            FirstReceiptGlobalNo = 5001,
            LastReceiptGlobalNo = 6204,
            LastAtUtc = new DateTime(2026, 9, 29, 23, 50, 0, DateTimeKind.Utc)
        }
    };

    /// <summary>The three platform devices, all Online; 46670's day is closed.</summary>
    private static List<FiscalConsoleDeviceResponse> PlatformFleet() =>
    [
        new()
        {
            DeviceId = 46668,
            Reachable = true,
            SerialNumber = "PLAT-46668",
            BranchName = "Head Office",
            OperatingMode = "Online",
            FiscalDayNo = 3,
            FiscalDayStatus = "FiscalDayOpened",
            LastReceiptCounter = 12,
            LastReceiptDate = new DateTime(2026, 9, 30, 11, 5, 0)
        },
        new()
        {
            DeviceId = 46669,
            Reachable = true,
            SerialNumber = "PLAT-46669",
            BranchName = "Head Office",
            OperatingMode = "Online",
            FiscalDayNo = 2,
            FiscalDayStatus = "FiscalDayOpened",
            LastReceiptCounter = 5,
            LastReceiptDate = new DateTime(2026, 9, 30, 13, 42, 0)
        },
        new()
        {
            DeviceId = 46670,
            Reachable = true,
            SerialNumber = "PLAT-46670",
            BranchName = "Head Office",
            OperatingMode = "Online",
            FiscalDayNo = 1,
            FiscalDayStatus = "FiscalDayClosed",
            LastReceiptCounter = 40,
            LastReceiptDate = new DateTime(2026, 9, 29, 17, 0, 0)
        }
    ];

    private static FiscalDayStateListResponse Days() => new()
    {
        TotalCount = 6,
        OutstandingCount = 2,
        NeedsAttentionCount = 0
    };
}
