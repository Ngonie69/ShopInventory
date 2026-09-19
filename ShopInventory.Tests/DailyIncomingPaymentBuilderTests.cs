using ShopInventory.Common.Sales;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Pins how a day's till money is split across SAP's payment sums and which G/L accounts it lands in.
/// Getting either wrong is invisible until someone reconciles by hand.
/// </summary>
public sealed class DailyIncomingPaymentBuilderTests
{
    /// <summary>CIS006's accounts, as finance gave them: cash on hand at the factory, and CABS for the rest.</summary>
    private static readonly PaymentAccounts Factory = new(CashAccount: "700300", ElectronicAccount: "701100");

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
        var split = DailyIncomingPaymentBuilder.SplitSale(Sale(TenderTypes.Cash));

        Assert.Equal(new TenderSplit(25m, 0m, 0m), split.Split);
    }

    [Theory]
    [InlineData(TenderTypes.Ecocash)]
    [InlineData(TenderTypes.Innbucks)]
    public void A_wallet_goes_in_the_transfer_sum(string tender)
    {
        var split = DailyIncomingPaymentBuilder.SplitSale(Sale(tender));

        Assert.Equal(new TenderSplit(0m, 25m, 0m), split.Split);
    }

    [Fact]
    public void A_swipe_is_held_as_card_money_on_the_line()
    {
        // It becomes transfer money only on the SAP request, so reports can still tell it from a wallet.
        var split = DailyIncomingPaymentBuilder.SplitSale(Sale(TenderTypes.Swipe, amountPaid: 384.22m));

        Assert.Equal(new TenderSplit(0m, 0m, 384.22m), split.Split);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bitcoin")]
    public void An_unmappable_tender_is_never_guessed_into_cash(string? tender)
    {
        var split = DailyIncomingPaymentBuilder.SplitSale(Sale(tender));

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

        var split = DailyIncomingPaymentBuilder.SplitConsolidation(consolidation, sales);

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

        var split = DailyIncomingPaymentBuilder.SplitConsolidation(consolidation, sales);

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
        var payment = PaymentWith(
            new DailyIncomingPaymentLineEntity { InvoiceDocEntry = 101, CashAmount = 25m },
            new DailyIncomingPaymentLineEntity { InvoiceDocEntry = 102, TransferAmount = 12.50m },
            new DailyIncomingPaymentLineEntity { InvoiceDocEntry = 103, CreditAmount = 40m },
            new DailyIncomingPaymentLineEntity { InvoiceDocEntry = 104, CashAmount = 5m });

        var request = DailyIncomingPaymentBuilder.BuildRequest(payment, Factory);

        Assert.Equal("KEFSHOP-BP", request.CardCode);
        Assert.Equal("2026-09-13", request.DocDate);
        Assert.Equal(30m, request.CashSum);
        // Wallet and swipe money together: both are banked, and there are no card records in SAP.
        Assert.Equal(52.50m, request.TransferSum);
        Assert.Equal(0m, request.CreditSum);
        Assert.True(request.PaymentCreditCards is null or { Count: 0 });
        Assert.Equal("2026-09-13", request.TransferDate);

        Assert.Equal([101, 102, 103, 104], request.PaymentInvoices!.Select(line => line.DocEntry));
        Assert.Equal(request.CashSum + request.TransferSum,
            request.PaymentInvoices!.Sum(line => line.SumApplied));

        // The reference opens the Remarks: it is what a lost reply is resolved by.
        Assert.StartsWith("DAYPAY-20260913-KEFSHOP-BP:", request.Remarks);
    }

    // ---- G/L accounts ----------------------------------------------------------------------------------
    //
    // Left unset, SAP applies its default, 700300, the factory's cash on hand. All eight daily payments
    // posted before the mapping existed went there, Cortina's and Machipisa's included.

    [Fact]
    public void Cash_posts_to_the_partners_cash_account_and_everything_else_to_its_electronic_account()
    {
        var payment = PaymentWith(
            new DailyIncomingPaymentLineEntity { InvoiceDocEntry = 101, CashAmount = 25m },
            new DailyIncomingPaymentLineEntity { InvoiceDocEntry = 102, TransferAmount = 12.50m },
            new DailyIncomingPaymentLineEntity { InvoiceDocEntry = 103, CreditAmount = 40m });

        var request = DailyIncomingPaymentBuilder.BuildRequest(payment, Factory);

        Assert.Equal("700300", request.CashAccount);
        Assert.Equal("701100", request.TransferAccount);
    }

    [Fact]
    public void A_cash_only_day_names_no_transfer_account()
    {
        var payment = PaymentWith(new DailyIncomingPaymentLineEntity { InvoiceDocEntry = 101, CashAmount = 25m });

        var request = DailyIncomingPaymentBuilder.BuildRequest(payment, Factory);

        Assert.Equal("700300", request.CashAccount);
        Assert.Equal(0m, request.TransferSum);
        Assert.Null(request.TransferAccount);
        Assert.Null(request.TransferDate);
    }

    [Fact]
    public void An_electronic_only_day_names_no_cash_account()
    {
        var payment = PaymentWith(new DailyIncomingPaymentLineEntity { InvoiceDocEntry = 101, CreditAmount = 40m });

        var request = DailyIncomingPaymentBuilder.BuildRequest(payment, Factory);

        Assert.Equal(0m, request.CashSum);
        Assert.Null(request.CashAccount);
        Assert.Equal("701100", request.TransferAccount);
    }

    [Theory]
    [InlineData("", "701100")]
    [InlineData("700300", " ")]
    public void A_payment_without_both_accounts_is_never_built(string cash, string electronic)
    {
        var payment = PaymentWith(new DailyIncomingPaymentLineEntity { InvoiceDocEntry = 101, CashAmount = 25m });

        Assert.Throws<InvalidOperationException>(
            () => DailyIncomingPaymentBuilder.BuildRequest(payment, new PaymentAccounts(cash, electronic)));
    }

    [Fact]
    public void The_reference_is_the_payments_SAP_reference_and_journal_remark()
    {
        var payment = PaymentWith(new DailyIncomingPaymentLineEntity { InvoiceDocEntry = 101, CashAmount = 25m });

        var request = DailyIncomingPaymentBuilder.BuildRequest(payment, Factory);

        Assert.Equal("DAYPAY-20260913-KEFSHOP-BP", request.CounterReference);
        Assert.Equal("DAYPAY-20260913-KEFSHOP-BP", request.JournalRemarks);
    }

    // ---- Online van sales ---------------------------------------------------------------------------

    [Theory]
    [InlineData(null, 1)]
    [InlineData("", 1)]
    [InlineData(TenderTypes.Cash, 1)]
    [InlineData(TenderTypes.Ecocash, 2)]
    public void An_online_van_sale_is_claimed_in_its_tender_and_counts_no_tender_as_cash(string? tender, int sum)
    {
        var reservation = new StockReservationEntity { CardCode = "VAN008", TotalValue = 86.96m, PaymentMethod = tender };

        var split = DailyIncomingPaymentBuilder.SplitReservation(reservation).Split!.Value;

        Assert.Equal(sum == 1, split.Cash > 0m);
        Assert.Equal(sum == 2, split.Transfer > 0m);
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
