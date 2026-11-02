using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The rate SAP books ZiG documents at must never move, be re-keyed or run out without someone
/// being told.
/// </summary>
/// <remarks>
/// Built on production as read on 2026-11-02: local currency USD, quoted indirectly; ZiG 36 until
/// 28 Feb 2026, 30 from 1 Mar, keyed through 30 Nov and refused (-4006) from 1 Dec. In 2025 five
/// month-ends carried a one-day 50 between 36s.
/// </remarks>
public sealed class ExchangeRateMonitorTests : IDisposable
{
    private const string Recipient = "finance@example.test";

    // Mon 2 Nov 2026, 08:00 CAT.
    private static readonly DateTime Nov2Morning = new(2026, 11, 2, 6, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly NoOpNotificationService _notifications = new();
    private readonly List<SendEmailRequest> _emails = [];
    private readonly FakeSapRates _sap = new();

    public ExchangeRateMonitorTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        // Production's ZiG rates.
        _sap.RateFor = day => day < new DateOnly(2026, 3, 1) ? 36m
            : day <= new DateOnly(2026, 11, 30) ? 30m
            : null;
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // ── A quiet first pass ───────────────────────────────

    [Fact]
    public async Task The_first_pass_on_2_November_records_SAPs_rate_and_raises_nothing()
    {
        var result = Assert.Single(await Monitor().CheckAsync(Nov2Morning));

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.AlertsRaised);
        Assert.Empty(_notifications.Sent);
        Assert.Empty(_emails);

        var status = Assert.Single(await StatusAsync(Nov2Morning));
        Assert.Equal("Ok", status.Health);
        Assert.Equal(30m, status.TodayRate);
        Assert.Equal("1 USD = 30 ZiG", status.TodayRateText);
        Assert.Equal(30m, status.ForeignPerLocal);
        Assert.Equal(0.033333m, status.LocalPerForeign);
        Assert.Equal(new DateOnly(2026, 11, 30), status.KeyedThrough);
        Assert.Equal(28, status.DaysKeyedAhead);
        Assert.False(status.KeyedBeyondWindow);
    }

    [Fact]
    public async Task The_first_pass_back_fills_history_without_alerting_on_it()
    {
        await Monitor(o => o.HistoryBackfillDays = 300).CheckAsync(Nov2Morning);

        Assert.Empty(_notifications.Sent);

        var status = Assert.Single(await StatusAsync(Nov2Morning));
        var change = Assert.Single(status.Changes);
        Assert.Equal(new DateOnly(2026, 3, 1), change.RateDate);
        Assert.Equal(36m, change.PreviousRate);
        Assert.Equal(30m, change.Rate);
        Assert.Equal(-16.67m, change.PercentChange);
    }

    // ── Changes ──────────────────────────────────────────

    [Fact]
    public async Task Re_keying_the_rest_of_the_month_is_one_alert_naming_the_whole_range()
    {
        await Monitor().CheckAsync(Nov2Morning);

        _sap.RateFor = day => day >= new DateOnly(2026, 11, 5) && day <= new DateOnly(2026, 11, 30) ? 28m
            : day <= new DateOnly(2026, 11, 30) ? 30m
            : null;

        var result = Assert.Single(await Monitor().CheckAsync(Nov2Morning.AddMinutes(15)));

        Assert.Equal(1, result.AlertsRaised);
        var notification = Assert.Single(_notifications.Sent);
        Assert.Equal("ZiG rate re-keyed in SAP: 30 → 28 from Thu 5 Nov 2026", notification.Title);
        Assert.Contains("Thu 5 Nov 2026 to Mon 30 Nov 2026: SAP's ZiG rate was re-keyed from 1 USD = 30 ZiG to 1 USD = 28 ZiG", notification.Message);
        Assert.Contains("ZiG stronger against USD", notification.Message);
        Assert.Equal("Warning", notification.Type);
        Assert.Equal("SAP", notification.Category);
        Assert.Equal("/exchange-rates", notification.ActionUrl);

        var email = Assert.Single(_emails);
        Assert.Equal([Recipient], email.To);
        Assert.Equal(notification.Title, email.Subject);

        // Seen again, it is old news.
        await Monitor().CheckAsync(Nov2Morning.AddMinutes(30));
        Assert.Single(_notifications.Sent);

        // The replaced values are kept, not overwritten.
        var nov5 = await _context.ExchangeRateObservations
            .Where(o => o.RateDate == new DateOnly(2026, 11, 5))
            .OrderBy(o => o.Id)
            .ToListAsync();
        Assert.Equal([30m, 28m], nov5.Select(o => o.Rate));
        Assert.NotNull(nov5[0].SupersededAtUtc);
        Assert.Null(nov5[1].SupersededAtUtc);
        Assert.Equal(30m, nov5[1].ReplacedRate);
    }

