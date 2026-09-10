using ShopInventory.Common.Idempotency;
using ShopInventory.Features.DesktopIntegration.Commands.CreateDesktopSale;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Pins the permanent half of the duplicate guard on a desktop sale's external reference.
/// </summary>
/// <remarks>
/// There are two guards on that key and they used to disagree about what it meant.
/// <c>IdempotencyRequestStore</c> compares the payload and refuses a key re-used for something else —
/// but its record expires after an hour, while the sale row it guards never does. So a reference
/// re-used within the hour was correctly refused, and the same reference re-used the next morning
/// fell through to a lookup that compared nothing and answered with the previous day's invoice.
///
/// <para>
/// That happened on 10 September 2026. A till crashed mid-print on the 9th without clearing its
/// pending reference; the next morning's basket picked it up, and this endpoint answered
/// <c>201 Created</c> with a sale for different goods at a different price made a day earlier. The
/// till printed a receipt for it, banked the takings against it and deducted stock the document
/// never contained.
/// </para>
///
/// <para>
/// Both requests below are the ones actually sent, from
/// <c>Logs\Orders\INV-KEF-FAC-20260909-57A824DD8D03.log</c> on the till.
/// </para>
/// </remarks>
public class DesktopSaleReferenceReuseTests
{
    private const string Reference = "KEF-FAC-20260909-57A824DD8D03";

    /// <summary>The sale actually made on 9 September: one bubblegum, $6.06 net, $7.00 tendered.</summary>
    private static CreateDesktopSaleRequest NinthSeptember() => new()
    {
        ExternalReferenceId = Reference,
        SourceSystem = "KefalosShopTill",
        DocCurrency = "USD",
        DocDate = "2026-09-09",
        NumAtCard = "Customer 2",
        PaymentMethod = "Cash",
        AmountPaid = 7.00m,
        Fiscalize = true,
        Lines =
        [
            new CreateDesktopSaleLineRequest
            {
                LineNum = 0,
                ItemCode = "ICS026",
                ItemDescription = "5 Litre Shamiso Bubblegum",
                Quantity = 1m,
                UnitPrice = 6.06m,
            },
        ],
    };

    /// <summary>The basket rung up on 10 September: one blueberry, $6.25 net, $8.00 tendered.</summary>
    private static CreateDesktopSaleRequest TenthSeptember() => new()
    {
        ExternalReferenceId = Reference,
        SourceSystem = "KefalosShopTill",
        DocCurrency = "USD",
        DocDate = "2026-09-10",
        NumAtCard = "Customer 2",
        PaymentMethod = "Cash",
        AmountPaid = 8.00m,
        Fiscalize = true,
        Lines =
        [
            new CreateDesktopSaleLineRequest
            {
                LineNum = 0,
                ItemCode = "ICS025",
                ItemDescription = "5 Litre Shamiso Blueberry",
                Quantity = 1m,
                UnitPrice = 6.25m,
            },
        ],
    };

    private static DesktopSaleEntity SaleMadeBy(CreateDesktopSaleRequest request) => new()
    {
        Id = 1,
        ExternalReferenceId = Reference,
        RequestHash = IdempotencyRequestHash.Of(request),
        CreatedAt = new DateTime(2026, 09, 09, 10, 19, 23, DateTimeKind.Utc),
    };

    // ---- The permanent guard --------------------------------------------------------------------

    [Fact]
    public void The_tenth_of_September_basket_is_refused_the_ninths_invoice()
    {
        // The whole incident in one assertion. Same reference, different goods: the reference is
        // spent, and the caller must be told so rather than handed somebody else's sale.
        var verdict = CreateDesktopSaleHandler.VerifyReplay(
            SaleMadeBy(NinthSeptember()),
            IdempotencyRequestHash.Of(TenthSeptember()));

        Assert.Equal(CreateDesktopSaleHandler.ReplayVerdict.Refuse, verdict);
    }

    [Fact]
    public void A_genuine_retry_of_the_same_basket_is_still_replayed()
    {
        // The property the guard must not break. A till whose post timed out resubmits the same
        // reference on purpose, and it has to be answered with the sale rather than refused —
        // otherwise the basket is sold twice, which is the failure this key exists to prevent.
        var verdict = CreateDesktopSaleHandler.VerifyReplay(
            SaleMadeBy(NinthSeptember()),
            IdempotencyRequestHash.Of(NinthSeptember()));

        Assert.Equal(CreateDesktopSaleHandler.ReplayVerdict.Replay, verdict);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_sale_from_before_the_column_existed_is_replayed_rather_than_refused(string? storedHash)
    {
        // Refusing here would turn every legitimate retry of a pre-migration sale into a supervisor
        // call, to close a hole that closes itself as those rows age out. The handler logs it.
        var sale = SaleMadeBy(NinthSeptember());
        sale.RequestHash = storedHash;

        var verdict = CreateDesktopSaleHandler.VerifyReplay(
            sale, IdempotencyRequestHash.Of(TenthSeptember()));

        Assert.Equal(CreateDesktopSaleHandler.ReplayVerdict.Unverifiable, verdict);
    }

    // ---- The fingerprint the two guards share -----------------------------------------------------

    [Fact]
    public void The_two_requests_from_the_incident_do_not_share_a_fingerprint()
    {
        Assert.NotEqual(
            IdempotencyRequestHash.Of(NinthSeptember()),
            IdempotencyRequestHash.Of(TenthSeptember()));
    }

    [Fact]
    public void The_same_request_fingerprints_the_same_way_twice()
    {
        // Otherwise a retry would never match and every timed-out sale would be refused.
        Assert.Equal(
            IdempotencyRequestHash.Of(NinthSeptember()),
            IdempotencyRequestHash.Of(NinthSeptember()));
    }

    [Fact]
    public void A_changed_quantity_changes_the_fingerprint()
    {
        var doubled = NinthSeptember();
        doubled.Lines[0].Quantity = 2m;

        Assert.NotEqual(
            IdempotencyRequestHash.Of(NinthSeptember()),
            IdempotencyRequestHash.Of(doubled));
    }

    [Fact]
    public void A_changed_price_changes_the_fingerprint()
    {
        var repriced = NinthSeptember();
        repriced.Lines[0].UnitPrice = 6.07m;

        Assert.NotEqual(
            IdempotencyRequestHash.Of(NinthSeptember()),
            IdempotencyRequestHash.Of(repriced));
    }

    [Fact]
    public void A_changed_tender_changes_the_fingerprint()
    {
        // Same goods, different money. The sale that gets written differs, so the request does too.
        var tendered = NinthSeptember();
        tendered.AmountPaid = 10.00m;

        Assert.NotEqual(
            IdempotencyRequestHash.Of(NinthSeptember()),
            IdempotencyRequestHash.Of(tendered));
    }
}
