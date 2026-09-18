using System.Globalization;
using ShopInventory.Common.Sales;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesAnalysis;
using ShopInventory.Features.DesktopIntegration.Queries.GetManagementSalesReport;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReview;

/// <summary>
/// Writes the review's findings from its figures.
/// </summary>
/// <remarks>
/// <para>
/// Every rule reads only the finished review and the management report it was built from, so a finding
/// can always be traced to a number on the same page, and the rules can be tested without a database.
/// </para>
/// <para>
/// Thresholds are named constants rather than settings. They describe what is unusual for a shop
/// counter, not a preference, and a threshold that moves from week to week would make one week's review
/// incomparable with the next.
/// </para>
/// </remarks>
public static class DesktopSalesReviewFindings
{
    /// <summary>A shop taking this share of the period or more is called out as carrying the business.</summary>
    public const decimal ConcentrationPercent = 40m;

    /// <summary>Cash above this share of takings makes cash handling the payment risk worth naming.</summary>
    public const decimal CashHeavyPercent = 80m;

    /// <summary>An item priced this much higher at one shop than another is listed.</summary>
    public const decimal PriceSpreadPercent = 10m;

    /// <summary>A day this many times the average trading day is a spike.</summary>
    public const decimal SpikeFactor = 2.5m;

    /// <summary>How many consecutive hours make the counter's peak window.</summary>
    public const int PeakWindowHours = 3;

    /// <summary>A vending settlement carrying this many times a counter sale's units reads as a round paid in, not a sale.</summary>
    public const decimal SettlementUnitsFactor = 3m;

    /// <summary>A shop's effective VAT this many points below the highest shop's implies zero-rated lines.</summary>
    public const decimal VatGapPoints = 0.15m;

    /// <summary>A shop's margin this many points below the whole period's is called out.</summary>
    public const decimal MarginGapPoints = 5m;

    /// <summary>Below this share of revenue costed by SAP, the margin is a partial picture and says so.</summary>
    public const decimal MarginCoveragePercent = 80m;

    /// <summary>Discounts above this share of line value are worth a look.</summary>
    public const decimal DiscountPercent = 1m;

    /// <summary>A sale SAP has not had for this many days past its date is late.</summary>
    public const int PostingLateDays = 3;

    public static List<DesktopSalesReviewFinding> Write(DesktopSalesReview review, ManagementSalesReport management)
    {
        var findings = new List<DesktopSalesReviewFinding>();

        Compliance(review, findings);

        foreach (var section in review.Currencies)
        {
            var managementSection = management.Currencies.FirstOrDefault(m => m.Currency == section.Currency);

            Period(review, section, findings);
            Trend(section, findings);
            SpikeDays(section, findings);
            Concentration(section, findings);
            Settlements(section, findings);
            Hours(section, findings);
            Payments(section, findings);
            Pricing(section, findings);
            LargeSales(section, findings);
            Operators(section, findings);
            Tax(section, findings);
            Margin(review, section, findings);
            Discounts(section, managementSection, findings);
            LapsedVendors(section, managementSection, findings);
        }

        return findings
            .Select((finding, order) => (finding, order))
            .OrderBy(x => DesktopSalesReviewSeverity.Rank(x.finding.Severity))
            .ThenBy(x => x.order)
            .Select(x => x.finding)
            .ToList();
    }

