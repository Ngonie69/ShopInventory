using System.Security.Claims;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the warehouse a new invoice or sales order opens on.
/// </summary>
/// <remarks>
/// The two screens used to disagree. Invoices took the warehouse configured in Settings and ignored
/// the operator's assignment; sales orders read the first "warehouse" claim and ignored the setting.
/// Both then fell back to a hardcoded "01". Reading the first claim was wrong on its own, because
/// CustomAuthStateProvider emits one claim per assigned warehouse, so a multi-warehouse operator got
/// an arbitrary one of them.
///
/// The rule these tests pin down: the configured default when the operator is entitled to it, then
/// their own first assignment, then nothing — and never a code the warehouse cache does not hold,
/// since a dropdown that cannot show the value would read "Select…" while the lines underneath
/// quietly carried it.
/// </remarks>
public sealed class DefaultWarehouseResolverTests
{
    private const string Configured = "KEFSHOP";

    private static ClaimsPrincipal Operator(params string[] assignedWarehouses)
    {
        var claims = assignedWarehouses.Select(code => new Claim("warehouse", code));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "jwt"));
    }

    private static IReadOnlyCollection<WarehouseDto> Cache(params string[] warehouseCodes) =>
        warehouseCodes.Select(code => new WarehouseDto { WarehouseCode = code }).ToList();

    // ── An operator with no assignment is unrestricted, not unassigned ──────────────────────

    [Fact]
    public void Unrestricted_operator_gets_the_configured_default()
    {
        var resolved = DefaultWarehouseResolver.Resolve(
            Operator(), Configured, Cache("01", Configured));

        Assert.Equal(Configured, resolved);
    }

    [Fact]
    public void Anonymous_principal_is_treated_as_unrestricted()
    {
        // The invoice screen resolves with a null user when fetching auth state threw.
        var resolved = DefaultWarehouseResolver.Resolve(
            user: null, Configured, Cache(Configured));

        Assert.Equal(Configured, resolved);
    }

    // ── The configured default only applies to operators entitled to it ─────────────────────

    [Fact]
    public void Assigned_operator_gets_the_configured_default_when_it_is_one_of_theirs()
    {
        var resolved = DefaultWarehouseResolver.Resolve(
            Operator("DEPOT2", Configured), Configured, Cache("DEPOT2", Configured));

        Assert.Equal(Configured, resolved);
    }

    [Fact]
    public void Assigned_operator_falls_to_their_own_warehouse_when_the_default_is_not_theirs()
    {
        var resolved = DefaultWarehouseResolver.Resolve(
            Operator("DEPOT2"), Configured, Cache("DEPOT2", Configured));

        Assert.Equal("DEPOT2", resolved);
    }

    [Fact]
    public void First_assignment_wins_among_several()
    {
        // The bug this replaced: FindFirst on a multi-warehouse operator was arbitrary rather than
        // ordered. Order now comes from the claim list, which follows AssignedWarehouseCodes.
        var resolved = DefaultWarehouseResolver.Resolve(
            Operator("DEPOT2", "DEPOT3"), Configured, Cache("DEPOT2", "DEPOT3", Configured));

        Assert.Equal("DEPOT2", resolved);
    }

    [Fact]
    public void Uncached_assignments_are_skipped_in_favour_of_a_cached_one()
    {
        var resolved = DefaultWarehouseResolver.Resolve(
            Operator("GONE", "DEPOT3"), Configured, Cache("DEPOT3", Configured));

        Assert.Equal("DEPOT3", resolved);
    }

    // ── Never hand back something the dropdown cannot show ──────────────────────────────────

    [Fact]
    public void Configured_default_missing_from_the_cache_resolves_to_nothing()
    {
        var resolved = DefaultWarehouseResolver.Resolve(
            Operator(), Configured, Cache("01", "DEPOT2"));

        Assert.Null(resolved);
    }

    [Fact]
    public void Empty_cache_resolves_to_nothing()
    {
        // Master data failed to load. Both screens already warn about that separately.
        var resolved = DefaultWarehouseResolver.Resolve(
            Operator(Configured), Configured, Cache());

        Assert.Null(resolved);
    }

    [Fact]
    public void Operator_whose_every_assignment_is_uncached_resolves_to_nothing()
    {
        var resolved = DefaultWarehouseResolver.Resolve(
            Operator("GONE", "ALSOGONE"), Configured, Cache("01", Configured));

        Assert.Null(resolved);
    }

    // ── Missing configuration ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Unrestricted_operator_with_no_configured_default_resolves_to_nothing(string? configured)
    {
        var resolved = DefaultWarehouseResolver.Resolve(
            Operator(), configured, Cache("01", Configured));

        Assert.Null(resolved);
    }

    [Fact]
    public void Assigned_operator_still_gets_their_own_when_nothing_is_configured()
    {
        var resolved = DefaultWarehouseResolver.Resolve(
            Operator("DEPOT2"), configuredDefault: null, Cache("DEPOT2"));

        Assert.Equal("DEPOT2", resolved);
    }

    [Fact]
    public void Blank_warehouse_claims_are_ignored()
    {
        var resolved = DefaultWarehouseResolver.Resolve(
            Operator("   ", "DEPOT2"), Configured, Cache("DEPOT2", Configured));

        Assert.Equal("DEPOT2", resolved);
    }

    // ── SAP warehouse codes are not case sensitive ──────────────────────────────────────────

    [Fact]
    public void Configured_default_matches_the_cache_regardless_of_case()
    {
        var resolved = DefaultWarehouseResolver.Resolve(
            Operator(), "kefshop", Cache(Configured));

        Assert.Equal("kefshop", resolved);
    }

    [Fact]
    public void Entitlement_matches_the_assignment_regardless_of_case()
    {
        var resolved = DefaultWarehouseResolver.Resolve(
            Operator("kefshop"), Configured, Cache(Configured));

        Assert.Equal(Configured, resolved);
    }
}
