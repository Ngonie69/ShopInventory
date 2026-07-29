using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Features.CreditControl.Queries.GetCreditLimitReview;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the evening sweep that reports customers already over their credit limit.
/// </summary>
/// <remarks>
/// The sweep reads every customer once and does the grouping in memory, so what these mostly guard
/// is that the in-memory grouping agrees with the per-order check in <see cref="CreditLimitService"/>
/// — the same account must not be judged one way at 19:15 and the other way when a rep raises an
/// order the next morning. The double-reporting rule matters as much: a group and its members are
/// one fact, and listing them separately makes the alert unreadable.
/// </remarks>
public class CreditLimitReviewTests
{
    [Fact]
    public async Task Reports_nothing_when_every_account_is_within_its_limit()
    {
        var review = await Review(
            Profile("C001", creditLimit: 30_000m, balance: 12_000m),
            Profile("C002", creditLimit: 10_000m, balance: 10_000m));

        Assert.Empty(review.Breaches);
        Assert.Equal(2, review.CustomersRead);
        Assert.Equal(2, review.LimitsMeasured);
    }

    [Fact]
    public async Task Reports_an_account_over_its_own_limit()
    {
        var review = await Review(
            Profile("C001", creditLimit: 30_000m, balance: 35_759.10m),
            Profile("C002", creditLimit: 10_000m, balance: 1_000m));

        var breach = Assert.Single(review.Breaches);
        Assert.Equal("C001", breach.CardCode);
        Assert.False(breach.IsGroup);
        Assert.Equal(5_759.10m, breach.AmountOver);
        Assert.Equal(5_759.10m, review.TotalOver);
    }

    [Fact]
    public async Task Counts_open_orders_toward_exposure()
    {
        var review = await Review(
            Profile("C001", creditLimit: 30_000m, balance: 25_000m, openOrders: 6_000m));

        var breach = Assert.Single(review.Breaches);
        Assert.Equal(31_000m, breach.Exposure);
        Assert.Equal(6_000m, breach.OpenOrders);
    }

    [Fact]
    public async Task Skips_accounts_with_no_limit_set()
    {
        var review = await Review(Profile("C001", creditLimit: 0m, balance: 90_000m));

        Assert.Empty(review.Breaches);
        Assert.Equal(0, review.LimitsMeasured);
    }

    [Fact]
    public async Task Reports_a_group_against_the_parents_limit()
    {
        var review = await Review(
            Profile("PIN001", creditLimit: 30_000m, balance: 18_000m),
            Profile("SAI034", creditLimit: 0m, balance: 9_000m, fatherCard: "PIN001"),
            Profile("SAI099", creditLimit: 0m, balance: 6_000m, fatherCard: "PIN001"));

        var breach = Assert.Single(review.Breaches);
        Assert.Equal("PIN001", breach.CardCode);
        Assert.True(breach.IsGroup);
        Assert.Equal(3, breach.AccountCount);
        Assert.Equal(33_000m, breach.Exposure);
        Assert.Equal(3_000m, breach.AmountOver);
    }

    [Fact]
    public async Task Reports_a_breached_group_once_not_once_per_member()
    {
        // Every member is over its own limit too. That is the same debt seen three times.
        var review = await Review(
            Profile("PIN001", creditLimit: 10_000m, balance: 20_000m),
            Profile("SAI034", creditLimit: 1_000m, balance: 9_000m, fatherCard: "PIN001"),
            Profile("SAI099", creditLimit: 1_000m, balance: 6_000m, fatherCard: "PIN001"));

        var breach = Assert.Single(review.Breaches);
        Assert.True(breach.IsGroup);
        Assert.Equal(35_000m, breach.Exposure);
    }

    [Fact]
    public async Task Still_reports_a_member_when_the_group_itself_is_within_its_limit()
    {
        // The group has room, so it is not reported — but this member has blown its own limit and
        // its orders will be refused, which is exactly what the evening report exists to surface.
        var review = await Review(
            Profile("PIN001", creditLimit: 500_000m, balance: 10_000m),
            Profile("SAI034", creditLimit: 5_000m, balance: 8_000m, fatherCard: "PIN001"));

        var breach = Assert.Single(review.Breaches);
        Assert.Equal("SAI034", breach.CardCode);
        Assert.False(breach.IsGroup);
    }

