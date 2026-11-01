using ShopInventory.Common.Sales;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Switching a partner on dates the day its daily payment pays from, in CAT, and nothing else moves it.
/// </summary>
public sealed class DailyPaymentSwitchTests
{
    /// <summary>22:30 UTC on 1 Nov is 00:30 CAT on 2 Nov.</summary>
    private static readonly DateTime LateEvening = new(2026, 11, 1, 22, 30, 0, DateTimeKind.Utc);

    private static readonly DateTime Earlier = new(2026, 10, 1);

    [Fact]
    public void Switching_on_pays_from_that_day_in_CAT()
    {
        var mapping = new IncomingPaymentGlMappingEntity { IsActive = false, PaysFrom = Earlier };

        DailyPaymentSwitch.Set(mapping, true, LateEvening);

        Assert.True(mapping.IsActive);
        Assert.Equal(new DateTime(2026, 11, 2), mapping.PaysFrom);
    }

    [Fact]
    public void Staying_on_keeps_the_day_it_was_switched_on()
    {
        var mapping = new IncomingPaymentGlMappingEntity { IsActive = true, PaysFrom = Earlier };

        DailyPaymentSwitch.Set(mapping, true, LateEvening);

        Assert.Equal(Earlier, mapping.PaysFrom);
    }

    [Fact]
    public void Switching_off_leaves_the_day_alone()
    {
        var mapping = new IncomingPaymentGlMappingEntity { IsActive = true, PaysFrom = Earlier };

        DailyPaymentSwitch.Set(mapping, false, LateEvening);

        Assert.False(mapping.IsActive);
        Assert.Equal(Earlier, mapping.PaysFrom);
    }

    [Fact]
    public void A_new_partner_saved_as_on_pays_from_that_day()
    {
        // A new row starts with IsActive true, so only knowing it is new tells this apart from staying on.
        var mapping = new IncomingPaymentGlMappingEntity();

        DailyPaymentSwitch.Set(mapping, true, LateEvening, isNew: true);

        Assert.Equal(new DateTime(2026, 11, 2), mapping.PaysFrom);
    }
}
