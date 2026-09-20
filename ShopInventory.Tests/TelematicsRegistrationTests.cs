using ShopInventory.Common.Telematics;
using Xunit;

namespace ShopInventory.Tests;

/// <summary>
/// The join between a route's truck and the fleet provider's vehicle is a string comparison, and
/// both sides of it are free text. These pin the spellings that have to collapse onto one key —
/// because the failure mode is silent: a plate that does not match reports the van as having never
/// moved, which reads exactly like a van that never moved.
/// </summary>
public class TelematicsRegistrationTests
{
    [Theory]
    [InlineData("AHF0218")]
    [InlineData("ahf0218")]
    [InlineData("AHF 0218")]
    [InlineData("AHF-0218")]
    [InlineData(" AHF0218 ")]
    [InlineData("ahf 0218 ")]
    [InlineData("AHF/0218")]
    public void Every_spelling_of_one_plate_normalizes_to_one_key(string typed)
    {
        Assert.Equal("AHF0218", TelematicsRegistration.Normalize(typed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-")]
    [InlineData(" / ")]
    public void A_blank_or_punctuation_only_registration_is_null_rather_than_empty(string? typed)
    {
        // Null rather than "": two routes with no truck assigned must not collide on one key and
        // be told they share a vehicle.
        Assert.Null(TelematicsRegistration.Normalize(typed));
    }

    [Fact]
    public void Two_plates_that_differ_only_in_spelling_are_the_same_vehicle()
    {
        Assert.True(TelematicsRegistration.AreSame("AFQ 9644", "afq-9644"));
    }

    [Fact]
    public void Two_different_plates_are_not_the_same_vehicle()
    {
        Assert.False(TelematicsRegistration.AreSame("AFQ9644", "AFQ9645"));
    }

    [Fact]
    public void A_blank_registration_matches_nothing_including_another_blank()
    {
        Assert.False(TelematicsRegistration.AreSame(null, null));
        Assert.False(TelematicsRegistration.AreSame("  ", ""));
        Assert.False(TelematicsRegistration.AreSame(null, "AFQ9644"));
    }

    [Fact]
    public void Digits_and_letters_survive_in_the_order_they_were_typed()
    {
        // Cartrack's own fleet names the vehicle "306_AFQ9644" — a fleet number joined to the
        // plate. That is not a registration and must not normalize to one, or a lookup on the
        // vehicle's name would silently match the route's truck.
        Assert.Equal("306AFQ9644", TelematicsRegistration.Normalize("306_AFQ9644"));
        Assert.NotEqual(
            TelematicsRegistration.Normalize("AFQ9644"),
            TelematicsRegistration.Normalize("306_AFQ9644"));
    }

    [Fact]
    public void A_registration_longer_than_the_stack_buffer_still_normalizes()
    {
        // The implementation stack-allocates up to 64 characters and heap-allocates past that.
        // Nothing sane is this long, but the boundary is in the code so it is pinned here.
        var typed = new string('a', 80) + " 1";

        Assert.Equal(new string('A', 80) + "1", TelematicsRegistration.Normalize(typed));
    }
}