    // ── Rules ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Sales that have not reached ZIMRA or SAP. Money and compliance, so they lead.</summary>
    private static void Compliance(DesktopSalesReview review, List<DesktopSalesReviewFinding> findings)
    {
        var health = review.Health;

        if (health.FiscalFailed.SalesCount > 0)
        {
            findings.Add(new("fiscal-failed", DesktopSalesReviewSeverity.Action, DesktopSalesReviewTopic.Compliance, null,
                $"{Count(health.FiscalFailed.SalesCount, "sale")} failed fiscalisation",
                $"{Amounts(health.FiscalFailed)} was sold without a fiscal receipt ZIMRA accepted. Retry them from the exception centre before the fiscal day's close."));
        }

        if (health.NeedsReconciliation.SalesCount > 0)
        {
            findings.Add(new("fiscal-reconcile", DesktopSalesReviewSeverity.Action, DesktopSalesReviewTopic.Compliance, null,
                $"{Count(health.NeedsReconciliation.SalesCount, "sale")} need fiscal reconciliation",
                $"The till and the fiscal device disagree about {Amounts(health.NeedsReconciliation)}. Ask the device which receipts it holds before anything is re-sent."));
        }

        if (health.PostingFailing.SalesCount > 0)
        {
            findings.Add(new("posting-failing", DesktopSalesReviewSeverity.Action, DesktopSalesReviewTopic.Compliance, null,
                $"{Count(health.PostingFailing.SalesCount, "sale")} SAP refused",
                $"{Amounts(health.PostingFailing)} has been tried and not posted."
                    + (string.IsNullOrWhiteSpace(health.LatestPostingError) ? string.Empty : $" The latest refusal: {health.LatestPostingError.Trim()}")));
        }

        if (health.PaymentFailed.SalesCount > 0)
        {
            findings.Add(new("payment-failed", DesktopSalesReviewSeverity.Action, DesktopSalesReviewTopic.Compliance, null,
                $"{Count(health.PaymentFailed.SalesCount, "sale")} with a failed incoming payment",
                $"{Amounts(health.PaymentFailed)} is invoiced in SAP but its payment did not post, so the customer's account shows it owing."));
        }

        if (health.OldestUnpostedDate is { } oldest && (review.ToDate - oldest).Days >= PostingLateDays)
        {
            findings.Add(new("posting-late", DesktopSalesReviewSeverity.Review, DesktopSalesReviewTopic.Compliance, null,
                $"Sales from {Day(oldest)} are not in SAP yet",
                $"{Count(health.PostingWaiting.SalesCount + health.PostingFailing.SalesCount, "sale")} are still outside SAP, the oldest {(review.ToDate - oldest).Days} days before the period's end. Stock and the ledger in SAP are short by them until they post."));
        }
    }

    /// <summary>Shops that started mid-period, or a period with nothing before it to compare with.</summary>
    private static void Period(DesktopSalesReview review, DesktopSalesReviewCurrency section, List<DesktopSalesReviewFinding> findings)
    {
        var started = section.ByShop
            .Where(shop => shop.StartedInPeriod && shop.FirstSaleDate > review.FromDate)
            .OrderBy(shop => shop.FirstSaleDate)
            .ToList();

        if (started.Count > 0)
        {
            findings.Add(new("shops-started", DesktopSalesReviewSeverity.Note, DesktopSalesReviewTopic.Period, section.Currency,
                started.Count == 1
                    ? $"{started[0].Label} started trading on {Day(started[0].FirstSaleDate!.Value)}"
                    : $"{started.Count} shops started trading during the period",
                (started.Count == 1
                    ? $"Its total covers {Count(started[0].DaysTraded, "trading day")} of the period's {(review.ToDate - review.FromDate).Days + 1}. "
                    : $"{string.Join(", ", started.Select(shop => $"{shop.Label} ({Day(shop.FirstSaleDate!.Value)})"))}. Their totals cover fewer days than the period. ")
                    + "Compare shops on takings per trading day, and read any day-on-day rise as the rollout as much as trade."));
        }

        if (section.Headline.PreviousSalesCount == 0 && section.Headline.SalesCount > 0)
        {
            findings.Add(new("no-comparison", DesktopSalesReviewSeverity.Note, DesktopSalesReviewTopic.Period, section.Currency,
                "Nothing to compare with",
                $"No {section.Currency} sales were recorded from {Day(review.PreviousFromDate)} to {Day(review.PreviousToDate)}, so this review has no previous period. Trends start with the next one."));
        }
    }

    /// <summary>The change on the previous period, split into more sales and bigger sales.</summary>
    private static void Trend(DesktopSalesReviewCurrency section, List<DesktopSalesReviewFinding> findings)
    {
        var headline = section.Headline;
        if (headline.TotalChangePercent is not { } change || headline.TrafficEffect is not { } traffic || headline.TicketEffect is not { } ticket)
        {
            return;
        }

        var direction = change >= 0 ? "up" : "down";
        findings.Add(new("takings-change", change <= -10m ? DesktopSalesReviewSeverity.Review : DesktopSalesReviewSeverity.Note,
            DesktopSalesReviewTopic.Trend, section.Currency,
            $"Takings {direction} {Math.Abs(change).ToString("0.#", CultureInfo.InvariantCulture)}% on the previous period",
            $"{Money(section.Currency, headline.TotalAmount)} against {Money(section.Currency, headline.PreviousTotalAmount)}. "
                + $"The number of sales ({headline.PreviousSalesCount:N0} → {headline.SalesCount:N0}) accounts for {Signed(section.Currency, traffic)}; "
                + $"the average sale ({Money(section.Currency, headline.PreviousAverageSale)} → {Money(section.Currency, headline.AverageSale)}) for {Signed(section.Currency, ticket)}."));
    }

