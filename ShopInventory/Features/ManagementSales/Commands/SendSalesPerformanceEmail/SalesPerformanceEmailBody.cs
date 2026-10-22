using System.Globalization;
using System.Net;
using System.Text;
using ShopInventory.Common.Sales;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesReview;
using ShopInventory.Features.ManagementSales.Queries.GetSalesPerformance;

namespace ShopInventory.Features.ManagementSales.Commands.SendSalesPerformanceEmail;

/// <summary>
/// The management email: one scorecard of every line of business, what needs attention, then each of
/// shops, vending and vans in its own detail.
/// </summary>
/// <remarks>
/// Tables and inline styles only — the markup mail clients still render the same. Every value is
/// HTML-encoded: shop names, route names, rep names and item descriptions all come from data.
/// Currencies are never added together, and product lists are never mixed across lines of business.
/// </remarks>
internal static class SalesPerformanceEmailBody
{
    /// <summary>How many findings the "needs attention" list carries across all three businesses.</summary>
    public const int AttentionLimit = 8;

    private const int OutletLimit = 12;
    private const int ItemLimit = 5;
    private const int RouteLimit = 15;
    private const int RepLimit = 10;

    private const string Ink = "#0f1a22";
    private const string Muted = "#44525c";
    private const string Faint = "#7a8790";
    private const string Rule = "#dde4e8";
    private const string Tile = "#f3f6f8";
    private const string Accent = "#1d5f8a";
    private const string Up = "#1e7a3c";
    private const string Down = "#c03131";
    private const string Amber = "#b26f00";

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string Build(SalesPerformance performance, string title, string previousLabel, DateTime generatedAtCat)
    {
        var html = new StringBuilder();
        html.Append($"<div style=\"font-family:Segoe UI,Helvetica,Arial,sans-serif;color:{Ink};max-width:720px;margin:0 auto;\">");
        html.Append($"<p style=\"font-size:11px;letter-spacing:1px;color:{Accent};margin:0 0 4px;\"><b>SALES PERFORMANCE · SHOPS, VENDING AND VANS</b></p>");
        html.Append($"<h1 style=\"font-size:24px;margin:0 0 4px;\">{E(title)}</h1>");
        html.Append($"<p style=\"font-size:14px;color:{Muted};margin:0 0 16px;\">{Period(performance.FromDate, performance.ToDate)} · "
            + $"compared with {E(previousLabel)}, {Period(performance.PreviousFromDate, performance.PreviousToDate)}</p>");

        Totals(html, performance);
        Scorecard(html, performance);
        Attention(html, performance);

        Counter(html, performance.Shops, SaleBusinesses.Shops);
        Counter(html, performance.Vending, SaleBusinesses.Vending);
        Vans(html, performance.Vans);
        Partners(html, performance);

        html.Append($"<p style=\"font-size:13px;color:{Muted};margin:24px 0 4px;\">Takings include VAT throughout. Shop and vending credits are shown beside the takings, "
            + "not taken off them; van takings are before any credit notes. The Shop &amp; Vending review for the same period is attached as a PDF. "
            + "The figures can be opened for any period under Reports → Shop &amp; Vending Review and Van Sales → Reports → Performance.</p>");
        html.Append($"<p style=\"font-size:11px;color:#84919a;margin:0;\">Generated {D(generatedAtCat, "d MMM yyyy, HH:mm")} CAT.</p>");
        html.Append("</div>");

        return html.ToString();
    }

    // ── Group ──────────────────────────────────────────────────────────────────────

    private static void Totals(StringBuilder html, SalesPerformance performance)
    {
        if (performance.Totals.Count == 0)
        {
            html.Append($"<p style=\"font-size:14px;\">No sales were recorded in this period or the one before.</p>");
            return;
        }

        html.Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"4\" width=\"100%\" style=\"margin:0 0 8px;\"><tr>");
        foreach (var total in performance.Totals.Take(3))
        {
            html.Append($"<td style=\"background:{Tile};padding:12px;vertical-align:top;\">");
            html.Append($"<div style=\"font-size:10px;letter-spacing:1px;color:{Faint};\">ALL SALES · {E(total.Currency)}</div>");
            html.Append($"<div style=\"font-size:22px;font-weight:bold;margin:2px 0;\">{Money(total.Currency, total.Takings)}</div>");
            html.Append($"<div style=\"font-size:12px;color:{Muted};\">{ChangeText(total.ChangePercent)} · {total.Transactions.ToString("N0", Invariant)} transactions</div>");
            html.Append("</td>");
        }

