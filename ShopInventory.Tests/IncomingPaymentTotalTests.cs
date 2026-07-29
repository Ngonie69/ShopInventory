using System.Text.Json;
using ShopInventory.Mappings;
using ShopInventory.Models;

namespace ShopInventory.Tests;

/// <summary>
/// Covers what an incoming payment read from SAP is worth.
/// </summary>
/// <remarks>
/// The model used to declare <c>CheckSum</c>, <c>CreditSum</c>, <c>DocTotal</c> and
/// <c>DocTotalFc</c> as bound fields. None exist on <c>SAPB1.Payment</c>, so all four were always
/// zero, and everything that summed them — the account sales and payment report, the DTO mapping,
/// the create notification — dropped cheque and card money on the floor. A payment settled entirely
/// by cheque was worth nothing.
/// </remarks>
public class IncomingPaymentTotalTests
{
    [Fact]
    public void Cheque_only_payment_is_worth_its_cheques()
    {
        // The failing case: no cash, no transfer, so the old code returned zero.
        var payment = new IncomingPayment
        {
            PaymentChecks = [Check(1, 250m), Check(2, 130.50m)]
        };

        Assert.Equal(380.50m, payment.DocTotal);
        Assert.Equal(380.50m, payment.CheckSum);
    }

    [Fact]
    public void Card_only_payment_is_worth_its_cards()
    {
        var payment = new IncomingPayment
        {
            PaymentCreditCards = [Card(1, 99.99m)]
        };

        Assert.Equal(99.99m, payment.DocTotal);
        Assert.Equal(99.99m, payment.CreditSum);
    }

    [Fact]
    public void Mixed_payment_counts_every_means_of_payment()
    {
        // Previously counted the 40 and nothing else.
        var payment = new IncomingPayment
        {
            CashSum = 40m,
            TransferSum = 10m,
            PaymentChecks = [Check(1, 25m)],
            PaymentCreditCards = [Card(1, 25m)]
        };

        Assert.Equal(100m, payment.DocTotal);
    }

    [Fact]
    public void Cash_and_transfer_only_payment_is_unchanged()
    {
        // The case that always worked; it has to keep working.
        var payment = new IncomingPayment { CashSum = 60m, TransferSum = 15m };

        Assert.Equal(75m, payment.DocTotal);
    }

    [Fact]
    public void Payment_with_no_rows_at_all_is_zero_rather_than_a_crash()
    {
        var payment = new IncomingPayment();

        Assert.Equal(0m, payment.DocTotal);
        Assert.Equal(0m, payment.CheckSum);
        Assert.Equal(0m, payment.CreditSum);
    }

    [Fact]
    public void Total_survives_deserialization_of_a_sap_response()
    {
        // End to end from the wire, with the field names SAP actually sends.
        const string sapJson = """
        {
          "DocEntry": 41,
          "DocNum": 771,
          "DocDate": "2026-07-20",
          "DueDate": "2026-08-19",
          "CardCode": "TMP119",
          "DocCurrency": "USD",
          "CashSum": 0.0,
          "TransferSum": 0.0,
          "PaymentChecks": [ { "LineNum": 0, "CheckSum": 500.0 } ],
          "PaymentCreditCards": [ { "LineNum": 0, "CreditSum": 250.0 } ],
          "PaymentInvoices": [ { "LineNum": 0, "DocEntry": 9, "SumApplied": 750.0, "AppliedFC": 700.0 } ]
        }
        """;

        var payment = JsonSerializer.Deserialize<IncomingPayment>(sapJson)!;

        Assert.Equal(750m, payment.DocTotal);
        Assert.Equal("2026-08-19", payment.DueDate);
        Assert.Equal(700m, payment.PaymentInvoices![0].SumAppliedFC);
    }

    [Fact]
    public void Dto_carries_the_composed_total_to_api_consumers()
    {
        // The mapping had the same broken fallback, so the API reported the same wrong figure.
        var payment = new IncomingPayment
        {
            DocCurrency = "USD",
            PaymentChecks = [Check(1, 320m)]
        };

        var dto = payment.ToDto();

        Assert.Equal(320m, dto.DocTotal);
        Assert.Equal(320m, dto.CheckSum);
    }

    private static PaymentCheck Check(int lineNum, decimal sum) =>
        new() { LineNum = lineNum, CheckSum = sum };

    private static PaymentCreditCard Card(int lineNum, decimal sum) =>
        new() { LineNum = lineNum, CreditSum = sum };
}
