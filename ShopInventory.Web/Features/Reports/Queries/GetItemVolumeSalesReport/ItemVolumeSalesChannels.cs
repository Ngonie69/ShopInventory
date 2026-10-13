namespace ShopInventory.Web.Features.Reports.Queries.GetItemVolumeSalesReport;

/// <summary>
/// The trade channels a loaded report spans, and how to read it one channel at a time.
/// </summary>
/// <remarks>
/// <para>
/// Each business partner sells through one channel — Shops, General Trade, Vending and the rest, from SAP's
/// <c>OCRD.U_Channel</c> — and the channels sell different ranges. An item's volume added across them is a
/// number nobody can act on: a shop's litres and a wholesaler's pallets are not one trend. So where the
/// partners picked span more than one channel, the item view shows one channel's partners and no others.
/// </para>
/// <para>
/// Applied to the loaded result rather than to the request, for the reason the crate filter is: switching
/// channel re-reads the window already in hand instead of asking SAP again, and the one filtered result
/// feeds the pivot, the chart and the workbook, so the three cannot disagree about whose volume it is.
/// </para>
/// <para>
/// Unlike the crates, every figure here is re-added from the accounts kept rather than reached by
/// subtraction. An account's invoices and credit notes are its own, so the document counts add honestly
/// across the accounts of a channel, which they do not across the items of an invoice.
/// </para>
/// </remarks>
public static class ItemVolumeSalesChannels
{
    /// <summary>What a channel is called on the page. A partner SAP gives none is in a group of its own.</summary>
    public static string Label(string? channel) =>
        string.IsNullOrWhiteSpace(channel) ? "No channel" : channel.Trim();

    private static string Key(string? channel) => channel?.Trim() ?? string.Empty;

    private static bool Traded(ItemVolumeSalesAccountResult account) =>
        account.InvoiceCount > 0 || account.CreditNoteCount > 0;

    /// <summary>
    /// The channels with something to show, by name, with the partners that have no channel last. Empty
    /// when the report could not read the channels: there is then nothing to split by.
    /// </summary>
    public static List<string> ChannelsIn(GetItemVolumeSalesReportResult report) =>
        !report.ChannelsRead
            ? []
            : report.AccountTotals
                .Where(Traded)
                .Select(account => Key(account.Channel))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(channel => channel.Length == 0 ? 1 : 0)
                .ThenBy(channel => channel, StringComparer.OrdinalIgnoreCase)
                .ToList();

