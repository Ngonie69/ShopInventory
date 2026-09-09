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
    [InlineData("WEB-3f2b1c9d4e5a6b7c8d9e0f1a2b3c4d5e")]
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
        Assert.Equal("WEB-", SaleReferenceNamespace.WebInvoicePrefix);
        Assert.Equal(3, SaleReferenceNamespace.ReservedPrefixes.Length);
    }

    /// <summary>
    /// The property the whole derivation exists for. A retry has to look SAP up under the same
    /// reference the lost attempt posted under, or the probe searches for something that was never
    /// written and the retry posts a second invoice.
    /// </summary>
    [Fact]
    public void The_same_idempotency_key_always_derives_the_same_reference()
    {
        Assert.Equal(
            SaleReferenceNamespace.ForClientRequest("8d1f4c2ba9034e6f9b7a5c3e1d0f8a26"),
            SaleReferenceNamespace.ForClientRequest("8d1f4c2ba9034e6f9b7a5c3e1d0f8a26"));

        Assert.NotEqual(
            SaleReferenceNamespace.ForClientRequest("8d1f4c2ba9034e6f9b7a5c3e1d0f8a26"),
            SaleReferenceNamespace.ForClientRequest("8d1f4c2ba9034e6f9b7a5c3e1d0f8a27"));
    }

    [Fact]
    public void A_derived_reference_is_reserved_so_a_caller_cannot_hand_write_one()
    {
        // Otherwise a caller could name the reference another request's key derives, and the two
        // would adopt each other's invoices.
        Assert.True(SaleReferenceNamespace.IsReserved(
            SaleReferenceNamespace.ForClientRequest("cc9c0e5f6a2b4d8e")));
    }

    [Theory]
    [InlineData("8d1f4c2ba9034e6f9b7a5c3e1d0f8a26")]        // a GUID, the shape every client sends
    [InlineData("order_449-2")]
    public void A_plain_key_is_kept_verbatim_so_SAP_can_be_searched_for_it(string key)
    {
        Assert.Equal("WEB-" + key, SaleReferenceNamespace.ForClientRequest(key));
    }

    [Theory]
    [InlineData("it's a key")]                                // would be refused by SanitizeODataValue
    [InlineData("key;drop")]
    [InlineData("key--comment")]
    [InlineData("key/*comment")]
    [InlineData("a-very-long-key-that-runs-well-past-what-the-UDF-should-be-asked-to-hold")]
    public void A_key_that_is_not_safe_for_SAP_is_fingerprinted_instead(string key)
    {
        var reference = SaleReferenceNamespace.ForClientRequest(key);

        Assert.Equal(36, reference.Length);
        Assert.StartsWith("WEB-", reference, StringComparison.Ordinal);
        Assert.True(reference[4..].All(character => char.IsAsciiLetterOrDigit(character)));
    }
}