    /// <summary>A day far above the rest, and whether a shop starting that day explains it.</summary>
    private static void SpikeDays(DesktopSalesReviewCurrency section, List<DesktopSalesReviewFinding> findings)
    {
        if (section.ByDay.Count < 3)
        {
            return;
        }

        var average = section.ByDay.Average(day => day.TotalAmount);
        foreach (var day in section.ByDay.Where(day => average > 0 && day.TotalAmount >= average * SpikeFactor))
        {
            var startedThatDay = section.ByShop.Where(shop => shop.FirstSaleDate == day.Date).Select(shop => shop.Label).ToList();
            findings.Add(new("spike-day", startedThatDay.Count > 0 ? DesktopSalesReviewSeverity.Note : DesktopSalesReviewSeverity.Review,
                DesktopSalesReviewTopic.Trend, section.Currency,
                $"{day.Date:ddd d MMM} took {Money(section.Currency, day.TotalAmount)}",
                startedThatDay.Count > 0
                    ? $"Well above the daily average, because {string.Join(", ", startedThatDay)} started trading that day."
                    : $"{Ratio(day.TotalAmount, average)} times the average trading day. Check it was one real day's trade and not a catch-up of earlier days."));
        }
    }

    private static void Concentration(DesktopSalesReviewCurrency section, List<DesktopSalesReviewFinding> findings)
    {
        if (section.ByShop.Count < 2)
        {
            return;
        }

        var top = section.ByShop[0];
        if (top.ShareOfValuePercent < ConcentrationPercent)
        {
            return;
        }

        var next = section.ByShop.Where(shop => shop != top).MaxBy(shop => shop.PerTradingDay)!;
        findings.Add(new("concentration", DesktopSalesReviewSeverity.Note, DesktopSalesReviewTopic.Shops, section.Currency,
            $"{top.Label} carries {Pct(top.ShareOfValuePercent)} of takings",
            $"{Money(section.Currency, top.TotalAmount)} over {Count(top.DaysTraded, "trading day")}, {Money(section.Currency, top.PerTradingDay)} a day. "
                + $"The next best is {next.Label} at {Money(section.Currency, next.PerTradingDay)} a day. A day lost at {top.Label} costs more than any other shop's."));
    }

    /// <summary>Vending settlements behave nothing like a counter sale, and skew every average they touch.</summary>
    private static void Settlements(DesktopSalesReviewCurrency section, List<DesktopSalesReviewFinding> findings)
    {
        var vending = section.ByShop.Where(IsVending).ToList();
        var counters = section.ByShop.Where(shop => !IsVending(shop)).ToList();
        if (vending.Count == 0 || counters.Count == 0)
        {
            return;
        }

        var vendingUnits = UnitsPerSale(vending);
        var counterUnits = UnitsPerSale(counters);
        if (counterUnits == 0 || vendingUnits < counterUnits * SettlementUnitsFactor)
        {
            return;
        }

        var vendingTotal = vending.Sum(shop => shop.TotalAmount);
        var vendingCount = vending.Sum(shop => shop.SalesCount);
        var busiest = section.ByHour.MaxBy(hour => hour.SalesCount);
        findings.Add(new("vending-settlements", DesktopSalesReviewSeverity.Note, DesktopSalesReviewTopic.Shops, section.Currency,
            "Vending sales are settlements, not customer purchases",
            $"{Count(vendingCount, "vending sale")} ({Money(section.Currency, vendingTotal)}) average {vendingUnits:0} units each against {counterUnits:0.#} at a counter: each is a vendor's round paid in. "
                + "They lift the average sale and the hours they are captured in"
                + (busiest is { SettlementSalesCount: > 0 } ? $" — {busiest.SettlementSalesCount} of the {busiest.SalesCount} sales at {Hour(busiest.Hour)} are settlements" : string.Empty)
                + ". Counter figures below are read without them."));
    }