    [Fact]
    public async Task Re_keying_today_says_the_documents_already_booked_keep_the_old_rate()
    {
        await Monitor().CheckAsync(Nov2Morning);

        _sap.RateFor = day => day >= new DateOnly(2026, 11, 2) && day <= new DateOnly(2026, 11, 30) ? 32m
            : day <= new DateOnly(2026, 11, 30) ? 30m
            : null;

        await Monitor().CheckAsync(Nov2Morning.AddHours(2));

        var notification = Assert.Single(_notifications.Sent);
        Assert.Contains("ZiG documents already booked for today keep the old rate", notification.Message);
        Assert.Contains("ZiG weaker against USD", notification.Message);
    }

    [Fact]
    public async Task A_new_day_keyed_at_a_different_rate_is_reported_when_it_is_keyed()
    {
        await Monitor().CheckAsync(Nov2Morning);

        _sap.RateFor = day => day <= new DateOnly(2026, 11, 30) ? 30m
            : day <= new DateOnly(2026, 12, 31) ? 32.5m
            : null;

        await Monitor().CheckAsync(Nov2Morning.AddHours(1));

        var notification = Assert.Single(_notifications.Sent);
        Assert.Equal("ZiG rate changes in SAP: 30 → 32.5 from Tue 1 Dec 2026", notification.Title);
        Assert.Contains("From Tue 1 Dec 2026: 1 USD = 32.5 ZiG, was 1 USD = 30 ZiG on Mon 30 Nov 2026 (+8.33%, ZiG weaker against USD)", notification.Message);
        Assert.DoesNotContain("typing slip", notification.Message);
    }

    [Fact]
    public async Task A_one_day_month_end_spike_like_2025s_fifties_is_flagged_as_a_possible_slip()
    {
        // Sun 29 Nov, with 30 Nov keyed at 50 between 36s.
        var nov29 = new DateTime(2026, 11, 29, 6, 0, 0, DateTimeKind.Utc);
        _sap.RateFor = day => day == new DateOnly(2026, 11, 30) ? 50m
            : day <= new DateOnly(2026, 12, 31) ? 36m
            : null;

        await Monitor(o => o.HistoryBackfillDays = 0).CheckAsync(nov29);

        var notification = Assert.Single(_notifications.Sent);
        Assert.Equal("ZiG rate changes in SAP: 36 → 50 from Mon 30 Nov 2026 (+1 more)", notification.Title);
        Assert.Contains("From Mon 30 Nov 2026: 1 USD = 50 ZiG", notification.Message);
        Assert.Contains("From Tue 1 Dec 2026: 1 USD = 36 ZiG, was 1 USD = 50 ZiG", notification.Message);
        Assert.Contains("That is a move of 38.9% in a day — check it is not a typing slip.", notification.Message);
    }

    // ── Running out ──────────────────────────────────────

    [Fact]
    public async Task No_rate_today_is_an_error_raised_once_a_day()
    {
        _sap.RateFor = day => day < new DateOnly(2026, 11, 2) ? 30m : null;

        await Monitor().CheckAsync(Nov2Morning);
        await Monitor().CheckAsync(Nov2Morning.AddHours(1));

        var notification = Assert.Single(_notifications.Sent);
        Assert.Equal("No ZiG rate in SAP today — ZiG documents will not post", notification.Title);
        Assert.Equal("Error", notification.Type);
        Assert.Contains("Update the exchange rate", notification.Message);
        Assert.Contains("Yesterday's rate was 30.", notification.Message);

        var status = Assert.Single(await StatusAsync(Nov2Morning.AddHours(1)));
        Assert.Equal("Error", status.Health);
        Assert.Null(status.TodayRate);
        Assert.Null(status.KeyedThrough);
    }

    [Fact]
    public async Task Rates_running_out_within_a_week_warn_once_a_day_until_they_are_keyed()
    {
        // Wed 25 Nov: production's rates end on Mon 30 Nov.
        var nov25 = new DateTime(2026, 11, 25, 6, 0, 0, DateTimeKind.Utc);

        await Monitor().CheckAsync(nov25);
        await Monitor().CheckAsync(nov25.AddHours(3));

        var first = Assert.Single(_notifications.Sent);
        Assert.Equal("ZiG rates in SAP run out on Tue 1 Dec 2026", first.Title);
        Assert.Contains("keyed only through Mon 30 Nov 2026 (5 more days)", first.Message);
        Assert.Contains("From Tue 1 Dec 2026 SAP will refuse every ZiG document dated that day", first.Message);
        Assert.Equal("Warning", first.Type);

        await Monitor().CheckAsync(nov25.AddDays(1));
        Assert.Equal(2, _notifications.Sent.Count);
        Assert.Contains("(4 more days)", _notifications.Sent[1].Message);

        var status = Assert.Single(await StatusAsync(nov25.AddDays(1)));
        Assert.Equal("Warning", status.Health);
    }

