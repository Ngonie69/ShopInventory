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
/// OCRD.CreditLine</c> — with a consolidating parent's limit governing the group. Open orders stay
/// out of it: only what the customer owes decides whether it is over. What matters most here is
/// which way each edge fails: an account with no
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
        // And not a figure the refusal was not measured on.
        Assert.DoesNotContain("open orders", result.Message);
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
    public async Task Ignores_open_orders_and_measures_the_balance_alone()
    {
        // Balance and this order sit inside the limit; the orders already raised are not owed yet and
        // must not refuse it. This is the FOR012 refusal that prompted the rule: 959.23 owed against
        // a 2,000 limit, refused on 4,726.90 of open orders alone.
        var service = CreateService(
            Profile(CustomerCardCode, creditLimit: 30_000m, balance: 20_000m, openOrders: 9_000m));

        var result = await service.CheckSalesOrderAsync(CustomerCardCode, 2_000m);

        Assert.True(result.IsWithinLimit);
    }

    [Fact]
    public async Task Counts_open_orders_toward_exposure_when_configured_to()
    {
        // The tighter reading, off by default: orders placed back to back each pass on the balance
        // alone and jointly bust the limit.
        var service = CreateService(
            Profile(CustomerCardCode, creditLimit: 30_000m, balance: 20_000m, openOrders: 9_000m),
            settings: new CreditLimitSettings { IncludeOpenOrders = true });

        var result = await service.CheckSalesOrderAsync(CustomerCardCode, 2_000m);

        Assert.False(result.IsWithinLimit);
        Assert.Equal(31_000m, result.Exposure);
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

    /// <summary>
    /// The same order must get the same answer whether it is measured in memory at capture or read
    /// back from Postgres at approval.
    /// </summary>
    /// <remarks>
    /// Production showed the drift directly: the capture-time refusal logged
    /// <c>1050.484050000000000000</c> and the approval-time one logged <c>1050.48</c> for one order.
    /// A total is computed as <c>quantity * unitPrice * (1 - discount/100)</c>, which carries far
    /// more scale than the column it lands in, so the two gates were comparing different numbers.
    /// Rounding once inside the check is what makes them agree.
    /// </remarks>
    [Fact]
    public async Task Measures_the_order_on_its_rounded_total()
    {
        // Balance leaves exactly 1,050.48 of headroom. The raw total exceeds it; the rounded one
        // does not — so without rounding this order clears the capture gate and fails at approval.
        var service = CreateService(Profile(CustomerCardCode, creditLimit: 30_000m, balance: 28_949.52m));

        var raw = await service.CheckSalesOrderAsync(CustomerCardCode, 1_050.484050000000000000m);
        var stored = await service.CheckSalesOrderAsync(CustomerCardCode, 1_050.48m);

        Assert.True(raw.IsWithinLimit);
        Assert.True(stored.IsWithinLimit);
    }

    /// <summary>Rounding is half-away-from-zero, so a hair over the limit is still over it.</summary>
    [Fact]
    public async Task Rounds_a_half_cent_up_rather_than_away()
    {
        var service = CreateService(Profile(CustomerCardCode, creditLimit: 30_000m, balance: 28_949.52m));

        var result = await service.CheckSalesOrderAsync(CustomerCardCode, 1_050.485m);

        Assert.False(result.IsWithinLimit);
        Assert.Equal(30_000.01m, result.Exposure);
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
