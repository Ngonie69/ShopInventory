using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Features.CreditControl.Queries.GetCreditHeadroom;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the credit room reported beside an order somebody is about to approve.
/// </summary>
/// <remarks>
/// On 2026-08-20 the same order at SPA077 was pushed four times. Each attempt spent between 8 and 26
/// seconds re-pricing against live SAP before the credit gate refused it, and each refusal named the
/// shortfall — after the fact. By the fourth attempt the order had been cut from 1,050.48 to 794.82
/// and was still 8.75 over. The account had 786.07 of room throughout, and the sweep that already
/// runs for credit control knew it.
/// <para>
/// The figures have to agree with the gate that actually refuses, which is why the group cases here
/// matter most: FOO030 was refused on its parent's limit, not its own.
/// </para>
/// </remarks>
public sealed class CreditHeadroomTests
{
    [Fact]
    public async Task Reports_the_room_left_on_a_standalone_account()
    {
        var handler = CreateHandler(Customer("SPA077", "Spar Avondale", creditLimit: 5_000m, balance: 4_213.93m));

        var result = await handler.Handle(Query("SPA077"), CancellationToken.None);

        var account = Assert.Single(result.Value.Accounts);
        Assert.True(account.HasLimit);
        Assert.False(account.IsGroup);
        Assert.Equal("SPA077", account.CreditAccountCardCode);
        Assert.Equal(5_000m, account.CreditLimit);
        Assert.Equal(4_213.93m, account.Exposure);

        // The number the approver needed: the order was 1,050.48 and this is what it had to fit in.
        Assert.Equal(786.07m, account.Headroom);
    }

    /// <summary>
    /// A child account is refused on its parent's limit, so that is the limit to report. Showing its
    /// own would promise room the order will never get.
    /// </summary>
    [Fact]
    public async Task A_child_account_reports_the_group_limit_that_governs_it()
    {
        var handler = CreateHandler(
            Customer("FOO025", "Food World USD", creditLimit: 3_500m, balance: 900m),
            Customer("FOO030", "Foodworld Emporium USD", creditLimit: 1_750m, balance: 1_017.22m, fatherCard: "FOO025"));

        var result = await handler.Handle(Query("FOO030"), CancellationToken.None);

        var account = Assert.Single(result.Value.Accounts);
        Assert.True(account.IsGroup);
        Assert.Equal("FOO025", account.CreditAccountCardCode);
        Assert.Equal("Food World USD", account.CreditAccountName);
        Assert.Equal(2, account.AccountCount);
        Assert.Equal(3_500m, account.CreditLimit);
        Assert.Equal(1_917.22m, account.Exposure);
        Assert.Equal(1_582.78m, account.Headroom);
    }

    /// <summary>
    /// An account already over its limit reports negative room, not zero — the approver needs to
    /// know a payment is required before any order will go through, not just this one.
    /// </summary>
    [Fact]
    public async Task An_account_already_over_its_limit_reports_negative_room()
    {
        var handler = CreateHandler(Customer("OVR001", "Over Limit Traders", creditLimit: 1_000m, balance: 1_250m));

        var result = await handler.Handle(Query("OVR001"), CancellationToken.None);

        Assert.Equal(-250m, Assert.Single(result.Value.Accounts).Headroom);
    }

    /// <summary>
    /// No limit set is the opposite of no room left, and a screen that showed 0.00 for both would
    /// have a rep chasing payment on an account that never needed it.
    /// </summary>
    [Fact]
    public async Task An_account_with_no_limit_is_reported_as_unlimited_not_as_zero()
    {
        var handler = CreateHandler(Customer("NOL001", "No Limit Stores", creditLimit: 0m, balance: 9_999m));

        var result = await handler.Handle(Query("NOL001"), CancellationToken.None);

        var account = Assert.Single(result.Value.Accounts);
        Assert.False(account.HasLimit);
        Assert.Equal(0m, account.CreditLimit);
    }

    [Fact]
    public async Task A_customer_SAP_does_not_know_is_reported_without_a_limit()
    {
        var handler = CreateHandler(Customer("SPA077", "Spar Avondale", creditLimit: 5_000m, balance: 100m));

        var result = await handler.Handle(Query("GHOST01"), CancellationToken.None);

        var account = Assert.Single(result.Value.Accounts);
        Assert.Equal("GHOST01", account.CardCode);
        Assert.False(account.HasLimit);
    }

