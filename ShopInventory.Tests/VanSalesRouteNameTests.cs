using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.DTOs;
using ShopInventory.Features.VanSalesCompatibility;

namespace ShopInventory.Tests;

/// <summary>
/// The rep's route, in words. The handset shows the business partner's name beside the warehouse and
/// falls back to the bare code without one, so a rep reads "VAN001 · VAN010" and learns nothing.
/// <para>
/// The point of these is mostly what happens when the name cannot be had: signing in has to keep
/// working. A rep at the first stop of the day with an unreadable business partner master should see
/// a poorly labelled route, not a refused login.
/// </para>
/// </summary>
public sealed class VanSalesRouteNameTests
{
    [Fact]
    public async Task The_assigned_partners_name_is_the_route_name()
    {
        var name = await Resolve("VAN010", _ => new BusinessPartnerDto { CardCode = "VAN010", CardName = "Harare North Route" });

        Assert.Equal("Harare North Route", name);
    }

    [Fact]
    public async Task The_code_is_looked_up_trimmed()
    {
        string? asked = null;

        await Resolve("  VAN010  ", code => { asked = code; return new BusinessPartnerDto { CardName = "Harare North" }; });

        Assert.Equal("VAN010", asked);
    }

    [Fact]
    public async Task A_name_padded_by_SAP_is_trimmed()
    {
        var name = await Resolve("VAN010", _ => new BusinessPartnerDto { CardName = "  Harare North Route  " });

        Assert.Equal("Harare North Route", name);
    }

    // ── Nothing to resolve ──────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_user_on_no_route_is_not_looked_up(string? code)
    {
        // Not every user is a van — a depot controller signing in has no assigned partner, and asking
        // SAP for the empty code would be a wasted round trip on every login.
        var asked = false;

        var name = await VanSalesRouteName.ResolveAsync(
            code,
            (_, _) => { asked = true; return Task.FromResult<BusinessPartnerDto?>(null); },
            NullLogger.Instance);

        Assert.Equal(string.Empty, name);
        Assert.False(asked);
    }

    [Fact]
    public async Task A_partner_SAP_does_not_know_leaves_the_route_unnamed()
    {
        var name = await Resolve("VAN010", _ => null);

        Assert.Equal(string.Empty, name);
    }

    [Fact]
    public async Task A_partner_with_no_name_leaves_the_route_unnamed()
    {
        var name = await Resolve("VAN010", _ => new BusinessPartnerDto { CardCode = "VAN010", CardName = "   " });

        Assert.Equal(string.Empty, name);
    }

    // ── Signing in survives SAP ─────────────────────────────────────────────

    /// <summary>
    /// The one that matters. Reading the business partner master is a live SAP call, and it throws on
    /// anything that is not a 404. Letting that escape would mean SAP being down stops every van in
    /// the fleet signing in — to put a name next to a code.
    /// </summary>
    [Fact]
    public async Task SAP_being_unreachable_does_not_stop_a_sign_in()
    {
        var name = await VanSalesRouteName.ResolveAsync(
            "VAN010",
            (_, _) => throw new HttpRequestException("SAP Service Layer is unreachable"),
            NullLogger.Instance);

        Assert.Equal(string.Empty, name);
    }

    [Fact]
    public async Task A_SAP_timeout_does_not_stop_a_sign_in()
    {
        var name = await VanSalesRouteName.ResolveAsync(
            "VAN010",
            (_, _) => throw new TaskCanceledException("timed out", new TimeoutException()),
            NullLogger.Instance);

        Assert.Equal(string.Empty, name);
    }

    [Fact]
    public async Task A_faulted_read_does_not_stop_a_sign_in()
    {
        // Thrown from inside the task rather than synchronously, which is how the real client fails.
        var name = await VanSalesRouteName.ResolveAsync(
            "VAN010",
            (_, _) => Task.FromException<BusinessPartnerDto?>(new InvalidOperationException("SAP said no")),
            NullLogger.Instance);

        Assert.Equal(string.Empty, name);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static Task<string> Resolve(string? code, Func<string, BusinessPartnerDto?> readPartner) =>
        VanSalesRouteName.ResolveAsync(
            code,
            (cardCode, _) => Task.FromResult(readPartner(cardCode)),
            NullLogger.Instance);
}