    [Fact]
    public async Task Rates_keyed_beyond_the_window_do_not_warn()
    {
        _sap.RateFor = _ => 30m;

        await Monitor().CheckAsync(Nov2Morning);

        Assert.Empty(_notifications.Sent);
        var status = Assert.Single(await StatusAsync(Nov2Morning));
        Assert.True(status.KeyedBeyondWindow);
        Assert.Equal("Ok", status.Health);
    }

    // ── When SAP cannot be read ──────────────────────────

    [Fact]
    public async Task A_failed_read_is_recorded_and_only_alerted_once_the_monitor_has_been_blind_two_hours()
    {
        await Monitor().CheckAsync(Nov2Morning);
        _sap.Failure = new HttpRequestException("Connection refused (10.10.10.6:50000)");

        var failed = Assert.Single(await Monitor().CheckAsync(Nov2Morning.AddMinutes(15)));
        Assert.False(failed.Succeeded);
        Assert.Empty(_notifications.Sent);

        await Monitor().CheckAsync(Nov2Morning.AddHours(1));
        Assert.Empty(_notifications.Sent);

        await Monitor().CheckAsync(Nov2Morning.AddHours(2).AddMinutes(15));
        var notification = Assert.Single(_notifications.Sent);
        Assert.Equal("ZiG rate monitor cannot read SAP", notification.Title);
        Assert.Contains("Connection refused", notification.Message);

        var state = await _context.ExchangeRateMonitorStates.AsNoTracking().SingleAsync();
        Assert.Equal(Nov2Morning, state.LastSuccessAtUtc);
        Assert.Contains("Connection refused", state.LastError);
    }

    [Fact]
    public async Task An_alert_whose_notification_could_not_be_stored_is_raised_on_the_next_pass()
    {
        _sap.RateFor = day => day < new DateOnly(2026, 11, 2) ? 30m : null;
        var flaky = new FlakyNotifications(failures: 1);

        await Monitor(notifications: flaky).CheckAsync(Nov2Morning);
        Assert.Empty(flaky.Sent);
        Assert.Empty(await _context.ExchangeRateMonitorAlerts.ToListAsync());

        await Monitor(notifications: flaky).CheckAsync(Nov2Morning.AddMinutes(15));
        Assert.Single(flaky.Sent);
        Assert.Single(await _context.ExchangeRateMonitorAlerts.ToListAsync());
    }

    // ── The rate service reads SAP's direction ───────────

    [Fact]
    public async Task The_rate_service_reads_30_as_1_USD_buying_30_ZiG()
    {
        var service = new ExchangeRateService(
            _context, _sap.AsClient(), NullLogger<ExchangeRateService>.Instance, new MemoryCache(new MemoryCacheOptions()));

        var usdToZig = await service.GetCurrentRateAsync("USD", "ZIG");
        var zigToUsd = await service.GetCurrentRateAsync("ZIG", "USD");

        Assert.Equal(30m, usdToZig!.Rate);
        Assert.Equal(1m / 30m, zigToUsd!.Rate);
        // Order 86255: USD 696.88 at 30 is ZiG 20,906.40 (SAP shows 20,906.42 after line rounding).
        Assert.Equal(20906.40m, await service.ConvertAsync(696.88m, "USD", "ZIG"));

        var all = await service.GetAllActiveRatesAsync();
        Assert.Contains(all, r => r.FromCurrency == "USD" && r.ToCurrency == "ZiG" && r.Rate == 30m);
        Assert.Contains(all, r => r.FromCurrency == "ZiG" && r.ToCurrency == "USD" && r.Rate == 1m / 30m);
    }

    // ── Kit ──────────────────────────────────────────────

    private ExchangeRateMonitor Monitor(
        Action<ExchangeRateMonitorSettings>? configure = null,
        INotificationService? notifications = null)
    {
        var settings = new ExchangeRateMonitorSettings { HistoryBackfillDays = 10 };
        configure?.Invoke(settings);

        return new ExchangeRateMonitor(
            _context,
            _sap.AsClient(),
            notifications ?? _notifications,
            StubProxy.For<IEmailService>((method, args) => method.Name switch
            {
                nameof(IEmailService.QueueEmailAsync) => Record((SendEmailRequest)args![0]!),
                _ => throw new InvalidOperationException($"IEmailService.{method.Name} was not expected.")
            }),
            Options.Create(settings),
            Options.Create(new SystemHealthAlertSettings { AlertRecipients = [Recipient] }),
            NullLogger<ExchangeRateMonitor>.Instance);
    }

    private Task Record(SendEmailRequest request)
    {
        _emails.Add(request);
        return Task.CompletedTask;
    }

