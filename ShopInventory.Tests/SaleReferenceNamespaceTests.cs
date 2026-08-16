using ShopInventory.Common.Sales;

namespace ShopInventory.Tests;

/// <summary>
/// Pins which sale references belong to the system rather than to a caller.
///
/// SAP's <c>U_Van_saleorder</c> UDF is how several routes ask "do you already hold this document?"
/// before posting one, and it is also the local idempotency key on invoice creation. It is settable
/// by any caller of <c>POST /api/Invoice</c>.
///
/// The failure a collision causes is not a duplicate but its quieter opposite: the posting service's
/// pre-post probe finds the caller's unrelated invoice, adopts it, and marks the sale posted against
/// a document that has nothing to do with it. The sale is then never really invoiced, and nothing
/// anywhere looks wrong.
/// </summary>
public sealed class SaleReferenceNamespaceTests
{
    [Theory]
    [InlineData("DS-20260814120000-a1b2c3d4")]
    [InlineData("CONSOL-20260814-KEFSHOP-BP")]
    public void A_reference_the_system_generates_is_reserved(string reference)
    {
        Assert.True(SaleReferenceNamespace.IsReserved(reference));
    }

    [Theory]
    [InlineData("ds-20260814120000-a1b2c3d4")]
    [InlineData("consol-20260814-KEFSHOP-BP")]
    [InlineData("  DS-20260814120000-a1b2c3d4  ")]
    public void Casing_and_padding_do_not_get_a_caller_into_the_namespace(string reference)
    {
        Assert.True(SaleReferenceNamespace.IsReserved(reference));
    }

    [Theory]
    [InlineData("SO-1234")]
    [InlineData("PO-99")]
    [InlineData("MYSYSTEM-DS-1")]     // reserved token, but not at the start
    [InlineData("DSOMETHING")]        // shares letters with the prefix, not the prefix
    [InlineData("CONSOLIDATED-1")]    // ditto: no hyphen, so not CONSOL-
    public void An_ordinary_caller_reference_is_left_alone(string reference)
    {
        // The guard has to be narrow. Refusing references that merely look similar would block
        // callers from using the field for what it is for.
        Assert.False(SaleReferenceNamespace.IsReserved(reference));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_absent_reference_is_not_reserved(string? reference)
    {
        // Leaving it unset is the normal case and must stay allowed; the handler generates its own.
        Assert.False(SaleReferenceNamespace.IsReserved(reference));
    }

    [Fact]
    public void The_reserved_prefixes_are_the_ones_the_system_actually_writes()
    {
        // A guard against a prefix changing on one side only. These two strings are duplicated in
        // CreateDesktopSaleHandler's fallback reference and in ConsolidateDailySalesHandler's key,
        // and nothing else checks that they still agree.
        Assert.Equal("DS-", SaleReferenceNamespace.DesktopSalePrefix);
        Assert.Equal("CONSOL-", SaleReferenceNamespace.ConsolidationPrefix);
        Assert.Equal(2, SaleReferenceNamespace.ReservedPrefixes.Length);
    }
}
