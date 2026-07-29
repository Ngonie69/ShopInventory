using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the credit limit gate on sales orders.
/// </summary>
/// <remarks>
/// The rule these encode is SAP's own approval query — <c>(DocTotal + OCRD.Balance) &gt;
/// OCRD.CreditLine</c> — with open orders added to exposure and a consolidating parent's limit
/// governing the group. What matters most here is which way each edge fails: an account with no
/// limit set and a SAP lookup that falls over must both let the order through, because a check
/// that blocks when it cannot answer would stop all selling during a SAP outage.
/// </remarks>
public class CreditLimitTests
{
    private const string CustomerCardCode = "SAI034";
    private const string ParentCardCode = "PIN001";

    [Fact]
    public async Task Allows_an_order_that_stays_within_the_limit()
    {
        var service = CreateService(Profile(CustomerCardCode, creditLimit: 30_000m, balance: 20_000m));

        var result = await service.CheckSalesOrderAsync(CustomerCardCode, 5_000m);

        Assert.True(result.IsWithinLimit);
    }

    [Fact]
    public async Task Blocks_an_order_that_would_pass_the_limit()
    {
        var service = CreateService(Profile(CustomerCardCode, creditLimit: 30_000m, balance: 28_000m));

        var result = await service.CheckSalesOrderAsync(CustomerCardCode, 5_000m);

        Assert.False(result.IsWithinLimit);
        Assert.Equal(CustomerCardCode, result.CreditAccountCardCode);
        Assert.Equal(33_000m, result.Exposure);
        Assert.Contains("credit limit", result.Message);
        // The rep needs the numbers, not just a refusal.
        Assert.Contains("30,000.00", result.Message);
        Assert.Contains("3,000.00", result.Message);
    }

    [Fact]
    public async Task Blocks_an_account_already_over_its_limit()
    {
        // The state in the screenshot that prompted this: limit 30,000, balance 35,759.10.
        var service = CreateService(Profile(CustomerCardCode, creditLimit: 30_000m, balance: 35_759.10m));

        var result = await service.CheckSalesOrderAsync(CustomerCardCode, 1m);

        Assert.False(result.IsWithinLimit);
    }

    [Fact]
    public async Task Counts_open_orders_toward_exposure()
    {
        // Balance alone is within the limit; the orders already raised are what break it. Without
        // this, orders placed back to back each pass and jointly bust the limit.
        var service = CreateService(
            Profile(CustomerCardCode, creditLimit: 30_000m, balance: 20_000m, openOrders: 9_000m));

        var result = await service.CheckSalesOrderAsync(CustomerCardCode, 2_000m);

        Assert.False(result.IsWithinLimit);
        Assert.Equal(31_000m, result.Exposure);
    }

    [Fact]
    public async Task Ignores_open_orders_when_configured_to_match_sap_exactly()
    {
        var service = CreateService(
            Profile(CustomerCardCode, creditLimit: 30_000m, balance: 20_000m, openOrders: 9_000m),
            settings: new CreditLimitSettings { IncludeOpenOrders = false });

        var result = await service.CheckSalesOrderAsync(CustomerCardCode, 2_000m);

        Assert.True(result.IsWithinLimit);
    }

    [Fact]
    public async Task Allows_any_order_when_no_limit_is_set()
    {
        var service = CreateService(Profile(CustomerCardCode, creditLimit: 0m, balance: 90_000m));

        var result = await service.CheckSalesOrderAsync(CustomerCardCode, 50_000m);

        Assert.True(result.IsWithinLimit);
    }

    [Fact]
    public async Task Measures_a_child_account_against_its_parents_limit()
    {
        // The child is well within its own limit. The group is not, and that is the limit it draws on.
        var child = Profile(CustomerCardCode, creditLimit: 50_000m, balance: 5_000m, fatherCard: ParentCardCode);
        var parent = Profile(ParentCardCode, creditLimit: 30_000m, balance: 18_000m);
        var sibling = Profile("SAI099", creditLimit: 0m, balance: 6_000m, fatherCard: ParentCardCode);

        var service = CreateService(child, group: [parent, child, sibling]);

        var result = await service.CheckSalesOrderAsync(CustomerCardCode, 2_000m);

        Assert.False(result.IsWithinLimit);
        Assert.Equal(ParentCardCode, result.CreditAccountCardCode);
        Assert.Equal(31_000m, result.Exposure);
        Assert.Contains(ParentCardCode, result.Message);
        Assert.Contains("3 accounts", result.Message);
    }

    [Fact]
    public async Task Allows_a_child_account_when_the_group_is_within_its_limit()
    {
        var child = Profile(CustomerCardCode, creditLimit: 0m, balance: 5_000m, fatherCard: ParentCardCode);
        var parent = Profile(ParentCardCode, creditLimit: 30_000m, balance: 10_000m);

        var service = CreateService(child, group: [parent, child]);

        var result = await service.CheckSalesOrderAsync(CustomerCardCode, 2_000m);

        Assert.True(result.IsWithinLimit);
    }

    [Fact]
    public async Task Still_enforces_a_childs_own_limit_when_the_group_has_room()
    {
        // Both limits are real. The group has headroom; this account does not.
        var child = Profile(CustomerCardCode, creditLimit: 5_000m, balance: 4_000m, fatherCard: ParentCardCode);
        var parent = Profile(ParentCardCode, creditLimit: 500_000m, balance: 10_000m);

        var service = CreateService(child, group: [parent, child]);

        var result = await service.CheckSalesOrderAsync(CustomerCardCode, 2_000m);

        Assert.False(result.IsWithinLimit);
        Assert.Equal(CustomerCardCode, result.CreditAccountCardCode);
    }