    [Fact]
    public async Task Measures_children_on_their_own_when_the_parent_holds_no_limit()
    {
        var review = await Review(
            Profile("PIN001", creditLimit: 0m, balance: 400_000m),
            Profile("SAI034", creditLimit: 10_000m, balance: 11_000m, fatherCard: "PIN001"));

        var breach = Assert.Single(review.Breaches);
        Assert.Equal("SAI034", breach.CardCode);
        // The parent's own balance must not leak into a limit it does not govern.
        Assert.Equal(11_000m, breach.Exposure);
    }

    [Fact]
    public async Task Ignores_a_parent_that_is_not_a_customer()
    {
        // A child pointing at a supplier or a deleted card must not throw away the child's own check.
        var review = await Review(
            Profile("SAI034", creditLimit: 5_000m, balance: 6_000m, fatherCard: "GONE001"));

        var breach = Assert.Single(review.Breaches);
        Assert.Equal("SAI034", breach.CardCode);
        Assert.False(breach.IsGroup);
    }

    [Fact]
    public async Task Orders_the_worst_breach_first()
    {
        var review = await Review(
            Profile("C001", creditLimit: 10_000m, balance: 11_000m),
            Profile("C002", creditLimit: 10_000m, balance: 40_000m),
            Profile("C003", creditLimit: 10_000m, balance: 15_000m));

        Assert.Equal(["C002", "C003", "C001"], review.Breaches.Select(breach => breach.CardCode));
    }

    [Fact]
    public async Task Notification_message_names_the_worst_and_counts_the_rest()
    {
        var review = await Review(
            Profile("C001", creditLimit: 10_000m, balance: 40_000m),
            Profile("C002", creditLimit: 10_000m, balance: 20_000m),
            Profile("C003", creditLimit: 10_000m, balance: 15_000m));

        var message = CreditLimitReviewService.BuildMessage(review, maxListed: 1);

        Assert.Contains("C001", message);
        Assert.Contains("30,000.00 over", message);
        Assert.Contains("And 2 more", message);
        Assert.DoesNotContain("C003", message);
        Assert.Equal("3 accounts are over the credit limit", CreditLimitReviewService.BuildSummary(review));
    }

    [Fact]
    public async Task Notification_summary_reads_correctly_for_a_single_account()
    {
        var review = await Review(Profile("C001", creditLimit: 10_000m, balance: 40_000m));

        Assert.Equal("1 account is over the credit limit", CreditLimitReviewService.BuildSummary(review));
    }

    [Fact]
    public async Task Endpoint_serves_a_repeat_read_from_cache_without_sweeping_sap_again()
    {
        // Credit control refreshing a page must not read every customer out of SAP each time.
        var (handler, sweeps) = CreateHandler();

        var first = await handler.Handle(new GetCreditLimitReviewQuery(), CancellationToken.None);
        var second = await handler.Handle(new GetCreditLimitReviewQuery(), CancellationToken.None);

        Assert.Single(sweeps);
        Assert.False(first.Value.FromCache);
        Assert.True(second.Value.FromCache);
        // Both answers describe the same sweep, so the timestamp must not be refreshed on read.
        Assert.Equal(first.Value.GeneratedAt, second.Value.GeneratedAt);
    }

    [Fact]
    public async Task Endpoint_rereads_sap_when_a_refresh_is_asked_for()
    {
        var (handler, sweeps) = CreateHandler();

        await handler.Handle(new GetCreditLimitReviewQuery(), CancellationToken.None);
        var refreshed = await handler.Handle(new GetCreditLimitReviewQuery(Refresh: true), CancellationToken.None);

        Assert.Equal(2, sweeps.Count);
        Assert.False(refreshed.Value.FromCache);
    }

