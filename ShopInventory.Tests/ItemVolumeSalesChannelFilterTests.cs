using ShopInventory.Web.Features.Reports.Queries.GetItemVolumeSalesReport;

namespace ShopInventory.Tests;

/// <summary>
/// Covers reading a loaded item volume report one trade channel at a time.
/// </summary>
/// <remarks>
/// The channels sell different ranges, so an item's volume added across them is a figure nobody can act on.
/// These pin that choosing a channel leaves only that channel's partners in every part of the report — the
/// item rows, the summary, each period and the document lines — and that the channels together still add
/// back to the whole, so splitting loses nothing and invents nothing.
/// </remarks>
public sealed class ItemVolumeSalesChannelFilterTests
{
    private const string Shops = "Shops";
    private const string GeneralTrade = "General Trade";

    [Fact]
    public void The_channels_offered_are_the_ones_that_traded_by_name_with_no_channel_last()
    {
        var report = Report();

        Assert.Equal([GeneralTrade, Shops, ""], ItemVolumeSalesChannels.ChannelsIn(report));
        Assert.Equal("No channel", ItemVolumeSalesChannels.Label(""));
        Assert.Equal(Shops, ItemVolumeSalesChannels.Label(Shops));
    }

    [Fact]
    public void A_partner_that_sold_nothing_does_not_put_its_channel_on_offer()
    {
        var report = Report();
        report.AccountTotals.Add(new ItemVolumeSalesAccountResult { CardCode = "VND001", CardName = "Vending", Channel = "Vending" });

        Assert.DoesNotContain("Vending", ItemVolumeSalesChannels.ChannelsIn(report));
    }

    [Fact]
    public void A_report_that_could_not_read_the_channels_offers_none()
    {
        var report = Report();
        report.ChannelsRead = false;

        Assert.Empty(ItemVolumeSalesChannels.ChannelsIn(report));
    }

    [Fact]
    public void One_channel_leaves_only_its_partners_and_adds_their_items()
    {
        var shops = ItemVolumeSalesChannels.Only(Report(), Shops);

        Assert.Equal(["SHP001", "SHP002"], shops.AccountTotals.Select(account => account.CardCode));

        // MILK is sold by both shops and by the wholesaler. The shops' row is the two shops' alone.
        var milk = shops.ItemTotals.Single(item => item.ItemCode == "MILK");
        Assert.Equal((30m, 15m, 60m), (milk.NetQuantity, milk.NetVolume, milk.NetRevenueUsd));
        Assert.Equal((3, 1), (milk.InvoiceCount, milk.CreditNoteCount));
        Assert.Equal(0.5m, milk.VolumeFactor);

        // And an item only the wholesaler sold is not in the shops' list at all.
        Assert.Equal(["MILK", "CHEESE"], shops.ItemTotals.Select(item => item.ItemCode));
        Assert.DoesNotContain(shops.ItemTotals, item => item.ItemCode == "BULK20");
    }

    [Fact]
    public void The_summary_the_periods_and_the_document_lines_follow_the_channel()
    {
        var shops = ItemVolumeSalesChannels.Only(Report(), Shops);

        Assert.Equal(2, shops.Summary.RequestedAccountCount);
        Assert.Equal(2, shops.Summary.ActiveAccountCount);
        Assert.Equal(2, shops.Summary.ItemCount);
        Assert.Equal(15m, shops.Summary.NetVolume);
        Assert.Equal(34m, shops.Summary.NetQuantity);
        Assert.Equal(100m, shops.Summary.NetRevenueUsd);
        Assert.Equal((4, 1), (shops.Summary.InvoiceCount, shops.Summary.CreditNoteCount));

        // CHEESE has no factor: it stays in the list, unconverted, and is named as a gap.
        Assert.Equal(1, shops.Summary.ItemsWithoutFactorCount);
        Assert.Equal(4m, shops.Summary.QuantityWithoutFactor);
        Assert.Equal(["CHEESE"], shops.ItemCodesWithoutFactor);

        var july = shops.Periods.Single();
        Assert.Equal(["SHP001", "SHP002"], july.Accounts.Select(account => account.CardCode));
        Assert.Equal(15m, july.NetVolume);
        Assert.Equal(100m, july.NetRevenueUsd);

        Assert.Equal(["SHP001", "SHP002"], shops.DocumentLines.Select(line => line.CardCode).Distinct().Order());
    }