    private Task<List<ExchangeRateMonitorStatusDto>> StatusAsync(DateTime utcNow) =>
        new ExchangeRateMonitorStatusReader(_context, Options.Create(new ExchangeRateMonitorSettings()))
            .ReadAsync(utcNow);

    private sealed class FakeSapRates
    {
        public Func<DateOnly, decimal?> RateFor { get; set; } = _ => null;

        public Exception? Failure { get; set; }

        public ISAPServiceLayerClient AsClient() =>
            StubProxy.For<ISAPServiceLayerClient>((method, args) =>
            {
                if (Failure is not null)
                {
                    return method.ReturnType.IsGenericType
                        ? typeof(Task).GetMethod(nameof(Task.FromException), 1, [typeof(Exception)])!
                            .MakeGenericMethod(method.ReturnType.GetGenericArguments()[0])
                            .Invoke(null, [Failure])
                        : Task.FromException(Failure);
                }

                return method.Name switch
                {
                    nameof(ISAPServiceLayerClient.GetCurrencyRateSettingsAsync) =>
                        Task.FromResult(new SapCurrencyRateSettings("USD", "USD", IndirectQuotation: true)),
                    nameof(ISAPServiceLayerClient.GetPriceListFactorsAsync) =>
                        Task.FromResult<IReadOnlyList<SapPriceListFactor>>(
                        [
                            new(28, "Factory ZiG Pricelist", 28, 1m, "ZiG", true),
                            new(29, "Factory ZiG Pricelist 2024", 28, 36m, "ZiG", true)
                        ]),
                    nameof(ISAPServiceLayerClient.GetCurrencyRateAsync) => Task.FromResult(
                        RateFor(DateOnly.FromDateTime((DateTime)args![1]!)) is { } rate
                            ? SapCurrencyRateLookup.Found(rate)
                            : SapCurrencyRateLookup.NotKeyed("Update the exchange rate")),
                    nameof(ISAPServiceLayerClient.GetExchangeRateAsync) => Task.FromResult<SAPExchangeRate?>(
                        new SAPExchangeRate { Currency = "ZiG", RateDate = new DateTime(2026, 11, 2), Rate = 30m }),
                    nameof(ISAPServiceLayerClient.GetExchangeRatesAsync) => Task.FromResult(new List<SAPExchangeRate>
                    {
                        new() { Currency = "ZiG", RateDate = new DateTime(2026, 11, 2), Rate = 30m }
                    }),
                    _ => throw new InvalidOperationException($"ISAPServiceLayerClient.{method.Name} was not expected.")
                };
            });
    }

    private sealed class FlakyNotifications(int failures) : INotificationService
    {
        private int _failuresLeft = failures;
        private readonly NoOpNotificationService _inner = new();

        public List<CreateNotificationRequest> Sent => _inner.Sent;

        public Task<NotificationDto> CreateNotificationAsync(CreateNotificationRequest request, CancellationToken cancellationToken = default)
        {
            if (_failuresLeft-- > 0)
            {
                throw new DbUpdateException("The notification table is locked.");
            }

            return _inner.CreateNotificationAsync(request, cancellationToken);
        }

        public Task<NotificationListResponseDto> GetNotificationsAsync(string? username, IReadOnlyCollection<string>? roles, int page = 1, int pageSize = 20, bool unreadOnly = false, string? category = null, CancellationToken cancellationToken = default) =>
            _inner.GetNotificationsAsync(username, roles, page, pageSize, unreadOnly, category, cancellationToken);

        public Task<int> GetUnreadCountAsync(string? username, IReadOnlyCollection<string>? roles, CancellationToken cancellationToken = default) =>
            _inner.GetUnreadCountAsync(username, roles, cancellationToken);

        public Task MarkAsReadAsync(string? username, IReadOnlyCollection<string>? roles, List<int>? notificationIds, CancellationToken cancellationToken = default) =>
            _inner.MarkAsReadAsync(username, roles, notificationIds, cancellationToken);

        public Task DeleteNotificationAsync(int id, string? username, IReadOnlyCollection<string>? roles, CancellationToken cancellationToken = default) =>
            _inner.DeleteNotificationAsync(id, username, roles, cancellationToken);

        public Task CleanupExpiredNotificationsAsync(CancellationToken cancellationToken = default) =>
            _inner.CleanupExpiredNotificationsAsync(cancellationToken);

        public Task CreateLowStockAlertAsync(string itemCode, string itemName, decimal currentStock, decimal reorderLevel, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task CreateSystemAlertAsync(string title, string message, string type = "Info", CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task CreateSalesOrderNotificationAsync(int orderId, string orderNumber, string customerCode, string customerName, decimal docTotal, string status, string source, string? createdByUsername, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
