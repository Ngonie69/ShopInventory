using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

/// <summary>
/// What the Exchange Rates page shows about the monitor, read from what the monitor recorded. It
/// never asks SAP: a page load must not cost forty Service Layer calls.
/// </summary>
public sealed class ExchangeRateMonitorStatusReader(
    ApplicationDbContext context,
    IOptions<ExchangeRateMonitorSettings> settings)
{
    private const int HistoryDays = 400;
    private const int AlertCount = 20;

    public async Task<List<ExchangeRateMonitorStatusDto>> ReadAsync(DateTime utcNow, CancellationToken cancellationToken = default)
    {
        var options = settings.Value;
        var today = ExchangeRateMonitorDates.TodayCat(utcNow);
        var statuses = new List<ExchangeRateMonitorStatusDto>();

        foreach (var currency in options.MonitoredCurrencies())
        {
            statuses.Add(await ReadCurrencyAsync(currency, today, utcNow, options, cancellationToken));
        }

        return statuses;
    }

    private async Task<ExchangeRateMonitorStatusDto> ReadCurrencyAsync(
        string currency,
        DateOnly today,
        DateTime utcNow,
        ExchangeRateMonitorSettings options,
        CancellationToken cancellationToken)
    {
        var state = await context.ExchangeRateMonitorStates
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Currency == currency, cancellationToken);

        var since = today.AddDays(-HistoryDays);
        var observations = await context.ExchangeRateObservations
            .AsNoTracking()
            .Where(o => o.Currency == currency && o.RateDate >= since)
            .OrderByDescending(o => o.RateDate)
            .ThenByDescending(o => o.FirstSeenAtUtc)
            .ToListAsync(cancellationToken);

        var alerts = await context.ExchangeRateMonitorAlerts
            .AsNoTracking()
            .Where(a => a.Currency == currency)
            .OrderByDescending(a => a.RaisedAtUtc)
            .ThenByDescending(a => a.Id)
            .Take(AlertCount)
            .ToListAsync(cancellationToken);

        var current = observations.Where(o => o.SupersededAtUtc == null).ToList();
        var todayRow = current.FirstOrDefault(o => o.RateDate == today);

        // Today's row can predate today's first pass (rates are read ahead); Health says when the
        // monitor has not confirmed it with SAP today.
        var rateSettings = state?.LocalCurrency is { } local && state.IndirectQuotation is { } indirect
            ? new SapCurrencyRateSettings(local, null, indirect)
            : null;

        var status = new ExchangeRateMonitorStatusDto
        {
            Currency = currency,
            LocalCurrency = state?.LocalCurrency,
            IndirectQuotation = state?.IndirectQuotation,
            Today = today,
            TodayRate = todayRow?.Rate,
            TodayRateText = todayRow is null ? null : ExchangeRateMonitor.Describe(todayRow.Rate, currency, rateSettings),
            LocalPerForeign = todayRow is null || rateSettings is null ? null : Round(rateSettings.LocalPerForeign(todayRow.Rate)),
            ForeignPerLocal = todayRow is null || rateSettings is null ? null : Round(rateSettings.ForeignPerLocal(todayRow.Rate)),
            KeyedThrough = state?.KeyedThrough,
            KeyedBeyondWindow = state?.KeyedBeyondWindow ?? false,
            DaysKeyedAhead = state?.KeyedThrough is { } through ? through.DayNumber - today.DayNumber : null,
            LastCheckedAtUtc = state?.LastCheckedAtUtc,
            LastSuccessAtUtc = state?.LastSuccessAtUtc,
            LastError = state?.LastError,
            CheckIntervalMinutes = Math.Max(1, options.IntervalMinutes),
            Observations = observations.Select(o => new ExchangeRateObservationDto
            {
                RateDate = o.RateDate,
                Rate = o.Rate,
                ReplacedRate = o.ReplacedRate,
                FirstSeenAtUtc = o.FirstSeenAtUtc,
                LastSeenAtUtc = o.LastSeenAtUtc,
                SupersededAtUtc = o.SupersededAtUtc
            }).ToList(),
            Changes = Changes(current),
            RecentAlerts = alerts.Select(a => new ExchangeRateMonitorAlertDto
            {
                Kind = a.Kind,
                Severity = a.Severity,
                RateDate = a.RateDate,
                Message = a.Message,
                RaisedAtUtc = a.RaisedAtUtc
            }).ToList(),
            PriceListFactors = ReadFactors(state?.PriceListFactorsJson)
        };

        (status.Health, status.HealthReason) = Health(status, state, today, utcNow, options);
        return status;
    }

    /// <summary>Each day whose current rate differs from the day before's current rate, newest first.</summary>
    private static List<ExchangeRateChangeDto> Changes(List<ExchangeRateObservationEntity> current)
    {
        var byDate = current
            .GroupBy(o => o.RateDate)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(o => o.FirstSeenAtUtc).First());

        var changes = new List<ExchangeRateChangeDto>();
        foreach (var (date, row) in byDate)
        {
            if (!byDate.TryGetValue(date.AddDays(-1), out var before) || before.Rate == row.Rate)
            {
                continue;
            }

            changes.Add(new ExchangeRateChangeDto
            {
                RateDate = date,
                PreviousRate = before.Rate,
                Rate = row.Rate,
                PercentChange = before.Rate == 0 ? 0 : Math.Round((row.Rate - before.Rate) / before.Rate * 100m, 2),
                FirstSeenAtUtc = row.FirstSeenAtUtc
            });
        }

        return changes.OrderByDescending(c => c.RateDate).ToList();
    }

    private static (string Health, string Reason) Health(
        ExchangeRateMonitorStatusDto status,
        ExchangeRateMonitorStateEntity? state,
        DateOnly today,
        DateTime utcNow,
        ExchangeRateMonitorSettings options)
    {
        if (state?.LastSuccessAtUtc is null)
        {
            return ("Warning", state?.LastError is { } error
                ? $"The monitor has not been able to read SAP yet: {error}"
                : "The monitor has not read SAP yet.");
        }

        if (ExchangeRateMonitorDates.TodayCat(state.LastSuccessAtUtc.Value) != today)
        {
            return ("Warning", "The monitor has not read SAP today, so today's rate is not known.");
        }

        if (status.TodayRate is null)
        {
            return ("Error", $"SAP has no {status.Currency} rate for today. {status.Currency} documents dated today will not post.");
        }

        if (utcNow - state.LastSuccessAtUtc.Value > ExchangeRateMonitor.BlindAfter)
        {
            return ("Warning", $"The monitor's last good read was over {ExchangeRateMonitor.BlindAfter.TotalHours:0} hours ago.");
        }

        if (!status.KeyedBeyondWindow && status.DaysKeyedAhead is { } ahead && ahead < Math.Max(0, options.RunningOutWarningDays))
        {
            return (ahead <= 1 ? "Error" : "Warning",
                $"Rates are keyed only through {status.KeyedThrough:d MMM}. SAP refuses {status.Currency} documents from the day after.");
        }

        return ("Ok", status.KeyedBeyondWindow
            ? $"Today's rate is keyed, and rates are keyed at least {status.DaysKeyedAhead} days ahead."
            : $"Today's rate is keyed, and rates are keyed through {status.KeyedThrough:d MMM yyyy}.");
    }

    private static List<ExchangeRatePriceListFactorDto> ReadFactors(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<ExchangeRatePriceListFactorDto>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static decimal Round(decimal value) => Math.Round(value, 6);
}