    /// <summary>
    /// The same window with only one channel's partners in it. Returns the report unchanged when every
    /// partner is already in that channel.
    /// </summary>
    public static GetItemVolumeSalesReportResult Only(GetItemVolumeSalesReportResult report, string? channel)
    {
        var wanted = Key(channel);

        bool InChannel(ItemVolumeSalesAccountResult account) =>
            string.Equals(Key(account.Channel), wanted, StringComparison.OrdinalIgnoreCase);

        if (report.AccountTotals.All(InChannel))
        {
            return report;
        }

        var accounts = report.AccountTotals.Where(InChannel).ToList();
        var codes = accounts.Select(account => account.CardCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var items = AddItems(accounts);

        return new GetItemVolumeSalesReportResult
        {
            GeneratedAtUtc = report.GeneratedAtUtc,
            FromDateUtc = report.FromDateUtc,
            ToDateUtc = report.ToDateUtc,
            Grouping = report.Grouping,
            ChannelsRead = report.ChannelsRead,

            // What was asked of SAP, which choosing a channel does not change.
            RequestedAccountCodes = report.RequestedAccountCodes,
            RequestedItemCodes = report.RequestedItemCodes,

            ItemCodesWithoutFactor = items
                .Where(item => !item.HasVolumeFactor)
                .Select(item => item.ItemCode)
                .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                .ToList(),

            Summary = Summarise(report.Summary, accounts, items),
            ItemTotals = items,
            AccountTotals = accounts,
            Periods = report.Periods.Select(period => OnlyPeriod(period, InChannel)).ToList(),
            DocumentLines = report.DocumentLines.Where(line => codes.Contains(line.CardCode)).ToList()
        };
    }

    /// <summary>
    /// One row per item across the accounts given, in the order the API lists a report's items: most
    /// volume first, then most units, then by code.
    /// </summary>
    private static List<ItemVolumeSalesItemResult> AddItems(IEnumerable<ItemVolumeSalesAccountResult> accounts) =>
        accounts
            .SelectMany(account => account.Items)
            .GroupBy(item => item.ItemCode, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First();

                return new ItemVolumeSalesItemResult
                {
                    ItemCode = first.ItemCode,
                    ItemName = first.ItemName,

                    // An invoice is one account's, so an item's documents add across accounts.
                    InvoiceCount = group.Sum(item => item.InvoiceCount),
                    CreditNoteCount = group.Sum(item => item.CreditNoteCount),

                    InvoicedQuantity = group.Sum(item => item.InvoicedQuantity),
                    CreditedQuantity = group.Sum(item => item.CreditedQuantity),
                    NetQuantity = group.Sum(item => item.NetQuantity),

                    // The factor is the item's, the same on every account that sold it.
                    VolumeFactor = first.VolumeFactor,
                    NetVolume = group.Sum(item => item.NetVolume),
                    InvoicedVolume = group.Sum(item => item.InvoicedVolume),
                    CreditedVolume = group.Sum(item => item.CreditedVolume),

                    InvoicedSalesUsd = group.Sum(item => item.InvoicedSalesUsd),
                    InvoicedSalesZig = group.Sum(item => item.InvoicedSalesZig),
                    CreditedSalesUsd = group.Sum(item => item.CreditedSalesUsd),
                    CreditedSalesZig = group.Sum(item => item.CreditedSalesZig),
                    NetRevenueUsd = group.Sum(item => item.NetRevenueUsd),
                    NetRevenueZig = group.Sum(item => item.NetRevenueZig)
                };
            })
            .OrderByDescending(item => item.NetVolume)
            .ThenByDescending(item => item.NetQuantity)
            .ThenBy(item => item.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static ItemVolumeSalesSummaryResult Summarise(
        ItemVolumeSalesSummaryResult whole,
        List<ItemVolumeSalesAccountResult> accounts,
        List<ItemVolumeSalesItemResult> items) =>
        new()
        {
            RequestedAccountCount = accounts.Count,
            ActiveAccountCount = accounts.Count(Traded),
            RequestedItemCount = whole.RequestedItemCount,
            ItemCount = items.Count,
            TotalPeriods = whole.TotalPeriods,

            InvoiceCount = accounts.Sum(account => account.InvoiceCount),
            CreditNoteCount = accounts.Sum(account => account.CreditNoteCount),

            InvoicedQuantity = items.Sum(item => item.InvoicedQuantity),
            CreditedQuantity = items.Sum(item => item.CreditedQuantity),
            NetQuantity = items.Sum(item => item.NetQuantity),

            InvoicedVolume = items.Sum(item => item.InvoicedVolume),
            CreditedVolume = items.Sum(item => item.CreditedVolume),
            NetVolume = items.Sum(item => item.NetVolume),

            ItemsWithoutFactorCount = items.Count(item => !item.HasVolumeFactor),
            QuantityWithoutFactor = items.Where(item => !item.HasVolumeFactor).Sum(item => item.NetQuantity),

            InvoicedSalesUsd = items.Sum(item => item.InvoicedSalesUsd),
            InvoicedSalesZig = items.Sum(item => item.InvoicedSalesZig),
            CreditedSalesUsd = items.Sum(item => item.CreditedSalesUsd),
            CreditedSalesZig = items.Sum(item => item.CreditedSalesZig),
            NetRevenueUsd = items.Sum(item => item.NetRevenueUsd),
            NetRevenueZig = items.Sum(item => item.NetRevenueZig)
        };

    private static ItemVolumeSalesPeriodResult OnlyPeriod(
        ItemVolumeSalesPeriodResult period, Func<ItemVolumeSalesAccountResult, bool> inChannel)
    {
        var accounts = period.Accounts.Where(inChannel).ToList();

        return new ItemVolumeSalesPeriodResult
        {
            Label = period.Label,
            PeriodStartUtc = period.PeriodStartUtc,
            PeriodEndUtc = period.PeriodEndUtc,

            InvoiceCount = accounts.Sum(account => account.InvoiceCount),
            CreditNoteCount = accounts.Sum(account => account.CreditNoteCount),

            NetQuantity = accounts.Sum(account => account.NetQuantity),
            NetVolume = accounts.Sum(account => account.NetVolume),
            NetRevenueUsd = accounts.Sum(account => account.NetRevenueUsd),
            NetRevenueZig = accounts.Sum(account => account.NetRevenueZig),

            Accounts = accounts
        };
    }
}
