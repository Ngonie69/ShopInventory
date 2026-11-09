using ShopInventory.Web.Components.Pages;

namespace ShopInventory.Tests;

/// <summary>
/// /whatsapp-contacts marks the digits being searched for in each number, matching them the way the
/// API's search does, so what is highlighted is what was found.
/// </summary>
public sealed class WhatsAppContactsSearchHighlightTests
{
    [Theory]
    [InlineData("1567", "1567")]
    [InlineData("0771 231 567", "771231567")]
    [InlineData("Cash", null)]
    [InlineData("15", null)]
    [InlineData(null, null)]
    public void The_page_reads_a_search_the_way_the_api_does(string? search, string? expected)
    {
        Assert.Equal(expected, WhatsAppContacts.PhoneSearchDigits(search));
        Assert.Equal(
            ShopInventory.Features.CustomerDocuments.Queries.GetCustomerWhatsAppContactRegister.GetCustomerWhatsAppContactRegisterHandler.PhoneDigits(search ?? string.Empty),
            WhatsAppContacts.PhoneSearchDigits(search));
    }

    [Fact]
    public void The_last_four_digits_are_marked()
    {
        var segments = WhatsAppContacts.PhoneSegments("+263771231567", "1567");

        Assert.Equal([("+26377123", false), ("1567", true)], segments);
    }

    [Fact]
    public void A_match_across_spaces_marks_the_spaces_between_its_digits_only()
    {
        var segments = WhatsAppContacts.PhoneSegments("+263 77 123 1567", "31567");

        Assert.Equal([("+263 77 12", false), ("3 1567", true)], segments);
    }

    [Fact]
    public void No_search_or_no_match_marks_nothing()
    {
        Assert.Equal([("+263771231567", false)], WhatsAppContacts.PhoneSegments("+263771231567", null));
        Assert.Equal([("+263771231567", false)], WhatsAppContacts.PhoneSegments("+263771231567", "9999"));
    }
}