        html.Append("</tr></table>");
    }

    private static void Scorecard(StringBuilder html, SalesPerformance performance)
    {
        if (performance.Scorecard.Count == 0)
        {
            return;
        }

        Heading(html, "Scorecard");
        foreach (var currency in performance.Totals)
        {
            var rows = performance.Scorecard
                .Where(row => string.Equals(row.Currency, currency.Currency, StringComparison.OrdinalIgnoreCase))
                .ToList();

            html.Append(TableOpen());
            html.Append(HeaderRow($"LINE OF BUSINESS · {currency.Currency}", "TAKINGS", "BEFORE", "CHANGE", "SHARE", "SALES", "AVERAGE", "MARGIN"));
            foreach (var row in rows)
            {
                html.Append($"<tr style=\"border-top:1px solid {Rule};\">");
                html.Append($"<td><b>{E(row.Label)}</b>{(row.Credited is > 0 ? $"<div style=\"font-size:11px;color:{Faint};\">{Number(row.Credited.Value)} credited back</div>" : string.Empty)}</td>");
                html.Append(Right(Number(row.Takings)));
                html.Append(Right(Number(row.PreviousTakings), Faint));
                html.Append(Right(ChangeHtml(row.ChangePercent)));
                html.Append(Right(Percent(row.SharePercent)));
                html.Append(Right(row.Transactions.ToString("N0", Invariant)));
                html.Append(Right(row.AverageTransaction is { } average ? Number(average) : "—"));
                html.Append(Right(MarginHtml(row.MarginPercent)));
                html.Append("</tr>");
            }

            html.Append($"<tr style=\"border-top:2px solid {Ink};font-weight:bold;\">");
            html.Append("<td>All sales</td>");
            html.Append(Right(Number(currency.Takings)));
            html.Append(Right(Number(currency.PreviousTakings), Faint));
            html.Append(Right(ChangeHtml(currency.ChangePercent)));
            html.Append(Right("100%"));
            html.Append(Right(currency.Transactions.ToString("N0", Invariant)));
            html.Append(Right(currency.Transactions > 0 ? Number(currency.Takings / currency.Transactions) : "—"));
            // Margins are of what each line costed, so they are not added across lines.
            html.Append(Right(""));
            html.Append("</tr></table>");
        }

        html.Append($"<p style=\"font-size:11px;color:{Faint};margin:0 0 8px;\">Sales are till sales for shops, settlements for vending and invoices for vans.</p>");
    }

    /// <summary>The findings worth acting on or checking, from every line of business, most urgent first.</summary>
    private static void Attention(StringBuilder html, SalesPerformance performance)
    {
        var findings = Sources(performance)
            .SelectMany(source => source.Findings.Select(finding => (source.Label, Finding: finding)))
            .Where(entry => entry.Finding.Severity != DesktopSalesReviewSeverity.Note)
            .OrderBy(entry => DesktopSalesReviewSeverity.Rank(entry.Finding.Severity))
            .Take(AttentionLimit)
            .ToList();

        Heading(html, "Needs attention");
        if (findings.Count == 0)
        {
            html.Append($"<p style=\"font-size:14px;color:{Muted};\">Nothing needs acting on or checking this period.</p>");
            return;
        }

        foreach (var (label, finding) in findings)
        {
            Finding(html, finding, label);
        }
    }

    // ── Shops and vending ──────────────────────────────────────────────────────────

    private static void Counter(StringBuilder html, SalesPerformanceCounter? counter, string business)
    {
        var label = counter?.Label ?? SaleBusinesses.Label(business);
        Section(html, label);

        if (counter is null || counter.Currencies.Count == 0)
        {
            html.Append($"<p style=\"font-size:14px;color:{Muted};\">No sales this period.</p>");
            return;
        }

        var outletNoun = business == SaleBusinesses.Vending ? "DEPOT" : "SHOP";
        foreach (var currency in counter.Currencies)
        {
            Tiles(html,
                ("Takings", Money(currency.Currency, currency.Takings),
                    currency.NetOfCredits != currency.Takings ? $"net of credits {Money(currency.Currency, currency.NetOfCredits)}" : $"{currency.DaysTraded} trading day{(currency.DaysTraded == 1 ? "" : "s")}"),
                (business == SaleBusinesses.Vending ? "Settlements" : "Sales", currency.Transactions.ToString("N0", Invariant), $"average {Money(currency.Currency, currency.AverageTransaction)}"),
                ("Gross margin", currency.MarginPercent is { } margin ? Percent(margin) : "—", currency.MarginPercent is null ? "not costed in SAP yet" : "on what SAP has costed"));

            if (currency.Outlets.Count > 0)
            {
                html.Append(TableOpen());
                html.Append(HeaderRow($"{outletNoun} · {currency.Currency}", "DAYS", "TAKINGS", "PER DAY", "CHANGE", "SHARE", "MARGIN"));
                foreach (var outlet in currency.Outlets.Take(OutletLimit))
                {
                    html.Append($"<tr style=\"border-top:1px solid {Rule};\">");
                    html.Append($"<td>{E(outlet.Label)}<span style=\"color:{Faint};\"> · {E(outlet.Channel)}</span></td>");
                    html.Append(Right(outlet.DaysTraded.ToString(Invariant)));
                    html.Append(Right(Number(outlet.Takings)));
                    html.Append(Right(Number(outlet.PerTradingDay)));
                    html.Append(Right(ChangeHtml(outlet.ChangePercent)));
                    html.Append(Right(Percent(outlet.SharePercent)));
                    html.Append(Right(MarginHtml(outlet.MarginPercent)));
                    html.Append("</tr>");
                }

                html.Append("</table>");
                More(html, currency.Outlets.Count, OutletLimit, outletNoun == "DEPOT" ? "depots" : "shops");
            }

            if (currency.TopItems.Count > 0)
            {
                html.Append(TableOpen());
                html.Append(HeaderRow($"BEST SELLERS · {currency.Currency}", "UNITS", "BEFORE VAT", "SHARE", "CHANGE"));
                foreach (var item in currency.TopItems.Take(ItemLimit))
                {
                    html.Append($"<tr style=\"border-top:1px solid {Rule};\">");
                    html.Append($"<td>{E(item.Description ?? item.ItemCode)}<span style=\"color:{Faint};\"> · {E(item.ItemCode)}</span></td>");
                    html.Append(Right(item.Quantity.ToString("N0", Invariant)));
                    html.Append(Right(Number(item.NetAmount)));
                    html.Append(Right(Percent(item.SharePercent)));
                    html.Append(Right(ChangeHtml(item.ChangePercent)));
                    html.Append("</tr>");
                }

                html.Append("</table>");
            }
        }

        Findings(html, counter.Findings);
    }

    // ── Vans ───────────────────────────────────────────────────────────────────────

    private static void Vans(StringBuilder html, SalesPerformanceVans vans)
    {
        Section(html, SaleBusinesses.Label(SaleBusinesses.Vans));

        if (vans.Currencies.Count == 0)
        {
            html.Append($"<p style=\"font-size:14px;color:{Muted};\">No van sales this period or the one before.</p>");
            Findings(html, vans.Findings);
            return;
        }

        var main = vans.MainCurrency;
        var takings = vans.Currencies.First();
        Tiles(html,
            ("Takings", Money(takings.Currency, takings.Takings), $"{ChangeText(takings.ChangePercent)}"
                + (takings.AverageDrop is { } drop ? $" · average drop {Number(drop)}" : string.Empty)),
            ("Invoices", vans.Invoices.ToString("N0", Invariant), $"{vans.PreviousInvoices.ToString("N0", Invariant)} before · {vans.TruckDays} rep-day{(vans.TruckDays == 1 ? "" : "s")}"),
            ("Strike rate", vans.StrikeRate is { } strike ? Rate(strike) : "—",
                vans.StrikeRate is null ? "no check-ins recorded" : vans.PreviousStrikeRate is { } before ? $"{Rate(before)} before" : $"{vans.ProductiveCalls:N0} productive calls"),
            ("Outlets", vans.OutletsThatBought.ToString("N0", Invariant), $"bought · {vans.NewOutlets:N0} new"
                + (vans.KilometresTravelled is { } km ? $" · {km:N0} km" : string.Empty)),
            ("Gross margin", takings.MarginPercent is { } vanMargin ? Percent(vanMargin) : "—",
                takings.MarginPercent is null ? "not costed in SAP yet" : "on what SAP has costed"));

        foreach (var other in vans.Currencies.Skip(1).Where(currency => currency.Takings > 0 || currency.PreviousTakings > 0))
        {
            html.Append($"<p style=\"font-size:13px;color:{Muted};margin:0 0 8px;\">Also {Money(other.Currency, other.Takings)} on {other.Invoices:N0} invoices, {ChangeText(other.ChangePercent)}.</p>");
        }

        var routes = vans.Routes.Where(route => route.HasRouteDay).ToList();
        if (routes.Count > 0)
        {
            html.Append(TableOpen());
            html.Append(HeaderRow($"ROUTE · {main}", "DAYS", "TAKINGS", "CHANGE", "CALLS MADE", "STRIKE", "MARGIN"));
            foreach (var route in routes.Take(RouteLimit))
            {
                html.Append($"<tr style=\"border-top:1px solid {Rule};\">");
                html.Append($"<td>{E(route.Label)}{(route.Territory is { Length: > 0 } territory ? $"<span style=\"color:{Faint};\"> · {E(territory)}</span>" : string.Empty)}</td>");
                html.Append(Right(route.TradingDays.ToString(Invariant)));
                html.Append(Right(Number(route.Takings.GetValueOrDefault(main))));
                html.Append(Right(ChangeHtml(route.ChangePercent)));
                html.Append(Right(route.CallCompliance is { } ccr ? Rate(ccr) : "—"));
                html.Append(Right(route.StrikeRate is { } pcr ? Rate(pcr) : "—"));
                html.Append(Right(MarginHtml(route.MarginPercent.TryGetValue(main, out var routeMargin) ? routeMargin : null)));
                html.Append("</tr>");
            }

            var unrouted = vans.Routes.FirstOrDefault(route => !route.HasRouteDay);
            if (unrouted is not null)
            {
                html.Append($"<tr style=\"border-top:1px solid {Rule};color:{Faint};\">");
                html.Append("<td><i>No route day opened</i></td>");
                html.Append(Right(unrouted.TradingDays.ToString(Invariant), Faint));
                html.Append(Right(Number(unrouted.Takings.GetValueOrDefault(main)), Faint));
                html.Append(Right("", Faint));
                html.Append(Right("", Faint));
                html.Append(Right("", Faint));
                html.Append(Right(MarginHtml(unrouted.MarginPercent.TryGetValue(main, out var unroutedMargin) ? unroutedMargin : null), Faint));
                html.Append("</tr>");
            }

            html.Append("</table>");
            More(html, routes.Count, RouteLimit, "routes");
            html.Append($"<p style=\"font-size:11px;color:{Faint};margin:-6px 0 8px;\">Calls made: planned shops called on over the day's round. Strike: calls that bought over calls made.</p>");
        }

        if (vans.Reps.Count > 0)
        {
            html.Append(TableOpen());
            html.Append(HeaderRow($"REP · {main}", "DAYS", "TAKINGS", "SHOPS BOUGHT", "STRIKE", "MARGIN"));
            foreach (var rep in vans.Reps.Take(RepLimit))
            {
                html.Append($"<tr style=\"border-top:1px solid {Rule};\">");
                html.Append($"<td>{E(rep.Name)}{(rep.Routes.Count > 0 ? $"<span style=\"color:{Faint};\"> · {E(string.Join(", ", rep.Routes))}</span>" : string.Empty)}</td>");
                html.Append(Right(rep.TradingDays.ToString(Invariant)));
                html.Append(Right(Number(rep.Takings.GetValueOrDefault(main))));
                html.Append(Right(rep.OutletsThatBought.ToString("N0", Invariant)));
                html.Append(Right(rep.StrikeRate is { } repStrike ? Rate(repStrike) : "—"));
                html.Append(Right(MarginHtml(rep.MarginPercent.TryGetValue(main, out var repMargin) ? repMargin : null)));
                html.Append("</tr>");
            }

            html.Append("</table>");
            More(html, vans.Reps.Count, RepLimit, "reps");
        }

        if (vans.TopItems.Count > 0)
        {
            html.Append(TableOpen());
            html.Append(HeaderRow($"MOST WIDELY SOLD · {main}", "SHOPS", "LINES", "TAKINGS", "MARGIN"));
            foreach (var item in vans.TopItems.Take(ItemLimit))
            {
                html.Append($"<tr style=\"border-top:1px solid {Rule};\">");
                html.Append($"<td>{E(item.Description ?? item.ItemCode)}<span style=\"color:{Faint};\"> · {E(item.ItemCode)}</span></td>");
                html.Append(Right(item.OutletsThatBought.ToString("N0", Invariant)));
                html.Append(Right(item.Lines.ToString("N0", Invariant)));
                html.Append(Right(Number(item.Takings)));
                html.Append(Right(MarginHtml(item.MarginPercent)));
                html.Append("</tr>");
            }

            html.Append("</table>");
        }

        Findings(html, vans.Findings);
    }

    // ── Business partners ──────────────────────────────────────────────────────────

    /// <summary>
    /// Every business partner on its own, a table per line of business closed by that line combined, then
    /// the lines combined side by side. Every partner is listed — none is cut to a top slice.
    /// </summary>
    private static void Partners(StringBuilder html, SalesPerformance performance)
    {
        Section(html, "Business partners");
        if (performance.Partners.Count == 0)
        {
            html.Append($"<p style=\"font-size:14px;color:{Muted};\">No business partner sold this period or the one before.</p>");
            return;
        }

        html.Append($"<p style=\"font-size:13px;color:{Muted};margin:0 0 10px;\">Each SAP business partner the sales were made as. For shops and vending that is the shop's own account "
            + "or the customer named on the sale; for vans it is the van's own account, which every van sale is invoiced to. Share is of the partner's own line of business."
            + (performance.PartnerNamesFromSap ? " Names are SAP's." : " SAP could not be reached, so names are as recorded on the sales.")
            + "</p>");

        var groups = SaleBusinesses.All
            .SelectMany(business => performance.Partners
                .Where(partner => partner.Business == business)
                .GroupBy(partner => partner.Currency, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Sum(partner => partner.Takings))
                .Select(group => (Business: business, Currency: group.Key, Partners: group.ToList())))
            .ToList();

        foreach (var (business, currency, partners) in groups)
        {
            var label = SaleBusinesses.Label(business);
            html.Append($"<h3 style=\"font-size:15px;margin:16px 0 6px;\">{E(label)} · {E(currency)} <span style=\"font-weight:normal;color:{Faint};\">· {partners.Count} partner{(partners.Count == 1 ? "" : "s")}</span></h3>");
            html.Append(TableOpen());
            html.Append(HeaderRow("BUSINESS PARTNER", "SALES", "TAKINGS", "BEFORE", "CHANGE", "SHARE", "MARGIN"));
            foreach (var partner in partners)
            {
                // The management report's hint already leads with the code; a van account's names its reps.
                var detail = partner.Hint is { Length: > 0 } hint
                    ? hint.Contains(partner.CardCode, StringComparison.OrdinalIgnoreCase) ? hint : $"{partner.CardCode} · {hint}"
                    : partner.CardCode;
                html.Append($"<tr style=\"border-top:1px solid {Rule};\">");
                html.Append($"<td>{E(partner.Name)}<div style=\"font-size:11px;color:{Faint};\">{E(detail)}"
                    + (partner.Credited is > 0 ? $" · {Number(partner.Credited.Value)} credited back" : string.Empty) + "</div></td>");
                html.Append(Right(partner.Transactions.ToString("N0", Invariant)));
                html.Append(Right(Number(partner.Takings)));
                html.Append(Right(Number(partner.PreviousTakings), Faint));
                html.Append(Right(partner.Takings == 0 && partner.PreviousTakings > 0 ? $"<span style=\"color:{Down};\">none</span>" : ChangeHtml(partner.ChangePercent)));
                html.Append(Right(Percent(partner.SharePercent)));
                html.Append(Right(partner.MarginPercent is { } margin ? Percent(margin) : "—"));
                html.Append("</tr>");
            }

            var takings = partners.Sum(partner => partner.Takings);
            var before = partners.Sum(partner => partner.PreviousTakings);
            html.Append($"<tr style=\"border-top:2px solid {Ink};font-weight:bold;\">");
            html.Append($"<td>{E(label)} combined</td>");
            html.Append(Right(partners.Sum(partner => partner.Transactions).ToString("N0", Invariant)));
            html.Append(Right(Number(takings)));
            html.Append(Right(Number(before), Faint));
            html.Append(Right(ChangeHtml(SalesPerformanceBuilder.Change(takings, before))));
            html.Append(Right("100%"));
            html.Append(Right(""));
            html.Append("</tr></table>");
        }

        // The lines of business combined, side by side, with the partner that carried each.
        html.Append("<h3 style=\"font-size:15px;margin:18px 0 6px;\">Combined per channel</h3>");
        foreach (var currency in performance.Totals)
        {
            var rows = groups.Where(group => string.Equals(group.Currency, currency.Currency, StringComparison.OrdinalIgnoreCase)).ToList();
            if (rows.Count == 0)
            {
                continue;
            }

            html.Append(TableOpen());
            html.Append(HeaderRow($"CHANNEL · {currency.Currency}", "PARTNERS", "SALES", "TAKINGS", "CHANGE", "SHARE", "LARGEST PARTNER"));
            foreach (var (business, _, partners) in rows)
            {
                var takings = partners.Sum(partner => partner.Takings);
                var biggest = partners.OrderByDescending(partner => partner.Takings).First();
                var traded = partners.Count(partner => partner.Takings > 0);
                html.Append($"<tr style=\"border-top:1px solid {Rule};\">");
                html.Append($"<td><b>{E(SaleBusinesses.Label(business))}</b></td>");
                html.Append(Right(traded == partners.Count ? traded.ToString(Invariant) : $"{traded} of {partners.Count}"));
                html.Append(Right(partners.Sum(partner => partner.Transactions).ToString("N0", Invariant)));
                html.Append(Right(Number(takings)));
                html.Append(Right(ChangeHtml(SalesPerformanceBuilder.Change(takings, partners.Sum(partner => partner.PreviousTakings)))));
                html.Append(Right(Percent(currency.Takings == 0 ? 0 : Math.Round(takings / currency.Takings * 100m, 1, MidpointRounding.AwayFromZero))));
                html.Append(Right(biggest.Takings > 0 ? $"{E(biggest.Name)} <span style=\"color:{Faint};\">{Percent(biggest.SharePercent)}</span>" : "—"));
                html.Append("</tr>");
            }

            html.Append($"<tr style=\"border-top:2px solid {Ink};font-weight:bold;\">");
            html.Append("<td>All channels</td>");
            html.Append(Right(rows.Sum(row => row.Partners.Count(partner => partner.Takings > 0)).ToString(Invariant)));
            html.Append(Right(rows.Sum(row => row.Partners.Sum(partner => partner.Transactions)).ToString("N0", Invariant)));
            html.Append(Right(Number(currency.Takings)));
            html.Append(Right(ChangeHtml(currency.ChangePercent)));
            html.Append(Right("100%"));
            html.Append(Right(""));
            html.Append("</tr></table>");
        }
    }

    // ── Pieces ─────────────────────────────────────────────────────────────────────

    private static IEnumerable<(string Label, List<SalesPerformanceFinding> Findings)> Sources(SalesPerformance performance)
    {
        if (performance.Shops is { } shops)
        {
            yield return (shops.Label, shops.Findings);
        }

        if (performance.Vending is { } vending)
        {
            yield return (vending.Label, vending.Findings);
        }

        yield return (SaleBusinesses.Label(SaleBusinesses.Vans), performance.Vans.Findings);
    }

    private static void Findings(StringBuilder html, List<SalesPerformanceFinding> findings)
    {
        html.Append("<h3 style=\"font-size:15px;margin:14px 0 8px;\">What the figures say</h3>");
        if (findings.Count == 0)
        {
            html.Append($"<p style=\"font-size:14px;color:{Muted};\">Nothing stood out this period.</p>");
            return;
        }

        foreach (var finding in findings)
        {
            Finding(html, finding, null);
        }
    }

    private static void Finding(StringBuilder html, SalesPerformanceFinding finding, string? business)
    {
        var (label, color) = finding.Severity switch
        {
            DesktopSalesReviewSeverity.Action => ("ACT", Down),
            DesktopSalesReviewSeverity.Review => ("CHECK", Amber),
            _ => ("NOTE", Accent)
        };

        html.Append($"<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" width=\"100%\" style=\"margin:0 0 10px;border-left:3px solid {color};\"><tr>");
        html.Append($"<td style=\"padding:2px 10px;vertical-align:top;width:52px;font-size:11px;font-weight:bold;color:{color};\">{label}</td>");
        html.Append("<td style=\"padding:0 0 0 4px;\">");
        html.Append($"<div style=\"font-size:14px;font-weight:bold;\">{(business is null ? string.Empty : $"<span style=\"color:{Faint};font-weight:normal;\">{E(business)} · </span>")}{E(finding.Title)}</div>");
        html.Append($"<div style=\"font-size:13px;color:{Muted};line-height:1.45;\">{E(finding.Detail)}</div>");
        html.Append("</td></tr></table>");
    }

    /// <summary>A margin cell: the percentage, a loss in red, and a dash where SAP has costed nothing.</summary>
    private static string MarginHtml(decimal? margin) =>
        margin is not { } value
            ? "—"
            : value < 0 ? $"<span style=\"color:#b42318;\">{Percent(value)}</span>" : Percent(value);

    private static void Tiles(StringBuilder html, params (string Label, string Value, string Detail)[] tiles)
    {
        html.Append("<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"4\" width=\"100%\" style=\"margin:0 0 8px;\"><tr>");
        foreach (var (label, value, detail) in tiles)
        {
            html.Append($"<td style=\"background:{Tile};padding:10px;vertical-align:top;width:{100 / tiles.Length}%;\">");
            html.Append($"<div style=\"font-size:10px;letter-spacing:1px;color:{Faint};\">{E(label.ToUpperInvariant())}</div>");
            html.Append($"<div style=\"font-size:18px;font-weight:bold;margin:2px 0;\">{E(value)}</div>");
            html.Append($"<div style=\"font-size:12px;color:{Muted};\">{E(detail)}</div>");
            html.Append("</td>");
        }

        html.Append("</tr></table>");
    }

    private static void Section(StringBuilder html, string label) =>
        html.Append($"<h2 style=\"font-size:19px;margin:28px 0 8px;padding:0 0 4px;border-bottom:2px solid {Accent};\">{E(label)}</h2>");

    private static void Heading(StringBuilder html, string label) =>
        html.Append($"<h2 style=\"font-size:17px;margin:20px 0 8px;\">{E(label)}</h2>");

    private static void More(StringBuilder html, int count, int shown, string noun)
    {
        if (count > shown)
        {
            html.Append($"<p style=\"font-size:11px;color:{Faint};margin:-6px 0 8px;\">The top {shown} of {count} {noun}.</p>");
        }
    }

    private static string TableOpen() =>
        "<table role=\"presentation\" cellpadding=\"4\" cellspacing=\"0\" width=\"100%\" style=\"font-size:13px;border-collapse:collapse;margin:0 0 12px;\">";

    private static string HeaderRow(string first, params string[] rest) =>
        $"<tr style=\"color:{Faint};font-size:11px;\"><td>{E(first)}</td>{string.Concat(rest.Select(cell => $"<td align=\"right\">{E(cell)}</td>"))}</tr>";

    /// <summary>A right-aligned cell. Its content is already markup: callers encode data before passing it.</summary>
    private static string Right(string content, string? color = null) =>
        $"<td align=\"right\"{(color is null ? string.Empty : $" style=\"color:{color};\"")}>{content}</td>";

    private static string ChangeHtml(decimal? change)
    {
        if (change is not { } value)
        {
            return $"<span style=\"color:{Faint};\">new</span>";
        }

        var color = value > 0 ? Up : value < 0 ? Down : Muted;
        var arrow = value > 0 ? "▲" : value < 0 ? "▼" : "";
        return $"<span style=\"color:{color};\">{arrow}{Math.Abs(value).ToString("0.#", Invariant)}%</span>";
    }

    private static string ChangeText(decimal? change) =>
        change is not { } value ? "nothing to compare with"
        : value == 0 ? "level with before"
        : $"{(value > 0 ? "up" : "down")} {Math.Abs(value).ToString("0.#", Invariant)}% on before";

    private static string Period(DateTime from, DateTime to) =>
        from.Date == to.Date ? D(from, "ddd d MMM yyyy") : $"{D(from, "ddd d MMM")} to {D(to, "ddd d MMM yyyy")}";

    private static string Money(string currency, decimal amount) => $"{currency} {amount.ToString("N2", Invariant)}";

    private static string Number(decimal amount) => amount.ToString("N2", Invariant);

    private static string Percent(decimal value) => $"{value.ToString("0.#", Invariant)}%";

    private static string Rate(double value) => $"{(value * 100).ToString("0", Invariant)}%";

    private static string D(DateTime date, string format) => date.ToString(format, Invariant);

    private static string E(string text) => WebUtility.HtmlEncode(text);
}
