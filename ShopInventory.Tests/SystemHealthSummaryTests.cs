using ShopInventory.Web.Common;

namespace ShopInventory.Tests;

/// <summary>
/// The dashboard's reading of GET /api/health.
///
/// The first test is the one that matters: it is the case a live environment
/// caught, where the card printed "Healthy" while SAP was unreachable.
/// </summary>
public sealed class SystemHealthSummaryTests
{
    [Fact]
    public void A_failing_dependency_is_reported_even_when_readiness_passes()
    {
        // The live shape: readiness excludes the SAP check by design, so it
        // says Healthy while the dependency section says otherwise.
        var (value, isHealthy) = SystemHealthSummary.Describe(
            readinessStatus: "Healthy",
            dependenciesStatus: "Unhealthy",
            checkStatuses: ["Healthy", "Healthy", "Unhealthy"]);

        Assert.Equal("Degraded", value);
        Assert.False(isHealthy);
    }

    [Fact]
    public void A_failing_check_alone_is_enough()
    {
        // Belt and braces: a section that forgot to aggregate still cannot
        // hide a check that is down.
        var (value, isHealthy) = SystemHealthSummary.Describe(
            readinessStatus: "Healthy",
            dependenciesStatus: "Healthy",
            checkStatuses: ["Healthy", "Degraded"]);

        Assert.Equal("Degraded", value);
        Assert.False(isHealthy);
    }

    [Fact]
    public void Everything_passing_reads_healthy()
    {
        var (value, isHealthy) = SystemHealthSummary.Describe(
            readinessStatus: "Healthy",
            dependenciesStatus: "Healthy",
            checkStatuses: ["Healthy", "Healthy"]);

        Assert.Equal("Healthy", value);
        Assert.True(isHealthy);
    }

    [Fact]
    public void The_api_failing_readiness_carries_its_own_word()
    {
        var (value, isHealthy) = SystemHealthSummary.Describe(
            readinessStatus: "Unhealthy",
            dependenciesStatus: "Healthy",
            checkStatuses: ["Healthy"]);

        Assert.Equal("Unhealthy", value);
        Assert.False(isHealthy);
    }

    [Fact]
    public void A_missing_dependency_section_is_unknown_rather_than_broken()
    {
        // Reporting trouble nobody has is its own kind of wrong.
        var (value, isHealthy) = SystemHealthSummary.Describe(
            readinessStatus: "Healthy",
            dependenciesStatus: null,
            checkStatuses: []);

        Assert.Equal("Healthy", value);
        Assert.True(isHealthy);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_report_at_all_reads_as_a_dash(string? readiness)
    {
        // The read has not landed. A dash, never a zero or a verdict.
        var (value, isHealthy) = SystemHealthSummary.Describe(readiness, null, []);

        Assert.Equal("—", value);
        Assert.True(isHealthy);
    }

    [Fact]
    public void The_status_word_is_matched_regardless_of_case()
    {
        Assert.True(SystemHealthSummary.IsPassing("healthy"));
        Assert.True(SystemHealthSummary.IsPassing("HEALTHY"));
        Assert.False(SystemHealthSummary.IsPassing("Degraded"));
        Assert.False(SystemHealthSummary.IsPassing(null));
    }
}
