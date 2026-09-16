using ShopInventory.Common.Sales;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Pins how a day's till money is split across SAP's payment sums. The tender decides which SAP account
/// real money lands in, and getting it wrong is invisible until someone reconciles by hand.
/// </summary>
public sealed class DailyIncomingPaymentBuilderTests
{
    /// <summary>The old route: a swipe carried on a SAP credit card line.</summary>
    private static readonly SwipeSettlement CardRoute = new(CreditCardCode: 7, TransferAccount: null);

    /// <summary>The route in use: card money banked into a G/L account as transfer money.</summary>
    private static readonly SwipeSettlement BankRoute = new(CreditCardCode: null, TransferAccount: "1300");

    private static DesktopSaleEntity Sale(
        string? paymentMethod = TenderTypes.Cash,
        decimal amountPaid = 25m,
        decimal totalAmount = 25m,
        int? consolidationId = null) => new()
        {
            ExternalReferenceId = "KEFSHOP-01-20260913-000123",
            CardCode = "KEFSHOP-BP",
            Currency = "USD",
            WarehouseCode = "KEFSHOP",
            PaymentMethod = paymentMethod,
            AmountPaid = amountPaid,
            TotalAmount = totalAmount,
            ConsolidationId = consolidationId
        };

    [Fact]
    public void Cash_goes_in_the_cash_sum()
    {
        var split = DailyIncomingPaymentBuilder.SplitSale(Sale(TenderTypes.Cash), SwipeSettlement.None);

        Assert.Equal(new TenderSplit(25m, 0m, 0m), split.Split);
    }

    [Theory]
    [InlineData(TenderTypes.Ecocash)]
    [InlineData(TenderTypes.Innbucks)]
    public void A_wallet_goes_in_the_transfer_sum(string tender)
    {
        var split = DailyIncomingPaymentBuilder.SplitSale(Sale(tender), SwipeSettlement.None);

        Assert.Equal(new TenderSplit(0m, 25m, 0m), split.Split);
    }

