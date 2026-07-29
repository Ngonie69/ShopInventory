using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Pods;
using ShopInventory.Data;
using ShopInventory.Services;

namespace ShopInventory.Tests;

public sealed class PodExclusionTests
{
    [Theory]
    [InlineData("Tinashe M - Staff Loan", true)]
    [InlineData("STAFF LOAN ACCOUNT", true)]
    [InlineData("staff loans", true)]
    [InlineData("Staffordshire Loan Co", false)]
    [InlineData("Regular customer", false)]
    [InlineData(null, false)]
    public void Staff_loan_business_partner_names_are_excluded(string? cardName, bool expected) =>
        Assert.Equal(expected, PodExclusions.IsExcludedCardName(cardName));

    [Theory]
    [InlineData("C001", "Regular customer", false)]
    [InlineData("CIS006", "Regular customer", true)]
    [InlineData("C001", "Chipo N - Staff Loan", true)]
    public void Either_an_excluded_card_code_or_an_excluded_name_excludes_the_invoice(
        string cardCode,
        string cardName,
        bool expected) =>
        Assert.Equal(expected, PodExclusions.IsExcluded(cardCode, cardName));

    [Fact]
    public void Exclusion_is_described_by_the_field_that_triggered_it()
    {
        Assert.Equal("CIS006", PodExclusions.DescribeExclusion("CIS006", "Regular customer"));
        Assert.Equal("Chipo N - Staff Loan", PodExclusions.DescribeExclusion("C001", " Chipo N - Staff Loan "));
    }

    /// <summary>
    /// The POD list runs on PostgreSQL, so an untranslatable predicate would only fail at request
    /// time. Rendering the SQL proves both halves of the exclusion survive translation.
    /// </summary>
    [Fact]
    public void Pod_list_exclusion_query_translates_on_postgres()
    {
        using var context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql("Host=localhost;Database=pod-exclusion-translation-check")
                .Options);

        var sql = DocumentService.GetPodExcludedInvoiceDocEntries(context).ToQueryString();

        Assert.Contains("ILIKE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UNION", sql, StringComparison.OrdinalIgnoreCase);
    }
}
