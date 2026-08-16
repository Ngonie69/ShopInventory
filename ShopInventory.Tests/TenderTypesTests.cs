using ShopInventory.Common.Sales;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Tests;

/// <summary>
/// Pins where a shop tender lands downstream.
///
/// A till takes cash, a card swipe, Ecocash and Innbucks, and each one has to reach two places that
/// disagree about vocabulary: ZIMRA wants a <see cref="MoneyType"/> on the fiscal receipt, and SAP
/// wants one of several parallel sums on the incoming payment. Both used to be decided by code that
/// could only ever produce "cash" — the receipt had <c>MoneyType.Cash</c> written into it as a
/// constant, and the van ingest path parsed the payment method as a <see cref="MoneyType"/> name,
/// which no brand has ever matched. Every non-cash sale was therefore declared to the revenue
/// authority as cash, and the mistake was invisible because nothing failed.
///
/// These are the mappings that fix that, so they are worth pinning rather than inferring.
/// </summary>
public sealed class TenderTypesTests
{
    // ---- What ZIMRA is told -------------------------------------------------------------------

    [Theory]
    [InlineData(TenderTypes.Cash, MoneyType.Cash)]
    [InlineData(TenderTypes.Swipe, MoneyType.Card)]
    [InlineData(TenderTypes.Ecocash, MoneyType.MobileWallet)]
    [InlineData(TenderTypes.Innbucks, MoneyType.MobileWallet)]
    public void Each_tender_declares_its_own_money_type(string tender, MoneyType expected)
    {
        Assert.Equal(expected, TenderTypes.ToMoneyType(tender));
    }

    [Fact]
    public void A_wallet_sale_is_not_declared_as_cash()
    {
        // The regression this type exists for. Parsing the brand as an enum name matched nothing and
        // fell back to cash, so a fiscal receipt for an Ecocash sale claimed the customer paid cash.
        Assert.NotEqual(MoneyType.Cash, TenderTypes.ToMoneyType(TenderTypes.Ecocash));
        Assert.NotEqual(MoneyType.Cash, TenderTypes.ToMoneyType(TenderTypes.Innbucks));
        Assert.NotEqual(MoneyType.Cash, TenderTypes.ToMoneyType(TenderTypes.Swipe));
    }

    // ---- Which SAP sum the money goes into ----------------------------------------------------

    [Theory]
    [InlineData(TenderTypes.Cash, PaymentSum.Cash)]
    [InlineData(TenderTypes.Swipe, PaymentSum.Credit)]
    [InlineData(TenderTypes.Ecocash, PaymentSum.Transfer)]
    [InlineData(TenderTypes.Innbucks, PaymentSum.Transfer)]
    public void Each_tender_posts_to_its_own_sap_sum(string tender, PaymentSum expected)
    {
        Assert.Equal(expected, TenderTypes.ToPaymentSum(tender));
    }

    [Fact]
    public void A_swipe_does_not_land_in_the_cash_till()
    {
        // Consolidation's switch had no card case, so a swipe fell to the default and was posted as
        // CashSum — money the till never physically held.
        Assert.Equal(PaymentSum.Credit, TenderTypes.ToPaymentSum(TenderTypes.Swipe));
    }

    [Fact]
    public void The_two_wallets_share_a_sum_but_stay_distinct_tenders()
    {
        // Both settle as transfers, so under SAP's default accounts they land together. That is
        // accepted — but the sale rows must still say which wallet it was, or the shop cannot tell
        // an Ecocash day from an Innbucks one.
        Assert.Equal(TenderTypes.ToPaymentSum(TenderTypes.Ecocash), TenderTypes.ToPaymentSum(TenderTypes.Innbucks));
        Assert.NotEqual(TenderTypes.Ecocash, TenderTypes.Innbucks);
    }

    // ---- Nothing is guessed --------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Bitcoin")]
    public void An_absent_or_unknown_tender_maps_to_nothing(string? tender)
    {
        // Null rather than cash. A fiscal receipt is a declaration, so the fallback has to be a
        // decision a caller makes in the open, not one this mapping makes quietly.
        Assert.Null(TenderTypes.ToMoneyType(tender));
        Assert.Null(TenderTypes.ToPaymentSum(tender));
    }

    // ---- What a till is allowed to send --------------------------------------------------------

    [Theory]
    [InlineData("cash", TenderTypes.Cash)]
    [InlineData("ECOCASH", TenderTypes.Ecocash)]
    [InlineData("  Innbucks  ", TenderTypes.Innbucks)]
    [InlineData("swipe machine", TenderTypes.Swipe)]
    [InlineData("Card", TenderTypes.Swipe)]
    public void Casing_and_wording_differences_normalise_to_one_spelling(string supplied, string expected)
    {
        Assert.True(TenderTypes.TryNormalize(supplied, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("transfer")]
    [InlineData("paynow")]
    [InlineData("cheque")]
    [InlineData(null)]
    public void Values_a_till_may_not_send_are_rejected(string? supplied)
    {
        // "transfer" and "paynow" are real values on historical rows, but nothing new should be
        // written with them — the validator rejects them so a shop cannot quietly reintroduce a
        // tender the checkout does not offer.
        Assert.False(TenderTypes.IsSupported(supplied));
    }

    [Fact]
    public void Legacy_stored_values_still_map_for_reporting()
    {
        // The rows already in the table have to keep resolving, even though a till can no longer
        // send these.
        Assert.Equal(MoneyType.Cash, TenderTypes.ToMoneyType("cash"));
        Assert.Equal(MoneyType.MobileWallet, TenderTypes.ToMoneyType("transfer"));
        Assert.Equal(MoneyType.MobileWallet, TenderTypes.ToMoneyType("paynow"));
        Assert.Equal(PaymentSum.Transfer, TenderTypes.ToPaymentSum("transfer"));
    }

    // ---- References ----------------------------------------------------------------------------

    [Theory]
    [InlineData(TenderTypes.Ecocash, true)]
    [InlineData(TenderTypes.Innbucks, true)]
    [InlineData(TenderTypes.Cash, false)]
    [InlineData(TenderTypes.Swipe, false)]
    [InlineData(null, false)]
    public void Only_the_wallets_need_a_reference(string? tender, bool expected)
    {
        // A wallet settles outside the till, so the confirmation reference is the only thing tying
        // the receipt to money that arrived.
        Assert.Equal(expected, TenderTypes.RequiresReference(tender));
    }

    [Fact]
    public void Every_supported_tender_resolves_completely()
    {
        // A tender added to the list without being added to both mappings would otherwise pass
        // validation and then be declared as cash and posted to the wrong account.
        Assert.All(TenderTypes.SupportedTenders, tender =>
        {
            Assert.True(TenderTypes.IsSupported(tender));
            Assert.NotNull(TenderTypes.ToMoneyType(tender));
            Assert.NotNull(TenderTypes.ToPaymentSum(tender));
        });
    }
}