    [Fact]
    public void A_swipe_waits_until_a_card_code_is_configured()
    {
        // Inventing a card code would book real money against the wrong card.
        var split = DailyIncomingPaymentBuilder.SplitSale(Sale(TenderTypes.Swipe), SwipeSettlement.None);

        Assert.False(split.IsMapped);
        Assert.Contains("SwipeCreditCardCode", split.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bitcoin")]
    public void An_unmappable_tender_is_never_guessed_into_cash(string? tender)
    {
        var split = DailyIncomingPaymentBuilder.SplitSale(Sale(tender), CardRoute);

        Assert.False(split.IsMapped);
    }

    [Fact]
    public void A_consolidated_invoice_splits_by_the_tender_each_sale_was_paid_in()
    {
        // The old consolidation booked the whole day under the most common tender, so wallet and card
        // money landed in cash.
        var consolidation = new SaleConsolidationEntity { Id = 4, TotalAmount = 100m };
        var sales = new List<DesktopSaleEntity>
        {
            Sale(TenderTypes.Cash, 30m, 30m, 4),
            Sale(TenderTypes.Cash, 20m, 20m, 4),
            Sale(TenderTypes.Ecocash, 10m, 10m, 4),
            Sale(TenderTypes.Swipe, 15m, 15m, 4)
        };

        var split = DailyIncomingPaymentBuilder.SplitConsolidation(consolidation, sales, CardRoute);

        // 25 of the invoice came from fiscalised queue entries, which the consolidation always took as
        // paid in full in cash.
        Assert.Equal(new TenderSplit(75m, 10m, 15m), split.Split);
    }

    [Fact]
    public void One_unmappable_sale_makes_its_consolidated_invoice_unmappable()
    {
        var consolidation = new SaleConsolidationEntity { Id = 4, TotalAmount = 50m };
        var sales = new List<DesktopSaleEntity>
        {
            Sale(TenderTypes.Cash, 25m, 25m, 4),
            Sale("Bitcoin", 25m, 25m, 4)
        };

        var split = DailyIncomingPaymentBuilder.SplitConsolidation(consolidation, sales, CardRoute);

        Assert.False(split.IsMapped);
    }

    [Theory]
    [InlineData(25, 25)]
    [InlineData(40, 25)]
    [InlineData(10, 10)]
    [InlineData(0, 0)]
    public void A_line_never_applies_more_than_its_invoice_owes(decimal openBalance, decimal expected)
    {
        var capped = DailyIncomingPaymentBuilder.CapTo(new TenderSplit(25m, 0m, 0m), openBalance);

        Assert.Equal(expected, capped.Total);
    }

    [Fact]
    public void The_request_settles_every_invoice_and_carries_each_tender_in_its_own_sum()
    {
        var payment = new DailyIncomingPaymentEntity
        {
            CardCode = "KEFSHOP-BP",
            PaymentDate = new DateTime(2026, 9, 13),
            Reference = DailyIncomingPaymentBuilder.ReferenceFor(new DateTime(2026, 9, 13), "KEFSHOP-BP"),
            Lines =
            [
                new DailyIncomingPaymentLineEntity { InvoiceDocEntry = 101, CashAmount = 25m },
                new DailyIncomingPaymentLineEntity { InvoiceDocEntry = 102, TransferAmount = 12.50m },
                new DailyIncomingPaymentLineEntity { InvoiceDocEntry = 103, CreditAmount = 40m },
                new DailyIncomingPaymentLineEntity { InvoiceDocEntry = 104, CashAmount = 5m }
            ]
        };

        var request = DailyIncomingPaymentBuilder.BuildRequest(payment, CardRoute);

        Assert.Equal("KEFSHOP-BP", request.CardCode);
        Assert.Equal("2026-09-13", request.DocDate);
        Assert.Equal(30m, request.CashSum);
        Assert.Equal(12.50m, request.TransferSum);
        Assert.Equal(40m, request.CreditSum);
        Assert.Equal("2026-09-13", request.TransferDate);

        Assert.Equal([101, 102, 103, 104], request.PaymentInvoices!.Select(line => line.DocEntry));
        Assert.Equal(request.CashSum + request.TransferSum + request.CreditSum,
            request.PaymentInvoices!.Sum(line => line.SumApplied));

        var card = Assert.Single(request.PaymentCreditCards!);
        Assert.Equal(7, card.CreditCard);
        Assert.Equal(40m, card.CreditSum);

        // The reference opens the Remarks: it is what a lost reply is resolved by.
        Assert.StartsWith("DAYPAY-20260913-KEFSHOP-BP:", request.Remarks);
    }

    // ---- Card money banked instead of carried on a card account -------------------------------------
    //
    // The company database has no credit card records at all, so SwipeCreditCardCode could never be set
    // and every card sale was invoiced and left open: 15 of them on 2026-09-15, 596.83 the cash desk had
    // counted and banked. Banking it as transfer money is what the money actually does.

    [Fact]
    public void A_swipe_settles_when_only_the_bank_account_is_configured()
    {
        var split = DailyIncomingPaymentBuilder.SplitSale(Sale(TenderTypes.Swipe, amountPaid: 384.22m), BankRoute);

        Assert.True(split.IsMapped);
        // Held as card money in the claim; it becomes transfer money only on the SAP request, so the
        // distinction survives for reporting and for changing route later.
        Assert.Equal(384.22m, split.Split!.Value.Credit);
    }

    [Fact]
    public void A_swipe_with_neither_route_configured_says_which_settings_would_fix_it()
    {
        var split = DailyIncomingPaymentBuilder.SplitSale(Sale(TenderTypes.Swipe), SwipeSettlement.None);

        Assert.False(split.IsMapped);
        Assert.Contains("SwipeTransferAccount", split.Reason);
        Assert.Contains("SwipeCreditCardCode", split.Reason);
    }

    [Fact]
    public void Card_money_is_banked_as_transfer_money_against_the_configured_account()
    {
        var payment = PaymentWith(
            new DailyIncomingPaymentLineEntity { InvoiceDocEntry = 101, CashAmount = 25m },
            new DailyIncomingPaymentLineEntity { InvoiceDocEntry = 102, CreditAmount = 40m });

        var request = DailyIncomingPaymentBuilder.BuildRequest(payment, BankRoute);

        Assert.Equal(25m, request.CashSum);
        Assert.Equal(40m, request.TransferSum);
        Assert.Equal(0m, request.CreditSum);
        Assert.Equal("1300", request.TransferAccount);
        Assert.Equal("2026-09-13", request.TransferDate);
        Assert.True(request.PaymentCreditCards is null or { Count: 0 });
    }

    [Fact]
    public void The_bank_account_wins_when_both_routes_are_configured()
    {
        var payment = PaymentWith(new DailyIncomingPaymentLineEntity { InvoiceDocEntry = 101, CreditAmount = 40m });

        var request = DailyIncomingPaymentBuilder.BuildRequest(payment, new SwipeSettlement(7, "1300"));

        Assert.Equal(40m, request.TransferSum);
        Assert.Equal(0m, request.CreditSum);
        Assert.Equal("1300", request.TransferAccount);
    }

    [Fact]
    public void Wallet_money_on_the_same_day_leaves_the_account_off_rather_than_posting_into_it()
    {
        // SAP takes one transfer account per payment. Naming the card account here would book the
        // wallet takings into it, which only a hand reconciliation would ever catch.
        var payment = PaymentWith(
            new DailyIncomingPaymentLineEntity { InvoiceDocEntry = 101, TransferAmount = 12.50m },
            new DailyIncomingPaymentLineEntity { InvoiceDocEntry = 102, CreditAmount = 40m });

        var request = DailyIncomingPaymentBuilder.BuildRequest(payment, BankRoute);

        Assert.Equal(52.50m, request.TransferSum);
        Assert.Null(request.TransferAccount);
        Assert.Equal("2026-09-13", request.TransferDate);
    }

    [Fact]
    public void The_card_line_route_is_untouched()
    {
        var payment = PaymentWith(new DailyIncomingPaymentLineEntity { InvoiceDocEntry = 101, CreditAmount = 40m });

        var request = DailyIncomingPaymentBuilder.BuildRequest(payment, CardRoute);

        Assert.Equal(40m, request.CreditSum);
        Assert.Equal(0m, request.TransferSum);
        Assert.Null(request.TransferAccount);
        Assert.Equal(7, Assert.Single(request.PaymentCreditCards!).CreditCard);
    }

    private static DailyIncomingPaymentEntity PaymentWith(params DailyIncomingPaymentLineEntity[] lines) =>
        new()
        {
            CardCode = "KEFSHOP-BP",
            PaymentDate = new DateTime(2026, 9, 13),
            Reference = DailyIncomingPaymentBuilder.ReferenceFor(new DateTime(2026, 9, 13), "KEFSHOP-BP"),
            Lines = [.. lines]
        };
}
