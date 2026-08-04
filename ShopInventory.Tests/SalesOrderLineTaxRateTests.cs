using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins the tax rate a sales order line is stored with.
/// </summary>
/// <remarks>
/// The rule used to fall back to the configured VAT rate for mobile orders only, which left every
/// web order's stored tax depending on the create form remembering to send a rate. It did not, so
/// web orders were written with zero-rate lines, a zero tax amount, and a document total equal to
/// their subtotal — understated against the SAP document, which prices tax from each item's own tax
/// code regardless of what we send. The credit check runs on that document total, so those orders
/// were also checked short. The fallback must stay source-independent: a caller that sends nothing
/// gets the configured rate, whoever it is.
/// </remarks>
public class SalesOrderLineTaxRateTests
{
    private const decimal ConfiguredVatPercent = 15.5m;

    private static decimal Resolve(decimal requestedTaxPercent) =>
        SalesOrderService.ResolveLineTaxPercent(requestedTaxPercent, ConfiguredVatPercent);

    [Fact]
    public void A_line_that_asks_for_nothing_gets_the_configured_rate()
    {
        Assert.Equal(ConfiguredVatPercent, Resolve(0m));
    }

    [Fact]
    public void A_line_that_names_its_own_rate_keeps_it()
    {
        Assert.Equal(5m, Resolve(5m));
    }

    /// <summary>
    /// A negative rate is not a request for negative tax; it is the same absence of an answer that
    /// zero is, and must not survive into a stored total.
    /// </summary>
    [Fact]
    public void A_nonsense_rate_falls_back_rather_than_being_stored()
    {
        Assert.Equal(ConfiguredVatPercent, Resolve(-1m));
    }
}