    [Fact]
    public async Task Endpoint_reports_the_breaches_with_their_figures()
    {
        var (handler, _) = CreateHandler(
            Profile("PIN001", creditLimit: 30_000m, balance: 18_000m),
            Profile("SAI034", creditLimit: 0m, balance: 15_000m, fatherCard: "PIN001"));

        var result = await handler.Handle(new GetCreditLimitReviewQuery(), CancellationToken.None);

        var breach = Assert.Single(result.Value.Breaches);
        Assert.Equal("PIN001", breach.CardCode);
        Assert.True(breach.IsGroup);
        Assert.Equal(2, breach.AccountCount);
        Assert.Equal(3_000m, breach.AmountOver);
        Assert.Equal(1, result.Value.BreachCount);
        Assert.Equal(3_000m, result.Value.TotalOver);
    }

    [Fact]
    public async Task Endpoint_reports_a_failed_sweep_rather_than_an_empty_list()
    {
        // An empty list means "nobody is over", and serving that on a SAP failure would read as an
        // all-clear.
        var reviewService = StubProxy.For<ICreditLimitReviewService>((method, _) =>
            method.Name == nameof(ICreditLimitReviewService.ReviewAsync)
                ? Task.FromException<CreditLimitReview>(new HttpRequestException("SAP is down"))
                : throw new InvalidOperationException($"Unexpected call to {method.Name}"));

        var handler = new GetCreditLimitReviewHandler(
            reviewService,
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new SAPSettings { Enabled = true }),
            Options.Create(new CreditLimitSettings()),
            NullLogger<GetCreditLimitReviewHandler>.Instance);

        var result = await handler.Handle(new GetCreditLimitReviewQuery(), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("CreditControl.ReviewFailed", result.FirstError.Code);
    }

    /// <summary>
    /// Returns the handler and the list its sweeps are recorded in, so a test can assert how many
    /// times SAP was actually read.
    /// </summary>
    private static (GetCreditLimitReviewHandler Handler, List<DateTime> Sweeps) CreateHandler(
        params BusinessPartnerCreditProfileDto[] customers)
    {
        var sweeps = new List<DateTime>();

        var sap = StubProxy.For<ISAPServiceLayerClient>((method, _) =>
        {
            if (method.Name != nameof(ISAPServiceLayerClient.GetCustomerCreditProfilesAsync))
                throw new InvalidOperationException($"Unexpected call to {method.Name}");

            sweeps.Add(DateTime.UtcNow);
            return Task.FromResult(customers.ToList());
        });

        var reviewService = new CreditLimitReviewService(
            sap,
            NullLogger<CreditLimitReviewService>.Instance,
            Options.Create(new CreditLimitSettings()));

        var handler = new GetCreditLimitReviewHandler(
            reviewService,
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new SAPSettings { Enabled = true }),
            Options.Create(new CreditLimitSettings()),
            NullLogger<GetCreditLimitReviewHandler>.Instance);

        return (handler, sweeps);
    }

    private static async Task<CreditLimitReview> Review(params BusinessPartnerCreditProfileDto[] customers)
    {
        var sap = StubProxy.For<ISAPServiceLayerClient>((method, _) =>
            method.Name == nameof(ISAPServiceLayerClient.GetCustomerCreditProfilesAsync)
                ? Task.FromResult(customers.ToList())
                : throw new InvalidOperationException($"Unexpected call to {method.Name}"));

        var service = new CreditLimitReviewService(
            sap,
            NullLogger<CreditLimitReviewService>.Instance,
            Options.Create(new CreditLimitSettings()));

        return await service.ReviewAsync();
    }

    private static BusinessPartnerCreditProfileDto Profile(
        string cardCode,
        decimal creditLimit,
        decimal balance,
        decimal openOrders = 0m,
        string? fatherCard = null) => new()
        {
            CardCode = cardCode,
            CardName = $"Trading {cardCode}",
            Currency = "USD",
            CreditLimit = creditLimit,
            Balance = balance,
            OpenOrdersBalance = openOrders,
            FatherCard = fatherCard
        };
}
