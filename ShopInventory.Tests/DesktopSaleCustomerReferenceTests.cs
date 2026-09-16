using ShopInventory.Common.Sales;
using ShopInventory.Models.Entities;
using Xunit;

namespace ShopInventory.Tests;

/// <summary>
/// What a system-posted sale's invoice carries in SAP's <c>NumAtCard</c>.
/// </summary>
/// <remarks>
/// Every invoice on these routes used to carry the sale's own machine reference there, which meant the
/// one column SAP shows a customer reference in said nothing a person could use: <c>CardCode</c> names
/// the van or the depot, so "who bought" was unanswerable from the document. These pin the rule that
/// replaced it, and pin that a shop till was deliberately left alone.
/// </remarks>
public sealed class DesktopSaleCustomerReferenceTests
{
    private const string Reference = "GRC-FAC-20260910-0A519807CF88";

    private static DesktopSaleEntity Sale(
        string source,
        string? routeCustomerCode = null,
        string? routeCustomerName = null,
        string? cardName = null) => new()
        {
            ExternalReferenceId = Reference,
            SourceSystem = source,
            CardCode = "COR007",
            CardName = cardName,
            RouteCustomerCode = routeCustomerCode,
            RouteCustomerName = routeCustomerName,
            WarehouseCode = "VAN006"
        };

    // ---------------------------------------------------------------
    // The rule
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(SaleSourceSystems.VanSales)]
    [InlineData(SaleSourceSystems.VanSalesOnline)]
    public void A_van_sale_names_the_shop_that_bought(string source)
    {
        var reference = DesktopSaleCustomerReference.For(
            Sale(source, routeCustomerCode: "RC014", routeCustomerName: "Mereki Store"));

        // The name, not the code. A route's customers are shops known by name; the code is internal.
        Assert.Equal("Mereki Store", reference);
    }

    [Fact]
    public void A_vending_sale_names_the_vendor_code()
    {
        var reference = DesktopSaleCustomerReference.For(
            Sale(SaleSourceSystems.Vending, routeCustomerCode: "VMB001", routeCustomerName: "Tarisai"));

        // The code, not the name, and that is not an inconsistency with the van rule above: a vending
        // vendor is its code. It is issued under VendorCodeConvention, unique company-wide, and printed
        // on the cart.
        Assert.Equal("VMB001", reference);
    }

    [Fact]
    public void A_till_sale_keeps_its_own_reference()
    {
        // A till sells over a counter to whoever is standing there. There is no counterparty to name,
        // and the reference is what support searches SAP for — changing it would cost and buy nothing.
        Assert.Equal(Reference, DesktopSaleCustomerReference.For(Sale(SaleSourceSystems.ShopTill)));
    }

    [Fact]
    public void An_unrecognised_source_keeps_its_own_reference()
    {
        Assert.Equal(Reference, DesktopSaleCustomerReference.For(Sale(SaleSourceSystems.LegacyDesktop)));
    }

    // ---------------------------------------------------------------
    // The fallbacks
    // ---------------------------------------------------------------

    [Fact]
    public void A_van_sale_with_no_route_customer_falls_back_to_the_account_name()
    {
        // Not every van sells to a route customer: some sell to real SAP business partners, and those
        // leave RouteCustomer* unset.
        Assert.Equal(
            "Chicken Inn Avondale",
            DesktopSaleCustomerReference.For(
                Sale(SaleSourceSystems.VanSales, cardName: "Chicken Inn Avondale")));
    }

    [Fact]
    public void A_van_sale_with_only_a_code_uses_the_code()
    {
        Assert.Equal(
            "RC014",
            DesktopSaleCustomerReference.For(Sale(SaleSourceSystems.VanSales, routeCustomerCode: "RC014")));
    }

    [Theory]
    [InlineData(SaleSourceSystems.VanSales)]
    [InlineData(SaleSourceSystems.Vending)]
    public void A_sale_that_names_nobody_falls_back_to_the_reference(string source)
    {
        // Never empty: CreateInvoiceRequest.NumAtCard is required, and an empty one refuses the post —
        // which on this route would strand a fiscalised sale that can never be invoiced.
        Assert.Equal(Reference, DesktopSaleCustomerReference.For(Sale(source)));
    }

    [Fact]
    public void A_blank_name_is_not_a_name()
    {
        var sale = Sale(SaleSourceSystems.VanSales, routeCustomerCode: "RC014", routeCustomerName: "   ");

        Assert.Equal("RC014", DesktopSaleCustomerReference.For(sale));
    }

    // ---------------------------------------------------------------
    // What SAP will actually take
    // ---------------------------------------------------------------

    [Fact]
    public void A_name_longer_than_the_column_is_cut_to_fit()
    {
        var sale = Sale(SaleSourceSystems.VanSales, routeCustomerName: new string('A', 300));

        var reference = DesktopSaleCustomerReference.For(sale);

        // SAP refuses the whole document over an oversized field, which here would mean a fiscalised
        // sale that can never be invoiced.
        Assert.Equal(DesktopSaleCustomerReference.MaxLength, reference.Length);
    }

    [Fact]
    public void Whitespace_a_person_typed_is_collapsed()
    {
        // A customer name is typed by a person and arrives with double spaces and the occasional
        // newline. Both reach an OData string literal on the way out and a SAP list column on the way
        // in, and neither is improved by them.
        var sale = Sale(SaleSourceSystems.VanSales, routeCustomerName: "  Mereki \n  Store  ");

        Assert.Equal("Mereki Store", DesktopSaleCustomerReference.For(sale));
    }

    // ---------------------------------------------------------------
    // The builder actually sends it
    // ---------------------------------------------------------------

    [Fact]
    public void The_invoice_request_carries_it_and_still_carries_the_sale_reference()
    {
        var request = DesktopSaleInvoiceRequestBuilder.Build(
            Sale(SaleSourceSystems.VanSales, routeCustomerCode: "RC014", routeCustomerName: "Mereki Store"));

        Assert.Equal("Mereki Store", request.NumAtCard);

        // Changing NumAtCard is only safe because nothing keys on it. The duplicate guard is
        // U_Van_saleorder, with the mobile invoice-number UDF beside it, and both must still hold the
        // sale's own reference or a retry after a lost reply posts a second invoice.
        Assert.Equal(Reference, request.U_Van_saleorder);
        Assert.Equal(Reference, request.MobileInvoiceNumber);
        Assert.Equal(Reference, request.ClientRequestId);
    }
}
