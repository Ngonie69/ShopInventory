using ShopInventory.Features.CreditNoteApprovals;
using ShopInventory.Models;
using Xunit;

namespace ShopInventory.Tests;

/// <summary>
/// The "Raised by …" note an added credit memo's Remarks carry: the reason stays first, the name always
/// fits, and a second add of the same memo adds nothing.
/// </summary>
public sealed class CreditNoteRaisedByRemarkTests
{
    [Theory]
    [InlineData("Jane Moyo", "jmoyo", "Raised by Jane Moyo (jmoyo)")]
    [InlineData("Jane Moyo", null, "Raised by Jane Moyo")]
    [InlineData(null, "jmoyo", "Raised by jmoyo")]
    [InlineData("washbay", "WASHBAY", "Raised by washbay")]
    public void Names_the_user_by_name_and_code(string? name, string? code, string expected) =>
        Assert.Equal(expected, CreditNoteRaisedByRemark.Describe(new SAPUser { InternalKey = 31, UserName = name, UserCode = code }, 31));

    [Fact]
    public void A_user_sap_cannot_name_is_named_by_key_and_nobody_is_nothing()
    {
        Assert.Equal("Raised by SAP user 31", CreditNoteRaisedByRemark.Describe(null, 31));
        Assert.Null(CreditNoteRaisedByRemark.Describe(null, null));
    }

    [Theory]
    [InlineData(null, "Raised by Jane Moyo")]
    [InlineData("  ", "Raised by Jane Moyo")]
    [InlineData("Crates returned", "Crates returned | Raised by Jane Moyo")]
    public void Follows_the_reason(string? comments, string expected) =>
        Assert.Equal(expected, CreditNoteRaisedByRemark.Append(comments, "Raised by Jane Moyo"));

    [Fact]
    public void Remarks_that_already_name_the_raiser_are_left_alone() =>
        Assert.Null(CreditNoteRaisedByRemark.Append("Crates returned | Raised by Jane Moyo", "Raised by Jane Moyo"));

    /// <summary>ORIN.Comments holds 254 characters: the reason gives way, never the name.</summary>
    [Fact]
    public void A_long_reason_is_shortened_to_keep_the_name()
    {
        var reason = new string('x', 250);

        var remarks = CreditNoteRaisedByRemark.Append(reason, "Raised by Jane Moyo (jmoyo)")!;

        Assert.Equal(CreditNoteRaisedByRemark.MaxLength, remarks.Length);
        Assert.EndsWith("… | Raised by Jane Moyo (jmoyo)", remarks);
        Assert.StartsWith("xxxx", remarks);
    }
}