    /// <summary>
    /// A page of orders is a handful of distinct customers. One sweep answers all of them — the
    /// whole reason this hangs off the credit review rather than reading each account live.
    /// </summary>
    [Fact]
    public async Task A_page_of_customers_costs_one_sweep()
    {
        var (handler, sweeps) = CreateHandlerRecordingSweeps(
            Customer("SPA077", "Spar Avondale", creditLimit: 5_000m, balance: 4_213.93m),
            Customer("FOO030", "Foodworld Emporium USD", creditLimit: 1_750m, balance: 0m),
            Customer("ALA001", "Alanby", creditLimit: 2_000m, balance: 500m));

        var result = await handler.Handle(Query("SPA077", "FOO030", "ALA001"), CancellationToken.None);

        Assert.Equal(3, result.Value.Accounts.Count);
        Assert.Single(sweeps);
    }

    [Fact]
    public async Task Asking_about_nothing_reads_nothing()
    {
        var (handler, sweeps) = CreateHandlerRecordingSweeps(
            Customer("SPA077", "Spar Avondale", creditLimit: 5_000m, balance: 100m));

        var result = await handler.Handle(Query(), CancellationToken.None);

        Assert.Empty(result.Value.Accounts);
        Assert.Empty(sweeps);
    }

    [Fact]
    public async Task A_duplicated_card_code_is_answered_once()
    {
        var handler = CreateHandler(Customer("SPA077", "Spar Avondale", creditLimit: 5_000m, balance: 100m));

        var result = await handler.Handle(Query("SPA077", "spa077", "SPA077"), CancellationToken.None);

        Assert.Single(result.Value.Accounts);
    }

    [Fact]
    public async Task A_failed_sweep_is_reported_rather_than_answered_with_empty_room()
    {
        var reviewService = StubProxy.For<ICreditLimitReviewService>((method, _) =>
            method.Name == nameof(ICreditLimitReviewService.ReviewAsync)
                ? Task.FromException<CreditLimitReview>(new HttpRequestException("SAP is down"))
                : throw new InvalidOperationException($"Unexpected call to {method.Name}"));

        var handler = new GetCreditHeadroomHandler(
            new CreditLimitReviewCache(
                reviewService,
                new MemoryCache(new MemoryCacheOptions()),
                Options.Create(new CreditLimitSettings())),
            Options.Create(new SAPSettings { Enabled = true }),
            NullLogger<GetCreditHeadroomHandler>.Instance);

        var result = await handler.Handle(Query("SPA077"), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("CreditControl.ReviewFailed", result.FirstError.Code);
    }

    private static GetCreditHeadroomQuery Query(params string[] cardCodes) => new(cardCodes);

    private static BusinessPartnerCreditProfileDto Customer(
        string cardCode,
        string cardName,
        decimal creditLimit,
        decimal balance,
        string? fatherCard = null) => new()
        {
            CardCode = cardCode,
            CardName = cardName,
            Currency = "USD",
            CreditLimit = creditLimit,
            Balance = balance,
            OpenOrdersBalance = 0m,
            FatherCard = fatherCard
        };

    private static GetCreditHeadroomHandler CreateHandler(params BusinessPartnerCreditProfileDto[] customers)
        => CreateHandlerRecordingSweeps(customers).Handler;

    /// <summary>
    /// Returns the handler and the list its sweeps are recorded in, so a test can assert how many
    /// times SAP was actually read after the handler has run.
    /// </summary>
    private static (GetCreditHeadroomHandler Handler, List<DateTime> Sweeps) CreateHandlerRecordingSweeps(
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

        var handler = new GetCreditHeadroomHandler(
            new CreditLimitReviewCache(
                new CreditLimitReviewService(
                    sap,
                    NullLogger<CreditLimitReviewService>.Instance,
                    Options.Create(new CreditLimitSettings())),
                new MemoryCache(new MemoryCacheOptions()),
                Options.Create(new CreditLimitSettings())),
            Options.Create(new SAPSettings { Enabled = true }),
            NullLogger<GetCreditHeadroomHandler>.Instance);

        return (handler, sweeps);
    }
}