    /// <summary>When the counters are busiest, and when they are nearly empty.</summary>
    private static void Hours(DesktopSalesReviewCurrency section, List<DesktopSalesReviewFinding> findings)
    {
        var counter = section.ByHour
            .Select(hour => (hour.Hour, Sales: hour.SalesCount - hour.SettlementSalesCount, Takings: hour.TotalAmount - hour.SettlementTotalAmount))
            .Where(hour => hour.Sales > 0)
            .OrderBy(hour => hour.Hour)
            .ToList();

        var total = counter.Sum(hour => hour.Sales);
        if (total < 20 || counter.Count <= PeakWindowHours)
        {
            return;
        }

        var best = Enumerable.Range(counter.First().Hour, counter.Last().Hour - counter.First().Hour + 1)
            .Select(start => (Start: start, Sales: counter.Where(h => h.Hour >= start && h.Hour < start + PeakWindowHours).Sum(h => h.Sales)))
            .MaxBy(window => window.Sales);

        var busiest = counter.MaxBy(hour => hour.Sales);
        var takings = counter.Sum(hour => hour.Takings);
        var edge = counter.Where(hour => hour.Hour < 8 || hour.Hour >= 16).Sum(hour => hour.Takings);

        findings.Add(new("peak-hours", DesktopSalesReviewSeverity.Note, DesktopSalesReviewTopic.Hours, section.Currency,
            $"Counters are busiest from {Hour(best.Start)} to {Hour(best.Start + PeakWindowHours)}",
            $"{Pct(Math.Round(best.Sales * 100m / total, 0))} of counter sales fall in those {PeakWindowHours} hours; {Hour(busiest.Hour)} is the busiest single hour ({busiest.Sales:N0} sales). "
                + $"Before 08:00 and from 16:00 the counters take {Pct(takings == 0 ? 0 : Math.Round(edge * 100m / takings, 1))} of their takings. Staff for the peak; question the opening hours at the edges."));
    }

    private static void Payments(DesktopSalesReviewCurrency section, List<DesktopSalesReviewFinding> findings)
    {
        var headline = section.Headline;
        var cashName = TenderTypes.ReportingName(TenderTypes.Cash);
        var cash = section.ByPaymentMethod.FirstOrDefault(row => row.PaymentMethod == cashName);

        if (cash is not null && cash.ShareOfValuePercent >= CashHeavyPercent)
        {
            findings.Add(new("cash-heavy", DesktopSalesReviewSeverity.Review, DesktopSalesReviewTopic.Payments, section.Currency,
                $"Cash is {Pct(cash.ShareOfValuePercent)} of takings",
                $"{Money(section.Currency, cash.TotalAmount)} was taken in cash. "
                    + "Daily banking and a counted handover at every shift change matter more here than card fees."));
        }

        var needsReference = new[] { TenderTypes.ReportingName(TenderTypes.Ecocash), TenderTypes.ReportingName(TenderTypes.Innbucks) };
        foreach (var row in section.ByPaymentMethod.Where(row => needsReference.Contains(row.PaymentMethod) && row.WithoutReferenceCount > 0))
        {
            findings.Add(new("missing-reference", DesktopSalesReviewSeverity.Action, DesktopSalesReviewTopic.Payments, section.Currency,
                $"{Count(row.WithoutReferenceCount, $"{row.PaymentMethod} sale")} without a reference",
                $"Without the transaction reference the money cannot be matched to the {row.PaymentMethod} statement. Find the references and add them to these sales."));
        }

        if (headline.ShortTendered > 0)
        {
            var worst = section.ByShop.Where(shop => shop.ShortTendered > 0).MaxBy(shop => shop.ShortTendered)!;
            findings.Add(new("short-tender", DesktopSalesReviewSeverity.Action, DesktopSalesReviewTopic.Payments, section.Currency,
                $"Sales accepted {Money(section.Currency, headline.ShortTendered)} short",
                $"Some sales took less than their total — usually no coin to give the customer. {worst.Label} is the largest at {Money(section.Currency, worst.ShortTendered)}. "
                    + "Rounding totals to the smallest note or coin in circulation stops it."));
        }
    }

