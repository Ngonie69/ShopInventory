using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Common.Stock;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Notifications;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

/// <summary>
/// Tells the admins about till sales that are not in SAP while their units are held off the shelf:
/// when one gets stuck, a few days before the hold ends, and when it has ended.
/// </summary>
/// <remarks>
/// <para><b>Why.</b> A till or vending sale takes its units off the ledger at the counter and reaches
/// SAP a minute later. When it does not, every reading of the shelf from SAP nets it off again
/// (<see cref="UnpostedTillSales.OutstandingAsync"/>) — for <c>DailyStock:UnpostedSaleLookbackDays</c>,
/// and then not at all. Sale 12 at KEFSHOP (KEF-FAC-20260914-8DFB97306920) spent its six posting
/// attempts in six minutes of a HANA outage on 14 Sep 2026, was held for thirty days with nothing
/// telling anyone, and on 15 Oct its units went back on sale while SAP still had no invoice for them.
/// The sale was visible on /desktop-sales the whole time; nobody was ever sent there.</para>
///
/// <para><b>One notification per stage per pass, not per sale.</b> A SAP outage strands a whole day's
/// takings at once, and a row per sale would take every slot in the bell
/// (see <c>MainLayout.RecentNotificationCount</c>). Each sale is stamped with the furthest stage sent
/// for it, so it is never reported at the same stage twice.</para>
///
/// <para><b>Addressed to the Admin role.</b> Only an admin can post or retry the sale, and the release
/// is a stock-accounting decision, not news for the counter.</para>
/// </remarks>
public sealed class DesktopSaleStockHoldAlerter(
    ApplicationDbContext context,
    INotificationService notificationService,
    SapCircuitBreakerState circuitState,
    IOptions<DesktopSalePostingSettings> postingSettings,
    IOptions<DailyStockSettings> dailyStockSettings,
    ILogger<DesktopSaleStockHoldAlerter> logger)
{
    /// <summary>
    /// How long after its hold ended a sale is still worth reporting as released. Past it the release is
    /// old news, which also keeps the first run after this shipped from announcing every sale ever
    /// released.
    /// </summary>
    internal const int ReleasedReportDays = 7;

    private const int ListedSalesPerNotification = 8;
    private const int MessageMaxLength = 1000;
    private const int ErrorMaxLength = 160;

    public async Task<int> AlertAsync(DateTime utcNow, CancellationToken cancellationToken = default)
    {
        var posting = postingSettings.Value;

        if (posting.StuckSaleAlertAfterHours <= 0)
        {
            return 0;
        }

        var stock = dailyStockSettings.Value;
        var lookbackDays = Math.Max(0, stock.UnpostedSaleLookbackDays);
        var stuckBefore = utcNow.AddHours(-posting.StuckSaleAlertAfterHours);
        var scanFrom = utcNow.AddDays(-(lookbackDays + 1 + ReleasedReportDays));
        var tillSources = SaleSourceSystems.PostedByDesktopSaleJob;

        // The same population OutstandingAsync holds for earlier days: till sources, anything not
        // Consolidated. A sale whose budget is spent is stuck at once, whatever its age.
        var candidates = await context.DesktopSales
            .Where(s => s.SourceSystem != null
                        && tillSources.Contains(s.SourceSystem)
                        && s.ConsolidationStatus != DesktopSaleConsolidationStatus.Consolidated
                        && s.StockHoldAlert != DesktopSaleStockHoldAlert.Released
                        && s.CreatedAt >= scanFrom
                        && (s.CreatedAt < stuckBefore || s.PostingAttempts >= posting.MaxPostingAttempts))
            .OrderBy(s => s.Id)
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
        {
            return 0;
        }

        // While SAP is known to be down nothing can post, the outage is alerted on its own, and every
        // sale of the day would otherwise be reported as stuck by age. A spent budget still is.
        var sapDown = circuitState.ShouldHoldBackWork(out _);
        var due = new List<(DesktopSaleEntity Sale, DesktopSaleStockHoldAlert Stage, DateTime ReleaseAt)>();

        foreach (var sale in candidates)
        {
            var releaseAt = UnpostedTillSales.HoldEndsAtUtc(sale.CreatedAt, stock.StockFetchTimeCAT, lookbackDays);
            var stage = utcNow >= releaseAt
                ? DesktopSaleStockHoldAlert.Released
                : utcNow >= releaseAt.AddDays(-Math.Max(0, posting.StockHoldReleaseWarningDays))
                    ? DesktopSaleStockHoldAlert.ReleaseDue
                    : DesktopSaleStockHoldAlert.Stuck;

            if (stage <= sale.StockHoldAlert
                || (stage == DesktopSaleStockHoldAlert.Released && releaseAt < utcNow.AddDays(-ReleasedReportDays))
                || (stage == DesktopSaleStockHoldAlert.Stuck && sapDown && !BudgetSpent(sale, posting)))
            {
                continue;
            }

            due.Add((sale, stage, releaseAt));
        }

        var sent = 0;

        foreach (var group in due.GroupBy(d => d.Stage).OrderByDescending(g => g.Key))
        {
            var sales = group.ToList();

            try
            {
                await notificationService.CreateNotificationAsync(
                    BuildNotification(group.Key, sales, posting, utcNow), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Not stamped, so the next pass sends it again.
                logger.LogError(ex, "Could not alert the admins about {Count} till sale(s) at stage {Stage}.",
                    sales.Count, group.Key);
                continue;
            }

            foreach (var (sale, stage, _) in sales)
            {
                sale.StockHoldAlert = stage;
                sale.StockHoldAlertedAtUtc = utcNow;
            }

            await context.SaveChangesAsync(CancellationToken.None);
            sent += sales.Count;

            logger.LogWarning("Alerted the admins that {Count} till sale(s) not in SAP reached {Stage}: {Refs}",
                sales.Count, group.Key, string.Join(", ", sales.Select(s => s.Sale.ExternalReferenceId)));
        }

        return sent;
    }

    private static bool BudgetSpent(DesktopSaleEntity sale, DesktopSalePostingSettings posting) =>
        sale.PostingAttempts >= posting.MaxPostingAttempts;

    private static CreateNotificationRequest BuildNotification(
        DesktopSaleStockHoldAlert stage,
        List<(DesktopSaleEntity Sale, DesktopSaleStockHoldAlert Stage, DateTime ReleaseAt)> sales,
        DesktopSalePostingSettings posting,
        DateTime utcNow)
    {
        var single = sales.Count == 1;
        var first = sales[0];

        var (title, lead, consequence, type) = stage switch
        {
            DesktopSaleStockHoldAlert.Released => (
                single
                    ? $"Stock released for unposted sale {first.Sale.ExternalReferenceId}"
                    : $"Stock released for {sales.Count} unposted till sales",
                single
                    ? "This till sale never reached SAP, and its units are no longer held off the shelf:"
                    : "These till sales never reached SAP, and their units are no longer held off the shelf:",
                "The till and SAP both now count stock that was sold. Post the sale to SAP from Desktop Sales "
                    + "rather than refreshing quantities from SAP, which would keep the sold units on sale.",
                "Error"),
            DesktopSaleStockHoldAlert.ReleaseDue => (
                single
                    ? $"Stock hold ending: {first.Sale.ExternalReferenceId} still not in SAP"
                    : $"Stock hold ending for {sales.Count} till sales not in SAP",
                single
                    ? "This till sale is still not in SAP, and its units go back on sale soon:"
                    : "These till sales are still not in SAP, and their units go back on sale soon:",
                "Post or retry it from Desktop Sales before then, or the till will sell stock that is gone.",
                "Warning"),
            _ => (
                single
                    ? $"Till sale stuck before SAP: {first.Sale.ExternalReferenceId}"
                    : $"{sales.Count} till sales stuck before SAP",
                single
                    ? "This till sale has not reached SAP, and its units are being held off the shelf:"
                    : "These till sales have not reached SAP, and their units are being held off the shelf:",
                "Post or retry it from Desktop Sales. The hold ends on the date shown, and the units then go "
                    + "back on sale whether or not SAP has the invoice.",
                "Warning")
        };

        // Sales are dropped whole once the column would overflow, never cut mid-sentence: the count of
        // the rest and the instruction at the end are what the admin acts on.
        var message = new StringBuilder(lead);
        var listed = 0;

        foreach (var (sale, _, releaseAt) in sales.Take(ListedSalesPerNotification))
        {
            var entry = new StringBuilder(" ")
                .Append(sale.ExternalReferenceId)
                .Append(" (")
                .Append(sale.WarehouseCode)
                .Append(", ")
                .Append(ModuleNotificationFactory.DescribeBusinessPartner(sale.CardCode, sale.CardName))
                .Append(", ")
                .Append(ModuleNotificationFactory.DescribeMoney(sale.Currency, sale.TotalAmount))
                .Append(", sold ")
                .Append(DescribeCat(sale.CreatedAt))
                .Append(") — ")
                .Append(DescribeReason(sale, posting))
                .Append(stage == DesktopSaleStockHoldAlert.Released ? "; released " : "; held until ")
                .Append(DescribeCat(releaseAt))
                .Append('.')
                .ToString();

            var remainder = sales.Count - listed - 1;
            var tail = (remainder > 0 ? DescribeRemainder(remainder).Length : 0) + 1 + consequence.Length;

            if (listed > 0 && message.Length + entry.Length + tail > MessageMaxLength)
            {
                break;
            }

            message.Append(entry);
            listed++;
        }

        if (sales.Count > listed)
        {
            message.Append(DescribeRemainder(sales.Count - listed));
        }

        message.Append(' ').Append(consequence);

        var request = ModuleNotificationFactory.CreateBroadcastNotification(
            title,
            Truncate(message.ToString(), MessageMaxLength),
            type,
            "SalesOrder",
            "DesktopSale",
            first.Sale.Id.ToString(CultureInfo.InvariantCulture),
            "/desktop-sales",
            new Dictionary<string, string>
            {
                ["stage"] = stage.ToString(),
                ["saleIds"] = string.Join(",", sales.Select(s => s.Sale.Id.ToString(CultureInfo.InvariantCulture))),
                ["externalReferenceIds"] = string.Join(",", sales.Select(s => s.Sale.ExternalReferenceId)),
                ["checkedAtUtc"] = utcNow.ToString("O", CultureInfo.InvariantCulture)
            });

        request.TargetRole = "Admin";
        request.EntityId = single ? request.EntityId : null;

        return request;
    }

    private static string DescribeReason(DesktopSaleEntity sale, DesktopSalePostingSettings posting)
    {
        switch (sale.FiscalizationStatus)
        {
            case DesktopSaleFiscalizationStatus.Failed:
            case DesktopSaleFiscalizationStatus.Skipped:
                return "not fiscalised, so it cannot post"
                    + (string.IsNullOrWhiteSpace(sale.FiscalError) ? string.Empty : $": {Shorten(sale.FiscalError)}");
            case DesktopSaleFiscalizationStatus.Pending:
                return "still waiting to be fiscalised";
        }

        if (BudgetSpent(sale, posting))
        {
            return $"posting failed {sale.PostingAttempts} times and has stopped retrying"
                + (string.IsNullOrWhiteSpace(sale.LastPostingError) ? string.Empty : $": {Shorten(sale.LastPostingError)}");
        }

        return string.IsNullOrWhiteSpace(sale.LastPostingError)
            ? "not posted yet"
            : $"not accepted by SAP yet: {Shorten(sale.LastPostingError)}";
    }

    private static string DescribeRemainder(int count) =>
        $" And {count.ToString(CultureInfo.InvariantCulture)} more — all of them are listed on Desktop Sales.";

    private static string DescribeCat(DateTime utc) =>
        AuditService.ToCAT(utc).ToString("d MMM HH:mm", CultureInfo.InvariantCulture);

    private static string Shorten(string value) => Truncate(value.Trim(), ErrorMaxLength);

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";
}
