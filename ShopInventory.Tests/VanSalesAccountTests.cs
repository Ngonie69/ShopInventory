using ShopInventory.Common.Pods;

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
    // than business partners the sales documents are raised against.
    [InlineData("VAN001", false)]
    [InlineData("VAN006", false)]
    [InlineData("VAN007", false)]
    [InlineData("VAN021", false)]
    [InlineData("SIM001", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Van_sales_business_partners_are_recognised(string? cardCode, bool expected)
    {
        Assert.Equal(expected, ShopInventory.Common.Sales.VanSalesAccounts.IsVanSalesAccount(cardCode));
        Assert.Equal(expected, ShopInventory.Web.Common.VanSalesAccounts.IsVanSalesAccount(cardCode));
    }

    /// <summary>
    /// The Web ranks from its own order window and the API ranks from the
    /// invoice report, so each project carries the list — the two do not
    /// reference each other. They have to name the same accounts, or the same
    /// customer would be excluded from the dashboard and not from the report.
    /// </summary>
    [Fact]
    public void The_web_copy_of_the_list_matches_the_api()
    {
        Assert.Equal(
            ShopInventory.Common.Sales.VanSalesAccounts.CardCodes.OrderBy(code => code, StringComparer.Ordinal),
            ShopInventory.Web.Common.VanSalesAccounts.CardCodes.OrderBy(code => code, StringComparer.Ordinal));
    }

    /// <summary>
    /// POD follow-up folds the van accounts into its own wider exclusion set.
    /// Nothing that was excluded from a POD chase before may fall out of it now
    /// the codes are held in one place.
    /// </summary>
    [Fact]
    public void Pod_follow_up_still_excludes_every_van_sales_account()
    {
        for (var number = 8; number <= 20; number++)
        {
            var cardCode = $"VAN{number:000}";

            Assert.True(ShopInventory.Common.Sales.VanSalesAccounts.IsVanSalesAccount(cardCode), cardCode);
            Assert.True(PodExclusions.IsExcludedCardCode(cardCode), cardCode);
        }

        // The accounts POD excludes for its own reasons are untouched. The count
        // is the whole list — 27 POD-only codes and the 13 van accounts — and is
        // here so a code cannot go missing in a rearrangement without saying so.
        Assert.True(PodExclusions.IsExcludedCardCode("CIS006"));
        Assert.True(PodExclusions.IsExcludedCardCode("CAS004(FCA)"));
        Assert.True(PodExclusions.IsExcludedCardCode("TEA007"));
        Assert.Equal(40, PodExclusions.ExcludedCardCodes.Count);
    }
}