    private static void Pricing(DesktopSalesReviewCurrency section, List<DesktopSalesReviewFinding> findings)
    {
        if (section.PriceSpreads.Count == 0)
        {
            return;
        }

        var labels = ShopLabels(section);
        var top = section.PriceSpreads.Take(3).ToList();
        findings.Add(new("price-spread", DesktopSalesReviewSeverity.Review, DesktopSalesReviewTopic.Pricing, section.Currency,
            section.PriceSpreads.Count == 1
                ? $"{top[0].ItemCode} sells for different prices at different shops"
                : $"{section.PriceSpreads.Count} items sell for different prices at different shops",
            string.Join(" ", top.Select(spread =>
                $"{spread.ItemCode} {spread.ItemDescription}: {Money(section.Currency, spread.LowUnitPrice)} at {labels.GetValueOrDefault(spread.LowWarehouseCode, spread.LowWarehouseCode)} against {Money(section.Currency, spread.HighUnitPrice)} at {labels.GetValueOrDefault(spread.HighWarehouseCode, spread.HighWarehouseCode)} ({Pct(spread.SpreadPercent)}; {Money(section.Currency, spread.UpliftAtHighPrice)} at the higher price)."))
                + " Prices before VAT, value over quantity. Confirm each is a deliberate price list before changing anything."));
    }

