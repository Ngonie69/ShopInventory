using ShopInventory.DTOs;

namespace ShopInventory.Tests;

public class SpecialPriceLookupResultTests
{
    [Fact]
    public void Default_value_does_not_claim_to_be_complete()
    {
        // The flag is deliberately IsComplete rather than IsIncomplete: a struct's default has to
        // mean "assume nothing was retrieved". Flipping the polarity would make an uninitialised
        // result assert completeness and silently reintroduce the overcharge this type prevents.
        Assert.False(default(SpecialPriceLookupResult).IsComplete);
    }

    [Fact]
    public void Empty_and_failed_lookups_are_distinguishable()
    {
        // The whole point of the type. Both carry no prices; only one means the customer has none.
        var customerHasNoSpecialPrices = new SpecialPriceLookupResult([], IsComplete: true);
        var lookupFailed = new SpecialPriceLookupResult([], IsComplete: false);

        Assert.Empty(customerHasNoSpecialPrices.Prices);
        Assert.Empty(lookupFailed.Prices);
        Assert.NotEqual(customerHasNoSpecialPrices.IsComplete, lookupFailed.IsComplete);
    }

    [Fact]
    public void A_partial_lookup_still_reports_the_prices_it_did_find()
    {
        // A failure part-way through paging leaves some prices behind. They are usable, but the
        // caller must not treat the set as exhaustive.
        var partial = new SpecialPriceLookupResult(
            new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["YOG149"] = 12.50m },
            IsComplete: false);

        Assert.Equal(12.50m, partial.Prices["yog149"]);
        Assert.False(partial.IsComplete);
    }
}
