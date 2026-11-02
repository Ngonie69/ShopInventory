using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Notifications;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

/// <summary>
/// Watches the exchange rate SAP books ZiG documents at, and tells people when it moves, when it is
/// re-keyed, and before it runs out.
/// </summary>
/// <remarks>
/// <para><b>Why.</b> Sales orders now post to SAP in ZiG, and SAP converts each one to USD at the
/// rate keyed for its document date. Nothing in the app reads that rate, so a change to it — or a
/// slip like the one-day 50s on five month-ends in 2025, between 36s — reached the books unseen.
/// And SAP keys rates a day at a time: production had ZiG rates through 30 Nov 2026 and none from
/// 1 Dec, the day SAP would start refusing every ZiG document with "Update the exchange rate".</para>
///
/// <para><b>Each pass</b> reads SAP's rate for yesterday, today and each day ahead up to
/// <see cref="ExchangeRateMonitorSettings.LookAheadDays"/>, stopping at the first day with none.
/// Every value seen is kept (<see cref="ExchangeRateObservationEntity"/>), so the history survives a
/// rate being re-keyed in SAP. Alerts are deduplicated by key in
/// <see cref="ExchangeRateMonitorAlertEntity"/>, so a pass on any node raises each one once.</para>
///
/// <para><b>Alerts go to the bell and by email</b>, one notification per currency per pass however
/// many findings it has.</para>
/// </remarks>
public sealed class ExchangeRateMonitor(
    ApplicationDbContext context,
    ISAPServiceLayerClient sapClient,
    INotificationService notificationService,
    IEmailService emailService,
    IOptions<ExchangeRateMonitorSettings> settings,
    IOptions<SystemHealthAlertSettings> healthAlertSettings,
    ILogger<ExchangeRateMonitor> logger)
{
    /// <summary>How long the monitor may fail to read SAP before it says it is blind.</summary>
    internal static readonly TimeSpan BlindAfter = TimeSpan.FromHours(2);

    private const int MessageMaxLength = 1000;
    private const int AlertMessageMaxLength = 2000;
    private const int ErrorMaxLength = 1000;

    public async Task<IReadOnlyList<ExchangeRateMonitorPassResult>> CheckAsync(
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        var options = settings.Value;
        var results = new List<ExchangeRateMonitorPassResult>();

        SapCurrencyRateSettings? rateSettings = null;
        Exception? settingsError = null;

        try
        {
            rateSettings = await sapClient.GetCurrencyRateSettingsAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            settingsError = ex;
        }

        IReadOnlyList<SapPriceListFactor>? priceLists = null;
        if (rateSettings is not null)
        {
            try
            {
                priceLists = await sapClient.GetPriceListFactorsAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // The factors are context for the rate, not the rate: a pass without them still watches it.
                logger.LogWarning(ex, "Could not read the SAP price list factors for the rate monitor.");
            }
        }

        foreach (var currency in options.MonitoredCurrencies())
        {
            results.Add(await CheckCurrencyAsync(
                currency, rateSettings, settingsError, priceLists, options, utcNow, cancellationToken));
        }

        return results;
    }

    private async Task<ExchangeRateMonitorPassResult> CheckCurrencyAsync(
        string currency,
        SapCurrencyRateSettings? rateSettings,
        Exception? settingsError,
        IReadOnlyList<SapPriceListFactor>? priceLists,
        ExchangeRateMonitorSettings options,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var state = await context.ExchangeRateMonitorStates.FindAsync([currency], cancellationToken);
        if (state is null)
        {
            state = new ExchangeRateMonitorStateEntity { Currency = currency };
            context.ExchangeRateMonitorStates.Add(state);
        }

        var today = ExchangeRateMonitorDates.TodayCat(utcNow);
        var previousLocal = state.LocalCurrency;
        var previousIndirect = state.IndirectQuotation;
        state.LastCheckedAtUtc = utcNow;

        List<(DateOnly Date, SapCurrencyRateLookup Lookup)> readings;
        try
        {
            if (rateSettings is null)
            {
                throw settingsError ?? new InvalidOperationException("SAP's currency settings could not be read.");
            }

            if (string.Equals(rateSettings.LocalCurrency, currency, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"{currency} is SAP's local currency, so it has no exchange rate to watch.");
            }

            readings = await ReadWindowAsync(currency, today, options, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            state.LastError = Truncate(DescribeFailure(ex), ErrorMaxLength);
            await context.SaveChangesAsync(CancellationToken.None);

            logger.LogWarning(ex, "The {Currency} rate monitor could not read SAP.", currency);

            var blind = BlindFinding(currency, state, today, utcNow);
            if (blind is not null)
            {
                await RaiseAsync(currency, [blind], rateSettings, utcNow, cancellationToken);
            }

            return new ExchangeRateMonitorPassResult(currency, false, state.LastError, 0);
        }

        var firstSuccess = state.LastSuccessAtUtc is null;
        state.LastSuccessAtUtc = utcNow;
        state.LastError = null;
        state.LocalCurrency = rateSettings.LocalCurrency;
        state.IndirectQuotation = rateSettings.IndirectQuotation;

        var keyedThrough = KeyedThrough(readings, today);
        state.KeyedThrough = keyedThrough;
        state.KeyedBeyondWindow = keyedThrough == today.AddDays(Math.Max(0, options.LookAheadDays));

        if (priceLists is not null)
        {
            state.PriceListFactorsJson = JsonSerializer.Serialize(
                DerivedListsIn(currency, priceLists).Select(list => new ExchangeRatePriceListFactorDto
                {
                    PriceListNo = list.PriceListNo,
                    Name = list.Name,
                    BasePriceList = list.BasePriceList,
                    BasePriceListName = priceLists.FirstOrDefault(p => p.PriceListNo == list.BasePriceList)?.Name,
                    Factor = list.Factor
                }).ToList());
        }

        await RecordObservationsAsync(currency, readings, rateSettings, today, options, utcNow, cancellationToken);
        await context.SaveChangesAsync(CancellationToken.None);

        if (firstSuccess)
        {
            await BackfillAsync(currency, rateSettings, today, options, utcNow, cancellationToken);
        }

        var findings = new List<ExchangeRateMonitorFinding>();
        findings.AddRange(await RateFindingsAsync(currency, readings, rateSettings, today, options, cancellationToken));
        findings.AddRange(CoverageFindings(currency, readings, keyedThrough, state.KeyedBeyondWindow, today, options));

        if ((previousLocal is not null && !string.Equals(previousLocal, rateSettings.LocalCurrency, StringComparison.Ordinal))
            || (previousIndirect is not null && previousIndirect != rateSettings.IndirectQuotation))
        {
            findings.Add(new ExchangeRateMonitorFinding(
                $"{currency}:settings:{rateSettings.LocalCurrency}:{(rateSettings.IndirectQuotation ? "indirect" : "direct")}",
                ExchangeRateMonitorAlertKinds.SettingsChanged,
                "Error",
                null,
                $"SAP's currency settings changed: local currency {previousLocal} → {rateSettings.LocalCurrency}, "
                    + $"rates quoted {Quotation(previousIndirect)} → {Quotation(rateSettings.IndirectQuotation)}. "
                    + $"Every {currency} rate now means something different."));
        }

        var raised = await RaiseAsync(currency, findings, rateSettings, utcNow, cancellationToken);

        return new ExchangeRateMonitorPassResult(currency, true, null, raised);
    }

    private async Task<List<(DateOnly Date, SapCurrencyRateLookup Lookup)>> ReadWindowAsync(
        string currency,
        DateOnly today,
        ExchangeRateMonitorSettings options,
        CancellationToken cancellationToken)
    {
        var readings = new List<(DateOnly, SapCurrencyRateLookup)>();
        var lastDay = today.AddDays(Math.Max(0, options.LookAheadDays));

        // Yesterday is read for comparison only: a gap there is history, not a problem to raise.
        for (var day = today.AddDays(-1); day <= lastDay; day = day.AddDays(1))
        {
            var lookup = await sapClient.GetCurrencyRateAsync(
                currency, day.ToDateTime(TimeOnly.MinValue), cancellationToken);
            readings.Add((day, lookup));

            if (!lookup.IsKeyed && day > today)
            {
                // The first unkeyed day ahead is where the rates run out; nothing past it is read.
                break;
            }
        }

        return readings;
    }

    /// <summary>
    /// Reads the days before yesterday into the history, once, so the page has one from the start.
    /// Nothing found here is alerted: it is history the monitor was not running for.
    /// </summary>
    private async Task BackfillAsync(
        string currency,
        SapCurrencyRateSettings rateSettings,
        DateOnly today,
        ExchangeRateMonitorSettings options,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var days = Math.Max(0, options.HistoryBackfillDays);
        if (days < 2)
        {
            return;
        }

        try
        {
            var readings = new List<(DateOnly, SapCurrencyRateLookup)>();
            for (var day = today.AddDays(-days); day < today.AddDays(-1); day = day.AddDays(1))
            {
                readings.Add((day, await sapClient.GetCurrencyRateAsync(
                    currency, day.ToDateTime(TimeOnly.MinValue), cancellationToken)));
            }

            await RecordObservationsAsync(currency, readings, rateSettings, today, options, utcNow, cancellationToken);
            await context.SaveChangesAsync(CancellationToken.None);

            logger.LogInformation("Back-filled {Days} days of {Currency} rate history from SAP.", readings.Count, currency);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // History is a convenience; the watch itself already has what it needs.
            logger.LogWarning(ex, "Could not back-fill the {Currency} rate history from SAP.", currency);
            context.ChangeTracker.Clear();
        }
    }

    private static DateOnly? KeyedThrough(List<(DateOnly Date, SapCurrencyRateLookup Lookup)> readings, DateOnly today)
    {
        DateOnly? through = null;
        foreach (var (date, lookup) in readings.Where(r => r.Date >= today).OrderBy(r => r.Date))
        {
            if (!lookup.IsKeyed)
            {
                break;
            }

            through = date;
        }

        return through;
    }

    private async Task RecordObservationsAsync(
        string currency,
        List<(DateOnly Date, SapCurrencyRateLookup Lookup)> readings,
        SapCurrencyRateSettings rateSettings,
        DateOnly today,
        ExchangeRateMonitorSettings options,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var from = readings.Min(r => r.Date);
        var through = today.AddDays(Math.Max(0, options.LookAheadDays) + 1);

        var current = await context.ExchangeRateObservations
            .Where(o => o.Currency == currency
                        && o.SupersededAtUtc == null
                        && o.RateDate >= from
                        && o.RateDate <= through)
            .ToListAsync(cancellationToken);

        var byDate = current.ToDictionary(o => o.RateDate);
        var added = new List<ExchangeRateObservationEntity>();

        foreach (var (date, lookup) in readings)
        {
            byDate.TryGetValue(date, out var existing);

            if (!lookup.IsKeyed)
            {
                // Deleted in SAP, or never keyed. Either way it is no longer SAP's rate for the day.
                if (existing is not null)
                {
                    existing.SupersededAtUtc = utcNow;
                }

                continue;
            }

            if (existing is not null && existing.Rate == lookup.Rate && existing.IndirectQuotation == rateSettings.IndirectQuotation)
            {
                existing.LastSeenAtUtc = utcNow;
                continue;
            }

            if (existing is not null)
            {
                existing.SupersededAtUtc = utcNow;
            }

            added.Add(new ExchangeRateObservationEntity
            {
                Currency = currency,
                RateDate = date,
                Rate = lookup.Rate,
                IndirectQuotation = rateSettings.IndirectQuotation,
                LocalCurrency = rateSettings.LocalCurrency,
                ReplacedRate = existing?.Rate,
                FirstSeenAtUtc = utcNow,
                LastSeenAtUtc = utcNow
            });
        }

        // Rows for days past the first unkeyed day are not read this pass and are left as they are.

        // Retire first, then add: the current-value index is unique per day, and EF cannot order an
        // update ahead of an insert by a filtered index it does not evaluate.
        await context.SaveChangesAsync(CancellationToken.None);
        context.ExchangeRateObservations.AddRange(added);
    }

    private async Task<List<ExchangeRateMonitorFinding>> RateFindingsAsync(
        string currency,
        List<(DateOnly Date, SapCurrencyRateLookup Lookup)> readings,
        SapCurrencyRateSettings rateSettings,
        DateOnly today,
        ExchangeRateMonitorSettings options,
        CancellationToken cancellationToken)
    {
        var findings = new List<ExchangeRateMonitorFinding>();
        var from = readings.Min(r => r.Date);
        var through = readings.Max(r => r.Date);

        // Re-keyed days, from the rows rather than from this pass's diff: if the alert for one could
        // not be sent, the next pass still finds it.
        var edited = await context.ExchangeRateObservations
            .AsNoTracking()
            .Where(o => o.Currency == currency
                        && o.SupersededAtUtc == null
                        && o.ReplacedRate != null
                        && o.RateDate >= from
                        && o.RateDate <= through)
            .ToListAsync(cancellationToken);

        var editedDates = edited.Select(o => o.RateDate).ToHashSet();

        // A rate re-keyed for the rest of the month is one event, not one per day: consecutive days
        // re-keyed from the same value to the same value are reported as one range.
        foreach (var run in ConsecutiveRuns(edited))
        {
            var first = run[0];
            var last = run[^1];
            var replaced = first.ReplacedRate!.Value;

            var keys = run
                .Select(row => $"{currency}:edited:{Iso(row.RateDate)}:{Number(replaced)}->{Number(row.Rate)}:{row.FirstSeenAtUtc.Ticks}")
                .ToList();

            foreach (var row in run)
            {
                var dayBefore = readings.FirstOrDefault(r => r.Date == row.RateDate.AddDays(-1));
                if (dayBefore.Lookup is { IsKeyed: true } before && before.Rate != row.Rate)
                {
                    // The same event seen as a day-over-day move: claimed here so it is not raised again.
                    keys.Add(ChangeKey(currency, row.RateDate, before.Rate, row.Rate));
                }
            }

            var days = first.RateDate == last.RateDate
                ? Day(first.RateDate)
                : $"{Day(first.RateDate)} to {Day(last.RateDate)}";

            var message = new StringBuilder()
                .Append(CultureInfo.InvariantCulture, $"{days}: SAP's {currency} rate was re-keyed from ")
                .Append(Describe(replaced, currency, rateSettings))
                .Append(" to ")
                .Append(Describe(first.Rate, currency, rateSettings))
                .Append(CultureInfo.InvariantCulture, $" ({Percent(replaced, first.Rate)}")
                .Append(Movement(replaced, first.Rate, currency, rateSettings))
                .Append(").")
                .Append(first.RateDate <= today
                    ? $" {currency} documents already booked for {(first.RateDate == today ? "today" : "those days")} keep the old rate."
                    : string.Empty)
                .Append(LargeChangeNote(replaced, first.Rate, options))
                .ToString();

            findings.Add(new ExchangeRateMonitorFinding(
                keys[0], ExchangeRateMonitorAlertKinds.Edited, "Warning", first.RateDate, message, keys.Skip(1).ToList(),
                replaced, first.Rate));
        }

        var keyed = readings.Where(r => r.Lookup.IsKeyed).OrderBy(r => r.Date).ToList();
        for (var i = 1; i < keyed.Count; i++)
        {
            var (previousDate, previous) = keyed[i - 1];
            var (date, lookup) = keyed[i];

            if (previousDate != date.AddDays(-1) || previous.Rate == lookup.Rate || editedDates.Contains(date))
            {
                continue;
            }

            var message = new StringBuilder()
                .Append(CultureInfo.InvariantCulture, $"From {Day(date)}: ")
                .Append(Describe(lookup.Rate, currency, rateSettings))
                .Append(", was ")
                .Append(Describe(previous.Rate, currency, rateSettings))
                .Append(CultureInfo.InvariantCulture, $" on {Day(previousDate)} ({Percent(previous.Rate, lookup.Rate)}")
                .Append(Movement(previous.Rate, lookup.Rate, currency, rateSettings))
                .Append(").")
                .Append(LargeChangeNote(previous.Rate, lookup.Rate, options))
                .ToString();

            findings.Add(new ExchangeRateMonitorFinding(
                ChangeKey(currency, date, previous.Rate, lookup.Rate),
                ExchangeRateMonitorAlertKinds.Change,
                "Warning",
                date,
                message,
                From: previous.Rate,
                To: lookup.Rate));
        }

        return findings;
    }

    private static List<List<ExchangeRateObservationEntity>> ConsecutiveRuns(List<ExchangeRateObservationEntity> rows)
    {
        var runs = new List<List<ExchangeRateObservationEntity>>();
        foreach (var row in rows.OrderBy(o => o.RateDate))
        {
            var run = runs.Count > 0 ? runs[^1] : null;
            if (run is not null
                && run[^1].RateDate.AddDays(1) == row.RateDate
                && run[^1].ReplacedRate == row.ReplacedRate
                && run[^1].Rate == row.Rate)
            {
                run.Add(row);
            }
            else
            {
                runs.Add([row]);
            }
        }

        return runs;
    }

    private static List<ExchangeRateMonitorFinding> CoverageFindings(
        string currency,
        List<(DateOnly Date, SapCurrencyRateLookup Lookup)> readings,
        DateOnly? keyedThrough,
        bool keyedBeyondWindow,
        DateOnly today,
        ExchangeRateMonitorSettings options)
    {
        var findings = new List<ExchangeRateMonitorFinding>();
        var todayReading = readings.FirstOrDefault(r => r.Date == today);

        if (keyedThrough is null)
        {
            var yesterday = readings.FirstOrDefault(r => r.Date == today.AddDays(-1));
            findings.Add(new ExchangeRateMonitorFinding(
                $"{currency}:missing:{Iso(today)}",
                ExchangeRateMonitorAlertKinds.MissingToday,
                "Error",
                today,
                $"SAP has no {currency} rate keyed for today, {Day(today)}. SAP refuses {currency} documents dated a day "
                    + $"without a rate (\"{todayReading.Lookup?.NotKeyedMessage ?? "Update the exchange rate"}\"), so {currency} "
                    + "orders and invoices dated today will not post until it is keyed in SAP "
                    + "(Administration → Exchange Rates and Indexes)."
                    + (yesterday.Lookup is { IsKeyed: true } y
                        ? $" Yesterday's rate was {Number(y.Rate)}."
                        : string.Empty)));
            return findings;
        }

        var warningDays = Math.Max(0, options.RunningOutWarningDays);
        var daysLeft = keyedThrough.Value.DayNumber - today.DayNumber;

        if (!keyedBeyondWindow && daysLeft < warningDays)
        {
            var firstGap = keyedThrough.Value.AddDays(1);
            findings.Add(new ExchangeRateMonitorFinding(
                $"{currency}:runningout:{Iso(keyedThrough.Value)}:{Iso(today)}",
                ExchangeRateMonitorAlertKinds.RunningOut,
                daysLeft <= 1 ? "Error" : "Warning",
                firstGap,
                $"SAP has {currency} rates keyed only through {Day(keyedThrough.Value)}"
                    + (daysLeft == 0 ? " (today)" : $" ({daysLeft} more day{(daysLeft == 1 ? "" : "s")})")
                    + $". From {Day(firstGap)} SAP will refuse every {currency} document dated that day until the rate "
                    + "is keyed (Administration → Exchange Rates and Indexes)."));
        }

        return findings;
    }

    private ExchangeRateMonitorFinding? BlindFinding(
        string currency,
        ExchangeRateMonitorStateEntity state,
        DateOnly today,
        DateTime utcNow)
    {
        if (state.LastSuccessAtUtc is not { } lastSuccess || utcNow - lastSuccess < BlindAfter)
        {
            return null;
        }

        return new ExchangeRateMonitorFinding(
            $"{currency}:blind:{Iso(today)}",
            ExchangeRateMonitorAlertKinds.CheckFailing,
            "Warning",
            null,
            $"The {currency} rate monitor has not been able to read SAP since "
                + $"{AuditService.ToCAT(lastSuccess).ToString("d MMM HH:mm", CultureInfo.InvariantCulture)}, so a rate change "
                + $"would not be noticed. Last error: {state.LastError}");
    }

    /// <summary>
    /// Sends the findings no earlier pass has raised, as one notification and one email, and records
    /// them. Nothing is recorded unless the notification is stored, so a failed send is retried.
    /// </summary>
    private async Task<int> RaiseAsync(
        string currency,
        List<ExchangeRateMonitorFinding> findings,
        SapCurrencyRateSettings? rateSettings,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        if (findings.Count == 0)
        {
            return 0;
        }

        var keys = findings.SelectMany(f => f.AllKeys).ToList();
        var raised = await context.ExchangeRateMonitorAlerts
            .AsNoTracking()
            .Where(a => keys.Contains(a.AlertKey))
            .Select(a => a.AlertKey)
            .ToListAsync(cancellationToken);
        var raisedKeys = raised.ToHashSet(StringComparer.Ordinal);

        var fresh = findings.Where(f => !raisedKeys.Contains(f.Key)).ToList();
        if (fresh.Count == 0)
        {
            return 0;
        }

        var severity = fresh.Any(f => f.Severity == "Error") ? "Error"
            : fresh.Any(f => f.Severity == "Warning") ? "Warning"
            : "Info";
        var title = Title(currency, fresh, rateSettings);
        var body = string.Join(" ", fresh.Select(f => f.Message));

        try
        {
            var request = ModuleNotificationFactory.CreateBroadcastNotification(
                title,
                Truncate(body, MessageMaxLength),
                severity,
                "SAP",
                "ExchangeRate",
                currency,
                "/exchange-rates",
                new Dictionary<string, string>
                {
                    ["currency"] = currency,
                    ["kinds"] = string.Join(",", fresh.Select(f => f.Kind).Distinct()),
                    ["alertKeys"] = string.Join(",", fresh.Select(f => f.Key)),
                    ["checkedAtUtc"] = utcNow.ToString("O", CultureInfo.InvariantCulture)
                });

            await notificationService.CreateNotificationAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not raise {Count} {Currency} rate alert(s); the next pass tries again.", fresh.Count, currency);
            return 0;
        }

        foreach (var finding in fresh)
        {
            foreach (var key in finding.AllKeys.Where(k => !raisedKeys.Contains(k)))
            {
                context.ExchangeRateMonitorAlerts.Add(new ExchangeRateMonitorAlertEntity
                {
                    AlertKey = key,
                    Currency = currency,
                    Kind = finding.Kind,
                    Severity = finding.Severity,
                    RateDate = finding.RateDate,
                    Message = Truncate(finding.Message, AlertMessageMaxLength),
                    RaisedAtUtc = utcNow
                });
                raisedKeys.Add(key);
            }
        }

        try
        {
            await context.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateException ex)
        {
            // Another pass, on another node or from the page's Check now, raised the same alert first.
            logger.LogWarning(ex, "A {Currency} rate alert was already recorded by a concurrent pass.", currency);
            context.ChangeTracker.Clear();
        }

        await QueueEmailAsync(title, fresh, cancellationToken);

        logger.LogWarning("Raised {Count} {Currency} rate alert(s): {Title} — {Body}", fresh.Count, currency, title, body);
        return fresh.Count;
    }

    private async Task QueueEmailAsync(
        string title,
        List<ExchangeRateMonitorFinding> findings,
        CancellationToken cancellationToken)
    {
        var recipients = settings.Value.AlertRecipients
            .Concat(settings.Value.AlertRecipients.Count == 0 ? healthAlertSettings.Value.AlertRecipients : [])
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .Select(address => address.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (recipients.Count == 0)
        {
            return;
        }

        var html = new StringBuilder()
            .Append("<p><strong>").Append(WebUtility.HtmlEncode(title)).Append("</strong></p><ul>");
        foreach (var finding in findings)
        {
            html.Append("<li>").Append(WebUtility.HtmlEncode(finding.Message)).Append("</li>");
        }

        html.Append("</ul><p>The full rate history is on the Exchange Rates page in Shop Inventory.</p>");

        try
        {
            await emailService.QueueEmailAsync(new SendEmailRequest
            {
                To = recipients,
                Subject = title,
                Body = html.ToString(),
                IsHtml = true
            }, "ExchangeRateMonitor", cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The bell has it; an email that could not be queued is logged, not retried.
            logger.LogError(ex, "Could not queue the rate alert email \"{Title}\".", title);
        }
    }

    private static string Title(string currency, List<ExchangeRateMonitorFinding> findings, SapCurrencyRateSettings? rateSettings)
    {
        if (findings.Count > 1)
        {
            var worst = findings.FirstOrDefault(f => f.Kind == ExchangeRateMonitorAlertKinds.MissingToday)
                        ?? findings.FirstOrDefault(f => f.Kind is ExchangeRateMonitorAlertKinds.Change or ExchangeRateMonitorAlertKinds.Edited)
                        ?? findings[0];
            return $"{Title(currency, [worst], rateSettings)} (+{findings.Count - 1} more)";
        }

        var finding = findings[0];
        var move = finding.From is { } from && finding.To is { } to ? $": {Number(from)} → {Number(to)}" : string.Empty;
        return finding.Kind switch
        {
            ExchangeRateMonitorAlertKinds.Change => $"{currency} rate changes in SAP{move} from {Day(finding.RateDate)}",
            ExchangeRateMonitorAlertKinds.Edited => $"{currency} rate re-keyed in SAP{move} from {Day(finding.RateDate)}",
            ExchangeRateMonitorAlertKinds.MissingToday => $"No {currency} rate in SAP today — {currency} documents will not post",
            ExchangeRateMonitorAlertKinds.RunningOut => $"{currency} rates in SAP run out on {Day(finding.RateDate)}",
            ExchangeRateMonitorAlertKinds.SettingsChanged => "SAP currency settings changed",
            _ => $"{currency} rate monitor cannot read SAP"
        };
    }

    private static IEnumerable<SapPriceListFactor> DerivedListsIn(string currency, IReadOnlyList<SapPriceListFactor> lists) =>
        lists
            .Where(list => list.Active
                           && list.IsDerived
                           && list.Factor != 1m
                           && string.Equals(list.Currency, currency, StringComparison.OrdinalIgnoreCase))
            .OrderBy(list => list.PriceListNo);

    private static string ChangeKey(string currency, DateOnly date, decimal from, decimal to) =>
        $"{currency}:change:{Iso(date)}:{Number(from)}->{Number(to)}";

    private static bool IsLarge(decimal from, decimal to, ExchangeRateMonitorSettings options) =>
        from > 0 && options.LargeChangePercent > 0 && Math.Abs(to - from) / from * 100m >= options.LargeChangePercent;

    private static string LargeChangeNote(decimal from, decimal to, ExchangeRateMonitorSettings options) =>
        IsLarge(from, to, options)
            ? $" That is a move of {Math.Abs(to - from) / from * 100m:0.#}% in a day — check it is not a typing slip."
            : string.Empty;

    private static string Percent(decimal from, decimal to) =>
        from == 0 ? "new" : $"{(to - from) / from * 100m:+0.##;-0.##;0}%";

    /// <summary>Which way the foreign currency moved: a higher indirect rate is more ZiG per USD, so a weaker ZiG.</summary>
    private static string Movement(decimal from, decimal to, string currency, SapCurrencyRateSettings settings)
    {
        var foreignWeaker = settings.IndirectQuotation ? to > from : to < from;
        return $", {currency} {(foreignWeaker ? "weaker" : "stronger")} against {settings.LocalCurrency}";
    }

    internal static string Describe(decimal sapRate, string currency, SapCurrencyRateSettings? settings) =>
        settings is null
            ? Number(sapRate)
            : settings.IndirectQuotation
                ? $"1 {settings.LocalCurrency} = {Number(sapRate)} {currency}"
                : $"1 {currency} = {Number(sapRate)} {settings.LocalCurrency}";

    private static string Quotation(bool? indirect) =>
        indirect is null ? "unknown" : indirect.Value ? "indirectly" : "directly";

    private static string DescribeFailure(Exception ex) => ex switch
    {
        SapRequestRejectedException rejected => rejected.Message,
        _ => ex.GetBaseException().Message
    };

    internal static string Number(decimal value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Day(DateOnly? date) =>
        date?.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture) ?? "an unknown day";

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";
}

/// <summary>One thing a pass found. <see cref="AlsoClaims"/> are keys of the same event seen another way.</summary>
internal sealed record ExchangeRateMonitorFinding(
    string Key,
    string Kind,
    string Severity,
    DateOnly? RateDate,
    string Message,
    IReadOnlyList<string>? AlsoClaims = null,
    decimal? From = null,
    decimal? To = null)
{
    public IEnumerable<string> AllKeys => AlsoClaims is null ? [Key] : [Key, .. AlsoClaims];
}

public sealed record ExchangeRateMonitorPassResult(string Currency, bool Succeeded, string? Error, int AlertsRaised);

public static class ExchangeRateMonitorDates
{
    /// <summary>Today's date in Harare, which is the date SAP documents carry.</summary>
    public static DateOnly TodayCat(DateTime utcNow) => DateOnly.FromDateTime(AuditService.ToCAT(utcNow));
}
