using ShopInventory.Web.Common;

namespace ShopInventory.Tests;

public sealed class VanSalesAccountTests
{
    [Theory]
    [InlineData("VAN008", true)]
    [InlineData("VAN020", true)]
    [InlineData("VAN014", true)]
    [InlineData("van011", true)]
    [InlineData(" VAN009 ", true)]
    // The range stops short of the lower van codes, which are warehouses rather
    // than business partners the sales orders are raised against.
    [InlineData("VAN001", false)]
    [InlineData("VAN006", false)]
    [InlineData("VAN007", false)]
    [InlineData("VAN021", false)]
    [InlineData("SIM001", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Van_sales_business_partners_are_recognised(string? cardCode, bool expected) =>
        Assert.Equal(expected, VanSalesAccounts.IsVanSalesAccount(cardCode));

    /// <summary>
    /// The sales rep dashboard and the POD follow-up have to agree on which van
    /// accounts they are dropping, or the same order would be a rep's business
    /// in one place and not the other.
    /// </summary>
    [Fact]
    public void Every_van_sales_account_is_also_excluded_from_pod_follow_up()
    {
        for (var number = 8; number <= 20; number++)
        {
            var cardCode = $"VAN{number:000}";

            Assert.True(VanSalesAccounts.IsVanSalesAccount(cardCode), cardCode);
            Assert.True(ShopInventory.Common.Pods.PodExclusions.IsExcludedCardCode(cardCode), cardCode);
        }
    }
}
