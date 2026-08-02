using ShopInventory.Web.Common;

namespace ShopInventory.Tests;

/// <summary>
/// A user's assigned section is stored as the short option ("Cheeseman"), while
/// an invoice's generated location carries the full name SAP records ("Cheeseman
/// DC Harare"). The POD dashboard scopes an operator's compliance figures by
/// comparing the two, so a mismatch here shows up as a section reporting no
/// outstanding deliveries when it has plenty.
/// </summary>
public sealed class AssignedSectionMatchingTests
{
    [Theory]
    [InlineData("Cheeseman", "Cheeseman")]
    [InlineData("Cheeseman DC Harare", "Cheeseman")]
    [InlineData("  cheeseman dc harare  ", "Cheeseman")]
    [InlineData("Factory", "Factory")]
    [InlineData("Factory-Dispatch", "Factory")]
    [InlineData("Factory - Dispatch", "Factory")]
    [InlineData("Cheeseman DC Byo", "Bulawayo")]
    [InlineData("Graniteside", "Graniteside")]
    [InlineData("Cheeseman DC Vic Falls", "Cheeseman DC Vic Falls")]
    public void Every_spelling_of_a_section_reduces_to_its_option(string value, string expected) =>
        Assert.Equal(expected, AssignedSectionOptions.Normalize(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_section_normalises_to_nothing(string? value) =>
        Assert.Equal(string.Empty, AssignedSectionOptions.Normalize(value));

    /// <summary>
    /// A location nobody has mapped is left as written rather than folded into
    /// one of the known sections, so it still compares equal to itself.
    /// </summary>
    [Fact]
    public void An_unmapped_location_is_kept_as_given()
    {
        Assert.Equal("Mutare Depot", AssignedSectionOptions.Normalize("  Mutare Depot "));
        Assert.True(AssignedSectionOptions.AreSameSection("Mutare Depot", "mutare depot"));
        Assert.False(AssignedSectionOptions.AreSameSection("Mutare Depot", "Graniteside"));
    }

    [Theory]
    [InlineData("Cheeseman", "Cheeseman DC Harare", true)]
    [InlineData("Factory", "Factory-Dispatch", true)]
    [InlineData("Bulawayo", "Cheeseman DC Byo", true)]
    [InlineData("Cheeseman", "Cheeseman DC Richwell", false)]
    [InlineData("Factory", "Graniteside", false)]
    public void Two_values_name_the_same_place_or_they_do_not(string left, string right, bool expected) =>
        Assert.Equal(expected, AssignedSectionOptions.AreSameSection(left, right));

    /// <summary>
    /// An invoice with no attributed location matches no section — it is counted
    /// as unattributed on the dashboard rather than falling into whichever
    /// section is being read.
    /// </summary>
    [Theory]
    [InlineData(null, "Cheeseman")]
    [InlineData("", "Cheeseman")]
    [InlineData("Cheeseman", null)]
    public void An_absent_value_never_matches(string? left, string? right) =>
        Assert.False(AssignedSectionOptions.AreSameSection(left, right));

    /// <summary>
    /// Richwell and Vic Falls are distinct sections whose names both begin
    /// "Cheeseman DC" — they must not collapse into the Harare depot.
    /// </summary>
    [Fact]
    public void The_outlying_cheeseman_depots_stay_separate()
    {
        Assert.False(AssignedSectionOptions.AreSameSection("Cheeseman DC Richwell", "Cheeseman"));
        Assert.False(AssignedSectionOptions.AreSameSection("Cheeseman DC Vic Falls", "Cheeseman DC Richwell"));
    }
}
