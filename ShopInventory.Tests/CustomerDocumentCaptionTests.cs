using ShopInventory.Configuration;
using ShopInventory.Features.CustomerDocuments.Delivery;
using ShopInventory.Features.CustomerDocuments.Documents;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>The words sent with a document, its file name, and when automatic sends may go.</summary>
public sealed class CustomerDocumentCaptionTests
{
    [Fact]
    public void The_caption_names_the_customer_the_invoice_and_its_total()
    {
        var caption = CustomerDocumentCaption.Render(
            new CustomerDocumentDeliverySettings().CaptionTemplate,
            "Spar Bridge",
            "780100",
            new DateTime(2026, 10, 7),
            "USD",
            1234.5m);

        Assert.Equal(
            "Good day Spar Bridge. Please find attached Kefalos tax invoice 780100 dated 07 Oct 2026 for USD 1,234.50. Reply to this message with any query.",
            caption);
    }

    [Fact]
    public void SAPs_all_currencies_marker_is_never_printed_as_a_currency()
    {
        var caption = CustomerDocumentCaption.Render("{currency} {total}.", "x", "1", null, "##", 10m);

        Assert.Equal("10.00.", caption);
    }

    [Fact]
    public void A_caption_is_cut_at_a_word_to_fit_the_gateways_limit()
    {
        var caption = CustomerDocumentCaption.Render(string.Join(" ", Enumerable.Repeat("invoice", 300)), "x", "1", null, null, null);

        Assert.True(caption.Length <= CustomerDocumentCaption.MaxCaptionLength);
        Assert.EndsWith("invoice…", caption);
    }

    [Theory]
    [InlineData("Kefalos-Invoice-{number}.pdf", "780100", "Kefalos-Invoice-780100.pdf")]
    [InlineData("Invoice {number}", "780100", "Invoice 780100.pdf")]
    [InlineData("Invoice/{number}.pdf", "1", "Invoice-1.pdf")]
    public void The_file_name_is_safe_and_ends_in_pdf(string template, string number, string expected)
    {
        Assert.Equal(expected, CustomerDocumentCaption.FileName(template, number));
    }

    [Fact]
    public void Automatic_sends_keep_to_the_CAT_window_and_skip_Sundays_unless_allowed()
    {
        var settings = new CustomerDocumentDeliverySettings { AutoWindowStartCat = "07:30", AutoWindowEndCat = "18:00" };

        // Wednesday 7 October 2026.
        Assert.True(DeliveryBudget.IsWithinAutoWindow(AuditService.FromCAT(new DateTime(2026, 10, 7, 7, 30, 0)), settings));
        Assert.False(DeliveryBudget.IsWithinAutoWindow(AuditService.FromCAT(new DateTime(2026, 10, 7, 7, 29, 0)), settings));
        Assert.False(DeliveryBudget.IsWithinAutoWindow(AuditService.FromCAT(new DateTime(2026, 10, 7, 18, 0, 0)), settings));

        var sunday = AuditService.FromCAT(new DateTime(2026, 10, 11, 10, 0, 0));
        Assert.False(DeliveryBudget.IsWithinAutoWindow(sunday, settings));
        settings.AutoSendOnSundays = true;
        Assert.True(DeliveryBudget.IsWithinAutoWindow(sunday, settings));
    }

    [Fact]
    public void A_CAT_day_starts_at_midnight_CAT_not_UTC()
    {
        // 23:30 UTC on 7 October is 01:30 CAT on the 8th.
        var lateUtc = new DateTime(2026, 10, 7, 23, 30, 0, DateTimeKind.Utc);

        Assert.Equal(new DateTime(2026, 10, 7, 22, 0, 0), DeliveryBudget.CatDayStartUtc(lateUtc));
        Assert.Equal(new DateTime(2026, 10, 8, 22, 0, 0), DeliveryBudget.NextCatDayStartUtc(lateUtc));
    }
}
