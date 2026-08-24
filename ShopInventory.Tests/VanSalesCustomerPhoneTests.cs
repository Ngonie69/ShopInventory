using ShopInventory.Features.VanSalesCustomerAuth;

namespace ShopInventory.Tests;

/// <summary>
/// The phone number is both the credential and the unique key of a customer account, so the
/// normaliser is what decides whether two spellings of one number are one customer or two.
///
/// The failure it exists to prevent is quiet: a customer onboarded as <c>+263771234567</c> who then
/// types <c>0771234567</c> at the login screen is simply told, forever, that no code is coming —
/// because the lookup missed, and the endpoint answers identically whether or not a number is
/// registered. There is no error anywhere to notice. Hence the cases below are spellings taken from
/// how the number actually gets entered: typed locally, pasted from a WhatsApp contact, imported
/// from a spreadsheet.
/// </summary>
public sealed class VanSalesCustomerPhoneTests
{
    private const string Zw = "+263";

    [Theory]
    // Local trunk form — what a customer types.
    [InlineData("0771234567")]
    // Pasted from a contact card, spaces and all.
    [InlineData("+263 77 123 4567")]
    // Country code, no plus — how spreadsheets tend to hold it.
    [InlineData("263771234567")]
    // International access prefix, as printed on stationery.
    [InlineData("00263771234567")]
    // Punctuation a human adds for readability.
    [InlineData("(077) 123-4567")]
    // Stray whitespace from a copy/paste.
    [InlineData("  0771234567  ")]
    public void All_the_ways_one_number_is_written_normalise_to_the_same_account_key(string input)
    {
        Assert.True(VanSalesCustomerPhone.TryNormalise(input, Zw, out var normalised), input);
        Assert.Equal("+263771234567", normalised);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a phone")]
    [InlineData("077-CALL-ME")]
    [InlineData("12345")]
    public void Input_that_cannot_be_a_number_is_refused_rather_than_guessed_at(string? input)
    {
        Assert.False(VanSalesCustomerPhone.TryNormalise(input, Zw, out var normalised));
        Assert.Equal(string.Empty, normalised);
    }

    [Fact]
    public void A_foreign_number_keeps_its_own_country_code()
    {
        // The default applies to local spellings only. A customer with a South African number must
        // not be silently rewritten into Zimbabwe.
        Assert.True(VanSalesCustomerPhone.TryNormalise("+27821234567", Zw, out var normalised));
        Assert.Equal("+27821234567", normalised);
    }

    [Fact]
    public void The_default_country_code_is_configurable()
    {
        Assert.True(VanSalesCustomerPhone.TryNormalise("0821234567", "+27", out var normalised));
        Assert.Equal("+27821234567", normalised);
    }

    [Fact]
    public void Normalising_is_idempotent()
    {
        // Numbers get re-saved by edits and imports; a second pass must not corrupt what the first produced.
        Assert.True(VanSalesCustomerPhone.TryNormalise("0771234567", Zw, out var once));
        Assert.True(VanSalesCustomerPhone.TryNormalise(once, Zw, out var twice));
        Assert.Equal(once, twice);
    }

    [Fact]
    public void Masking_shows_only_the_last_four_digits()
    {
        // Used when telling an operator where a code went, and in logs. The rest must not appear.
        var masked = VanSalesCustomerPhone.Mask("+263771234567");

        Assert.EndsWith("4567", masked);
        Assert.DoesNotContain("26377", masked);
    }
}
