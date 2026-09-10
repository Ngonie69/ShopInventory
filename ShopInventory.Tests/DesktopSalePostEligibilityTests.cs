using ShopInventory.Common.Sales;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Pins which sales the console offers a "Post to SAP" button for.
///
/// This rule has two readers that must never disagree: the sales list, which decides whether a row
/// gets a checkbox, and the posting command, which decides whether to refuse. Written twice they
/// drift, and the direction they drift in is a button that posts a sale the route was deliberately
/// keeping out of SAP — for which the only remedy is a manual credit note.
///
/// It is also not a rule of its own invention. It mirrors what each background pass already selects,
/// so a manual post cannot refuse what the pass would post (which would make the button useless in
/// the only case it exists for) or post what the pass refuses (which would make it a way around a
/// safety rule).
/// </summary>
public sealed class DesktopSalePostEligibilityTests
{
    [Theory]
    [InlineData(SaleSourceSystems.ShopTill)]
    [InlineData(SaleSourceSystems.Vending)]
    [InlineData(SaleSourceSystems.VanSales)]
    public void A_fiscalised_sale_on_a_per_sale_route_may_be_posted(string source)
    {
        Assert.Null(Refusal(source, DesktopSaleConsolidationStatus.Pending, DesktopSaleFiscalizationStatus.Success));
    }

    [Fact]
    public void A_sale_whose_last_post_failed_may_be_posted_again()
    {
        // Failed is the state a sale is parked in once the automatic pass has spent its attempts, so
        // it is the single most important row on this page to be able to press a button on.
        Assert.Null(Refusal(
            SaleSourceSystems.ShopTill,
            DesktopSaleConsolidationStatus.Failed,
            DesktopSaleFiscalizationStatus.Success));
    }

    [Fact]
    public void A_sale_already_in_sap_is_refused()
    {
        Assert.Contains("already in SAP", Refusal(
            SaleSourceSystems.ShopTill,
            DesktopSaleConsolidationStatus.Consolidated,
            DesktopSaleFiscalizationStatus.Success));
    }

    [Fact]
    public void An_excluded_sale_is_refused()
    {
        // Excluded is somebody's decision to keep this sale out of SAP. A button that quietly
        // overrode it would make the exclusion meaningless.
        Assert.Contains("excluded", Refusal(
            SaleSourceSystems.ShopTill,
            DesktopSaleConsolidationStatus.Excluded,
            DesktopSaleFiscalizationStatus.Success));
    }

    [Theory]
    [InlineData(SaleSourceSystems.ShopTill)]
    [InlineData(SaleSourceSystems.Vending)]
    public void A_till_sale_that_has_not_been_fiscalised_is_refused(string source)
    {
        // A till fiscalises on this side, so a Pending sale may still get its receipt. Invoicing it
        // now would put a document in SAP for a sale ZIMRA has no receipt for.
        Assert.Contains("not been fiscalised", Refusal(
            source, DesktopSaleConsolidationStatus.Pending, DesktopSaleFiscalizationStatus.Pending));
    }

    [Fact]
    public void A_till_sale_whose_fiscalisation_failed_is_refused()
    {
        Assert.Contains("needs a person", Refusal(
            SaleSourceSystems.ShopTill,
            DesktopSaleConsolidationStatus.Pending,
            DesktopSaleFiscalizationStatus.Failed));
    }

    [Fact]
    public void A_till_sale_that_was_never_offered_fiscalisation_may_still_be_posted()
    {
        // Skipped is not "failed to fiscalise" — it is what a sale gets when fiscalisation was not
        // asked for or is switched off. It will never become Success, and it must still reach SAP,
        // exactly as the background pass has always sent it.
        Assert.Null(Refusal(
            SaleSourceSystems.ShopTill,
            DesktopSaleConsolidationStatus.Pending,
            DesktopSaleFiscalizationStatus.Skipped));
    }

    [Fact]
    public void A_van_sale_that_was_never_stamped_may_still_be_posted()
    {
        // The one place the two routes genuinely disagree, and both are right. On a van sale
        // `Failed` means the upload carried no usable signature; the van pass posts it anyway,
        // because the money is real and the fiscal side is chased separately through
        // ReceiptIngestStatus. Refusing here would strand takings the pass posts every night.
        Assert.Null(Refusal(
            SaleSourceSystems.VanSales,
            DesktopSaleConsolidationStatus.Pending,
            DesktopSaleFiscalizationStatus.Failed));
    }

    [Fact]
    public void An_online_van_sale_receipt_carrier_is_refused()
    {
        // This row is not a sale awaiting SAP. It carries the receipt a handset signed for a sale
        // that reached SAP through its reservation, and posting it would invoice that sale twice.
        Assert.Contains("nothing to post", Refusal(
            SaleSourceSystems.VanSalesOnline,
            DesktopSaleConsolidationStatus.Pending,
            DesktopSaleFiscalizationStatus.Success));
    }

    [Theory]
    [InlineData(SaleSourceSystems.LegacyDesktop)]
    [InlineData(null)]
    [InlineData("SomethingNobodyHasBuiltYet")]
    public void A_sale_that_reaches_sap_by_consolidation_is_refused(string? source)
    {
        // Including the unrecognised source, deliberately. A source with no per-sale posting route
        // must default to "not yours to post" rather than to a button that calls a service which
        // will refuse the sale anyway.
        Assert.Contains("end-of-day consolidation", Refusal(
            source, DesktopSaleConsolidationStatus.Pending, DesktopSaleFiscalizationStatus.Success));
    }

    [Fact]
    public void The_entity_overload_answers_the_same_as_the_loose_one()
    {
        // Two entry points, one rule. The list projects columns and the command reads an entity, and
        // a divergence between them is the drift this whole class exists to prevent.
        var sale = new DesktopSaleEntity
        {
            ExternalReferenceId = "KEFSHOP-01-20260910-000001",
            SourceSystem = SaleSourceSystems.ShopTill,
            ConsolidationStatus = DesktopSaleConsolidationStatus.Pending,
            FiscalizationStatus = DesktopSaleFiscalizationStatus.Pending
        };

        Assert.Equal(
            DesktopSalePostEligibility.Refusal(
                sale.SourceSystem, sale.ConsolidationStatus, sale.FiscalizationStatus),
            DesktopSalePostEligibility.Refusal(sale));

        Assert.False(DesktopSalePostEligibility.CanPost(sale));
    }

    private static string Refusal(
        string? source,
        DesktopSaleConsolidationStatus consolidation,
        DesktopSaleFiscalizationStatus fiscalisation)
        => DesktopSalePostEligibility.Refusal(source, consolidation, fiscalisation)!;
}