    private static void LargeSales(DesktopSalesReviewCurrency section, List<DesktopSalesReviewFinding> findings)
    {
        var unnamed = section.LargeSales.Where(sale => sale.CustomerCode is null).ToList();
        if (unnamed.Count == 0)
        {
            return;
        }

        var labels = ShopLabels(section);
        findings.Add(new("large-sales", DesktopSalesReviewSeverity.Review, DesktopSalesReviewTopic.Shops, section.Currency,
            $"{Count(unnamed.Count, "trade-size sale")} went through a till with no customer named",
            $"{string.Join(", ", unnamed.Take(4).Select(sale => $"{Money(section.Currency, sale.TotalAmount)} at {labels.GetValueOrDefault(sale.WarehouseCode, sale.WarehouseCode)} on {Day(sale.DocDate)} ({sale.TimesShopAverage:0.#}× its average)"))}. "
                + "Regular buyers of this size belong on a customer account: credit control, a purchase history and standing orders."));
    }

    private static void Operators(DesktopSalesReviewCurrency section, List<DesktopSalesReviewFinding> findings)
    {
        var counters = section.ByShop.Where(shop => !IsVending(shop) && shop.SalesCount > 0).ToList();
        if (counters.Count < 2 || counters.Any(shop => shop.OperatorCount != 1))
        {
            return;
        }

        findings.Add(new("one-login-per-shop", DesktopSalesReviewSeverity.Note, DesktopSalesReviewTopic.Shops, section.Currency,
            "Every counter traded under a single login",
            "Each shop's sales carry one operator, so a cashier cannot be told from the counter they work at, and a short drawer cannot be traced to a shift. One login per cashier per shift makes both possible."));
    }

    private static void Tax(DesktopSalesReviewCurrency section, List<DesktopSalesReviewFinding> findings)
    {
        var shops = section.ByShop.Where(shop => shop.NetAmount >= 100m).ToList();
        if (shops.Count < 2)
        {
            return;
        }

        var standard = shops.Max(shop => shop.EffectiveVatPercent);
        foreach (var shop in shops.Where(shop => standard - shop.EffectiveVatPercent >= VatGapPoints))
        {
            var zeroRated = standard == 0 ? 0 : Math.Round(shop.NetAmount - shop.VatAmount * 100m / standard, 2, MidpointRounding.AwayFromZero);
            findings.Add(new("vat-gap", DesktopSalesReviewSeverity.Note, DesktopSalesReviewTopic.Tax, section.Currency,
                $"{shop.Label} charges less VAT than the other shops",
                $"{Pct(shop.EffectiveVatPercent)} of net against {Pct(standard)} elsewhere — about {Money(section.Currency, zeroRated)} of zero-rated or exempt lines. Check those items carry the tax code they should."));
        }
    }

    private static void Margin(DesktopSalesReview review, DesktopSalesReviewCurrency section, List<DesktopSalesReviewFinding> findings)
    {
        if (!review.Margin.Available || section.Headline.MarginPercent is not { } overall)
        {
            return;
        }

        var coverage = section.Headline.NetAmount == 0 ? 0 : Math.Round(section.Headline.CostedNetAmount * 100m / section.Headline.NetAmount, 0);
        if (coverage < MarginCoveragePercent)
        {
            findings.Add(new("margin-partial", DesktopSalesReviewSeverity.Note, DesktopSalesReviewTopic.Margin, section.Currency,
                $"Margin covers {Pct(coverage)} of sales",
                $"SAP has cost for {Money(section.Currency, section.Headline.CostedNetAmount)} of {Money(section.Currency, section.Headline.NetAmount)} before VAT; the rest is not posted yet. The {Pct(overall)} margin is measured on what is."));
        }

        var low = section.ByShop
            .Where(shop => shop.MarginPercent is { } margin && overall - margin >= MarginGapPoints)
            .OrderBy(shop => shop.MarginPercent)
            .ToList();
        if (low.Count > 0)
        {
            findings.Add(new("margin-low", DesktopSalesReviewSeverity.Review, DesktopSalesReviewTopic.Margin, section.Currency,
                low.Count == 1 ? $"{low[0].Label} earns a thin margin" : $"{low.Count} shops earn a thin margin",
                $"{string.Join(", ", low.Select(shop => $"{shop.Label} {Pct(shop.MarginPercent!.Value)}"))} against {Pct(overall)} overall. Price, discount or product mix — the item table says which."));
        }
    }

    private static void Discounts(DesktopSalesReviewCurrency section, ManagementCurrencySection? management, List<DesktopSalesReviewFinding> findings)
    {
        if (management is null)
        {
            return;
        }

        var given = management.ByItem.Sum(item => item.DiscountAmount);
        var lineValue = management.ByItem.Sum(item => item.NetAmount);
        if (lineValue == 0 || given * 100m / (lineValue + given) < DiscountPercent)
        {
            return;
        }

        var top = management.ByItem.MaxBy(item => item.DiscountAmount)!;
        findings.Add(new("discounts", DesktopSalesReviewSeverity.Review, DesktopSalesReviewTopic.Pricing, section.Currency,
            $"{Money(section.Currency, given)} given away in discounts",
            $"{Pct(Math.Round(given * 100m / (lineValue + given), 1))} of list value. The most on {top.ItemCode} {top.ItemDescription} ({Money(section.Currency, top.DiscountAmount)})."));
    }

    private static void LapsedVendors(DesktopSalesReviewCurrency section, ManagementCurrencySection? management, List<DesktopSalesReviewFinding> findings)
    {
        if (management is null || management.LapsedVendors.Count == 0)
        {
            return;
        }

        var lapsed = management.LapsedVendors.OrderByDescending(v => v.PreviousTotalAmount).ToList();
        findings.Add(new("lapsed-vendors", DesktopSalesReviewSeverity.Review, DesktopSalesReviewTopic.Shops, section.Currency,
            $"{Count(lapsed.Count, "vendor")} bought last period and not this one",
            $"{string.Join(", ", lapsed.Take(5).Select(v => $"{v.Label} ({Money(section.Currency, v.PreviousTotalAmount)}, last on {Day(v.LastSaleDate)})"))}. Worth a call before they are lost."));
    }

    // ── Wording ────────────────────────────────────────────────────────────────────────────────────

    private static bool IsVending(DesktopSalesReviewShopRow shop) =>
        shop.Channel == ManagementSalesRollup.SourceLabel(SaleSourceSystems.Vending);

    private static decimal UnitsPerSale(List<DesktopSalesReviewShopRow> shops)
    {
        var count = shops.Sum(shop => shop.SalesCount);
        return count == 0 ? 0 : shops.Sum(shop => shop.QuantitySold) / count;
    }

    private static Dictionary<string, string> ShopLabels(DesktopSalesReviewCurrency section) =>
        section.ByShop.ToDictionary(shop => shop.WarehouseCode, shop => shop.Label, StringComparer.OrdinalIgnoreCase);

    private static string Money(string currency, decimal amount) =>
        $"{currency} {amount.ToString("N2", CultureInfo.InvariantCulture)}";

    private static string Signed(string currency, decimal amount) =>
        (amount >= 0 ? "+" : "−") + Money(currency, Math.Abs(amount));

    private static string Pct(decimal value) => value.ToString("0.#", CultureInfo.InvariantCulture) + "%";

    private static string Ratio(decimal value, decimal average) =>
        average == 0 ? "—" : (value / average).ToString("0.#", CultureInfo.InvariantCulture);

    private static string Count(int count, string noun) =>
        $"{count.ToString("N0", CultureInfo.InvariantCulture)} {noun}{(count == 1 ? string.Empty : "s")}";

    private static string Day(DateTime date) => date.ToString("d MMM", CultureInfo.InvariantCulture);

    private static string Hour(int hour) => $"{hour % 24:00}:00";

    private static string Amounts(ManagementHealthBucket bucket) =>
        bucket.Value.Count == 0
            ? Count(bucket.SalesCount, "sale")
            : string.Join(" and ", bucket.Value.Select(v => Money(v.Currency, v.Amount)));
}
