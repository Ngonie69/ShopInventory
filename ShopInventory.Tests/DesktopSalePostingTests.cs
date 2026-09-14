using ShopInventory.Common.Sales;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Pins how a till sale reaches SAP.
///
/// Two routes now read the same table with the same Pending status: the 18:00 consolidation, which
/// folds a customer's day into one invoice, and the per-sale posting service. Only the source system
/// keeps them apart, and a sale claimed by both is fiscalised once and invoiced twice — recoverable
/// only by a manual credit note. A sale claimed by neither is fiscalised and never invoiced at all,
/// which is quieter and worse.
///
/// The payment is not here: no sale gets its own. See <c>DailyIncomingPaymentBuilderTests</c>.
/// </summary>
public sealed class DesktopSalePostingTests
{
    private static DesktopSaleEntity Sale(
        string? paymentMethod = TenderTypes.Cash,
        string? paymentReference = null,
        decimal amountPaid = 25m) => new()
        {
            Id = 1,
            ExternalReferenceId = "KEFSHOP-01-20260813-000123",
            CardCode = "KEFSHOP-BP",
            DocDate = new DateTime(2026, 8, 13),
            Currency = "USD",
            WarehouseCode = "KEFSHOP",
            CostCentreCode = "CC-01",
            PaymentMethod = paymentMethod,
            PaymentReference = paymentReference,
            AmountPaid = amountPaid,
        };

    // ---- Which route claims a sale ---------------------------------------------------------------

    [Theory]
    [InlineData(SaleSourceSystems.ShopTill)]
    [InlineData(SaleSourceSystems.Vending)]
    [InlineData(SaleSourceSystems.VanSales)]
    public void A_sale_that_posts_one_invoice_each_is_kept_out_of_consolidation(string source)
    {
        Assert.Contains(source, SaleSourceSystems.PostedPerSale);
    }

    [Theory]
    [InlineData(SaleSourceSystems.ShopTill)]
    [InlineData(SaleSourceSystems.Vending)]
    public void The_till_route_claims_shop_and_vending_sales(string source)
    {
        Assert.Contains(source, SaleSourceSystems.PostedByDesktopSaleJob);
    }

    [Fact]
    public void The_till_route_never_claims_a_van_sale()
    {
        // Van sales have their own posting service. Both claiming one would post it twice.
        Assert.DoesNotContain(SaleSourceSystems.VanSales, SaleSourceSystems.PostedByDesktopSaleJob);
    }

    [Fact]
    public void Legacy_desktop_sales_still_belong_to_consolidation()
    {
        // Rows written before this route existed say DESKTOP_APP. They must keep consolidating: the
        // per-sale job has never seen them, and moving them across would hand it sales the 18:00 run
        // may already have posted.
        Assert.DoesNotContain(SaleSourceSystems.LegacyDesktop, SaleSourceSystems.PostedPerSale);
        Assert.Equal(
            SaleSourceSystems.LegacyDesktop,
            SaleSourceSystems.NormalizeTillSource(SaleSourceSystems.LegacyDesktop));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_caller_that_declares_no_source_keeps_consolidating(string? supplied)
    {
        // THE regression guard. Defaulting a silent caller to ShopTill would move every existing
        // client onto the per-sale route at once — and since that route's sources are excluded from
        // the 18:00 consolidation, any of them the route did not pick up would be fiscalised, refused
        // by consolidation, claimed by nobody, and never invoiced, with nothing logged. A till opts in
        // by naming itself.
        Assert.Equal(SaleSourceSystems.LegacyDesktop, SaleSourceSystems.NormalizeTillSource(supplied));
        Assert.DoesNotContain(
            SaleSourceSystems.NormalizeTillSource(supplied), SaleSourceSystems.PostedByDesktopSaleJob);
    }

    [Theory]
    [InlineData("kefalosshoptill", SaleSourceSystems.ShopTill)]
    [InlineData("  KefalosVending  ", SaleSourceSystems.Vending)]
    [InlineData("KEFALOSSHOPTILL", SaleSourceSystems.ShopTill)]
    public void A_tills_declared_source_is_canonicalised(string supplied, string expected)
    {
        // The routing test is an equality check against these constants, so a till that spelled its
        // source differently would be picked up by neither route and never reach SAP.
        Assert.Equal(expected, SaleSourceSystems.NormalizeTillSource(supplied));
    }

    [Fact]
    public void An_unrecognised_source_is_left_alone()
    {
        // Not reclassified, so it keeps whatever routing it had.
        Assert.Equal("POS_TERMINAL_1", SaleSourceSystems.NormalizeTillSource("POS_TERMINAL_1"));
        Assert.DoesNotContain("POS_TERMINAL_1", SaleSourceSystems.PostedPerSale);
    }

    // ---- The invoice ------------------------------------------------------------------------------

    [Fact]
    public void The_invoice_is_keyed_on_the_sales_own_reference()
    {
        // The key has to be derivable without anything the post returns — that is what lets a lost
        // reply be recovered by asking SAP rather than guessed at.
        var request = DesktopSaleInvoiceRequestBuilder.Build(Sale());

        Assert.Equal("KEFSHOP-01-20260813-000123", request.NumAtCard);
        Assert.Equal("KEFSHOP-01-20260813-000123", request.U_Van_saleorder);
        Assert.Equal("KEFSHOP-01-20260813-000123", request.ClientRequestId);
    }

    [Fact]
    public void A_line_with_no_warehouse_falls_back_to_the_sales_own()
    {
        var sale = Sale();
        sale.Lines =
        [
            new DesktopSaleLineEntity { LineNum = 1, ItemCode = "A", Quantity = 1, UnitPrice = 5m, WarehouseCode = "" },
            new DesktopSaleLineEntity { LineNum = 2, ItemCode = "B", Quantity = 1, UnitPrice = 5m, WarehouseCode = "KEFSHOP" },
        ];

        var request = DesktopSaleInvoiceRequestBuilder.Build(sale);

        var lines = Assert.IsAssignableFrom<IReadOnlyList<CreateInvoiceLineRequest>>(request.Lines);
        Assert.All(lines, l => Assert.Equal("KEFSHOP", l.WarehouseCode));
        // The cost centre comes from the header: the line's own is not mapped, so a re-read sale
        // always has it null there.
        Assert.All(lines, l => Assert.Equal("CC-01", l.CostCentreCode));
    }
}