    [Fact]
    public async Task Falls_back_to_the_accounts_own_limit_when_the_parent_holds_none()
    {
        var child = Profile(CustomerCardCode, creditLimit: 10_000m, balance: 9_000m, fatherCard: ParentCardCode);
        var parent = Profile(ParentCardCode, creditLimit: 0m, balance: 400_000m);

        var service = CreateService(child, group: [parent, child]);

        var result = await service.CheckSalesOrderAsync(CustomerCardCode, 2_000m);

        Assert.False(result.IsWithinLimit);
        Assert.Equal(CustomerCardCode, result.CreditAccountCardCode);
        // The parent's own huge balance must not leak into a check its limit does not govern.
        Assert.Equal(11_000m, result.Exposure);
    }

    [Fact]
    public async Task Allows_the_order_when_sap_cannot_be_reached()
    {
        var sap = StubProxy.For<ISAPServiceLayerClient>((method, _) =>
            method.Name == nameof(ISAPServiceLayerClient.GetBusinessPartnerCreditProfileAsync)
                ? Task.FromException<BusinessPartnerCreditProfileDto?>(new HttpRequestException("SAP is down"))
                : throw new InvalidOperationException($"Unexpected call to {method.Name}"));

        var result = await CreateService(sap).CheckSalesOrderAsync(CustomerCardCode, 5_000m);

        Assert.True(result.IsWithinLimit);
    }

    [Fact]
    public async Task Allows_the_order_when_the_group_lookup_fails_but_still_checks_the_account()
    {
        // A failed group read must not silently drop the account's own limit as well.
        var child = Profile(CustomerCardCode, creditLimit: 10_000m, balance: 9_500m, fatherCard: ParentCardCode);

        var sap = StubProxy.For<ISAPServiceLayerClient>((method, _) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetBusinessPartnerCreditProfileAsync) =>
                Task.FromResult<BusinessPartnerCreditProfileDto?>(child),
            nameof(ISAPServiceLayerClient.GetConsolidatedCreditProfilesAsync) =>
                Task.FromException<List<BusinessPartnerCreditProfileDto>>(new HttpRequestException("SAP is down")),
            _ => throw new InvalidOperationException($"Unexpected call to {method.Name}")
        });

        var result = await CreateService(sap).CheckSalesOrderAsync(CustomerCardCode, 1_000m);

        Assert.False(result.IsWithinLimit);
        Assert.Equal(CustomerCardCode, result.CreditAccountCardCode);
    }

    [Fact]
    public async Task Allows_the_order_when_the_customer_is_unknown_to_sap()
    {
        var sap = StubProxy.For<ISAPServiceLayerClient>((method, _) =>
            method.Name == nameof(ISAPServiceLayerClient.GetBusinessPartnerCreditProfileAsync)
                ? Task.FromResult<BusinessPartnerCreditProfileDto?>(null)
                : throw new InvalidOperationException($"Unexpected call to {method.Name}"));

        var result = await CreateService(sap).CheckSalesOrderAsync(CustomerCardCode, 5_000m);

        Assert.True(result.IsWithinLimit);
    }

    [Fact]
    public async Task Allows_everything_when_the_check_is_switched_off()
    {
        var service = CreateService(
            StubProxy.Unused<ISAPServiceLayerClient>(),
            new CreditLimitSettings { Enabled = false });

        var result = await service.CheckSalesOrderAsync(CustomerCardCode, 500_000m);

        Assert.True(result.IsWithinLimit);
    }

    [Fact]
    public async Task Reads_sap_once_per_card_code_within_a_scope()
    {
        // A web order is checked before it is created and again before it is posted. Both land in
        // one request scope, and the second must not pay for another SAP round trip.
        var calls = 0;
        var sap = StubProxy.For<ISAPServiceLayerClient>((method, _) =>
        {
            if (method.Name != nameof(ISAPServiceLayerClient.GetBusinessPartnerCreditProfileAsync))
                throw new InvalidOperationException($"Unexpected call to {method.Name}");

            calls++;
            return Task.FromResult<BusinessPartnerCreditProfileDto?>(
                Profile(CustomerCardCode, creditLimit: 30_000m, balance: 1_000m));
        });

        var service = CreateService(sap);
        await service.CheckSalesOrderAsync(CustomerCardCode, 100m);
        await service.CheckSalesOrderAsync(CustomerCardCode, 100m);

        Assert.Equal(1, calls);
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

    private static CreditLimitService CreateService(
        BusinessPartnerCreditProfileDto profile,
        List<BusinessPartnerCreditProfileDto>? group = null,
        CreditLimitSettings? settings = null)
    {
        var sap = StubProxy.For<ISAPServiceLayerClient>((method, _) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetBusinessPartnerCreditProfileAsync) =>
                Task.FromResult<BusinessPartnerCreditProfileDto?>(profile),
            nameof(ISAPServiceLayerClient.GetConsolidatedCreditProfilesAsync) =>
                Task.FromResult(group ?? []),
            _ => throw new InvalidOperationException($"Unexpected call to {method.Name}")
        });

        return CreateService(sap, settings);
    }

    private static CreditLimitService CreateService(
        ISAPServiceLayerClient sapClient,
        CreditLimitSettings? settings = null) =>
        new(sapClient,
            NullLogger<CreditLimitService>.Instance,
            Options.Create(settings ?? new CreditLimitSettings()));
}