    [Fact]
    public void The_channels_together_are_the_whole_window()
    {
        var report = Report();
        var parts = ItemVolumeSalesChannels.ChannelsIn(report)
            .Select(channel => ItemVolumeSalesChannels.Only(report, channel))
            .ToList();

        Assert.Equal(report.AccountTotals.Sum(account => account.NetVolume), parts.Sum(part => part.Summary.NetVolume));
        Assert.Equal(report.AccountTotals.Sum(account => account.NetQuantity), parts.Sum(part => part.Summary.NetQuantity));
        Assert.Equal(report.AccountTotals.Sum(account => account.NetRevenueUsd), parts.Sum(part => part.Summary.NetRevenueUsd));
        Assert.Equal(report.AccountTotals.Count, parts.Sum(part => part.AccountTotals.Count));
        Assert.Equal(report.DocumentLines.Count, parts.Sum(part => part.DocumentLines.Count));
    }

    [Fact]
    public void A_window_of_one_channel_is_handed_straight_back()
    {
        var report = Report();
        report.AccountTotals.RemoveAll(account => account.Channel != Shops);

        Assert.Same(report, ItemVolumeSalesChannels.Only(report, Shops));
    }

    [Fact]
    public void The_partners_with_no_channel_are_a_group_of_their_own()
    {
        var none = ItemVolumeSalesChannels.Only(Report(), "");

        Assert.Equal(["CIS900"], none.AccountTotals.Select(account => account.CardCode));
        Assert.Equal(["MILK"], none.ItemTotals.Select(item => item.ItemCode));
        Assert.Equal(1m, none.Summary.NetVolume);
    }

    // ── A window of four partners in three channels, all in July ────────────────────────────────

    private static GetItemVolumeSalesReportResult Report()
    {
        List<ItemVolumeSalesAccountResult> accounts =
        [
            Account("SHP001", Shops, invoices: 2, credits: 1,
                Item("MILK", 0.5m, quantity: 20m, usd: 40m, invoices: 2, credits: 1),
                Item("CHEESE", null, quantity: 4m, usd: 40m, invoices: 1, credits: 0)),
            Account("SHP002", Shops, invoices: 2, credits: 0,
                Item("MILK", 0.5m, quantity: 10m, usd: 20m, invoices: 1, credits: 0)),
            Account("WHS001", GeneralTrade, invoices: 5, credits: 0,
                Item("MILK", 0.5m, quantity: 4000m, usd: 6000m, invoices: 5, credits: 0),
                Item("BULK20", 20m, quantity: 100m, usd: 9000m, invoices: 3, credits: 0)),
            Account("CIS900", "", invoices: 1, credits: 0,
                Item("MILK", 0.5m, quantity: 2m, usd: 5m, invoices: 1, credits: 0)),
        ];

        return new GetItemVolumeSalesReportResult
        {
            FromDateUtc = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            ToDateUtc = new DateTime(2026, 7, 31, 0, 0, 0, DateTimeKind.Utc),
            Grouping = ItemVolumeSalesGrouping.Monthly,
            ChannelsRead = true,
            RequestedAccountCodes = accounts.Select(account => account.CardCode).ToList(),
            Summary = new ItemVolumeSalesSummaryResult { TotalPeriods = 1 },
            AccountTotals = accounts,
            Periods =
            [
                new ItemVolumeSalesPeriodResult
                {
                    Label = "Jul 2026",
                    PeriodStartUtc = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
                    PeriodEndUtc = new DateTime(2026, 7, 31, 0, 0, 0, DateTimeKind.Utc),
                    Accounts = accounts.ToList()
                }
            ],
            DocumentLines = accounts
                .SelectMany(account => account.Items.Select(item => new ItemVolumeSalesDocumentLineResult
                {
                    CardCode = account.CardCode,
                    CardName = account.CardName,
                    ItemCode = item.ItemCode
                }))
                .ToList()
        };
    }

    private static ItemVolumeSalesAccountResult Account(
        string cardCode, string channel, int invoices, int credits, params ItemVolumeSalesItemResult[] items) =>
        new()
        {
            CardCode = cardCode,
            CardName = cardCode + " name",
            Channel = channel,
            InvoiceCount = invoices,
            CreditNoteCount = credits,
            NetQuantity = items.Sum(item => item.NetQuantity),
            NetVolume = items.Sum(item => item.NetVolume),
            NetRevenueUsd = items.Sum(item => item.NetRevenueUsd),
            ItemsWithoutFactorCount = items.Count(item => !item.HasVolumeFactor),
            Items = items.ToList()
        };

    private static ItemVolumeSalesItemResult Item(
        string itemCode, decimal? factor, decimal quantity, decimal usd, int invoices, int credits) =>
        new()
        {
            ItemCode = itemCode,
            ItemName = itemCode + " name",
            VolumeFactor = factor,
            InvoiceCount = invoices,
            CreditNoteCount = credits,
            InvoicedQuantity = quantity,
            NetQuantity = quantity,
            InvoicedVolume = quantity * (factor ?? 0m),
            NetVolume = quantity * (factor ?? 0m),
            InvoicedSalesUsd = usd,
            NetRevenueUsd = usd
        };
}
