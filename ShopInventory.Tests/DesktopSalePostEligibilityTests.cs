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
        // Still refused — it has no receipt — but no longer a dead end: the sweep retries it and the
        // refusal points the operator at retrying it now, rather than saying it needs a person.
        var refusal = Refusal(
            SaleSourceSystems.ShopTill,
            DesktopSaleConsolidationStatus.Pending,
            DesktopSaleFiscalizationStatus.Failed);

        Assert.Contains("fiscalisation failed", refusal);
        Assert.Contains("Retry fiscalisation", refusal);
    }

    [Fact]
    public void A_sale_that_was_never_offered_fiscalisation_is_refused()
    {
        // Skipped used to post. It is what a sale got when the caller sent `fiscalize: false`, and the
        // argument for letting it through was that it will never become Success, so holding it would
        // strand it — which overlooked what letting it through buys: an A/R invoice in SAP for goods
        // ZIMRA was never told about, on nothing but a flag in a request body. It is refused now, and
        // the flag itself is refused at creation, so no new row can reach this state.
        var refusal = Refusal(
            SaleSourceSystems.ShopTill,
            DesktopSaleConsolidationStatus.Pending,
            DesktopSaleFiscalizationStatus.Skipped);

        Assert.Contains("fiscalisation switched off", refusal);

        // Not a dead end. DesktopSaleFiscalisationRetry now offers a Skipped sale to the device, which
        // is the only thing that can make one postable again — so the refusal says to do that rather
        // than leaving the money with nowhere to go.
        Assert.Contains("Retry fiscalisation", refusal);
        Assert.Null(DesktopSaleFiscalisationRetry.ManualRefusal(
            SaleSourceSystems.ShopTill,
            DesktopSaleFiscalizationStatus.Skipped,
            requiresReconciliation: false,
            createdAtUtc: DateTime.UtcNow.AddDays(-1),
            nowUtc: DateTime.UtcNow,
            usesPlatform: false));
    }

    [Fact]
    public void A_van_sale_that_was_never_stamped_is_refused()
    {
        // This used to be the one place the two routes disagreed: a van sale posted unfiscalised,
        // because the handset had stamped it hours ago and the money was real either way. The half of
        // that which does not hold is "stamped hours ago" — a stamped sale is written Success, so
        // Failed means the handset stamped nothing, and under REVMax no handset can stamp at all. So
        // Failed here means what it means on a till: a receipt the sweep has not signed yet.
        //
        // It matters more on a van, not less. A van sale posts one-to-one, and its invoice is exactly
        // what the SAP-to-FDMS reconciliation goes looking for a receipt against.
        var refusal = Refusal(
            SaleSourceSystems.VanSales,
            DesktopSaleConsolidationStatus.Pending,
            DesktopSaleFiscalizationStatus.Failed);

        Assert.Contains("fiscalisation failed", refusal);
        Assert.Contains("Retry fiscalisation", refusal);
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
