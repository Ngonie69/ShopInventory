using ShopInventory.Web.Common;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the depot a controller's dashboard opens on.
/// </summary>
/// <remarks>
/// Both dashboards used to open on the first "warehouse" claim, which is the order the claims
/// happened to arrive in rather than a choice — and usually landed on the general warehouse while
/// the controller works out of a centre.
///
/// The rule these tests pin down: the centre naming the account's own section, then any centre,
/// then a warehouse of any kind naming that section, then the first code offered. The names are
/// SAP's warehouse descriptions, where every operational site reads as a centre and the company
/// name in front of it ("Kefalos", "Cortina") says nothing about which one.
/// </remarks>
public sealed class HomeDepotResolverTests
{
    private const string Bulawayo = "KEFBYC";
    private const string Graniteside = "KEFGRC";
    private const string Machipisa = "CORMACH";
    private const string General = "01";

    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        [General] = "General Warehouse",
        [Bulawayo] = "Kefalos Bulawayo Centre",
        [Graniteside] = "Kefalos Graniteside Centre",
        [Machipisa] = "Cortina Machipisa Centre",
        ["KEFFACT"] = "Kefalos Factory Dispatch",
        ["KEFDC"] = "Kefalos DC Store",
        ["KEFDEP"] = "Kefalos Depot",
        ["KEFVIC"] = "Cheeseman DC Vic Falls Centre",
        ["KEFHRE"] = "Cheeseman DC Harare Centre"
    };

    private static string? Resolve(string? section, params string[] codes) =>
        HomeDepotResolver.Resolve(codes, Names, section);

    // ── The section picks between centres ───────────────────────────────────────────────────

    [Fact]
    public void The_centre_naming_the_section_wins_over_the_other_centres()
    {
        Assert.Equal(Bulawayo, Resolve("Bulawayo", General, Graniteside, Bulawayo, Machipisa));
    }

    [Fact]
    public void The_section_wins_wherever_it_sits_in_the_offered_order()
    {
        Assert.Equal(Machipisa, Resolve("Machipisa", Bulawayo, Graniteside, Machipisa));
    }

    [Fact]
    public void A_section_written_as_its_display_label_still_matches()
    {
        // "Bulawayo" is stored on the account but shows as "Cheeseman DC Byo", and the abbreviation
        // shares no word with the warehouse name — normalising back to the option is what saves it.
        Assert.Equal(Bulawayo, Resolve("Cheeseman DC Byo", Graniteside, Bulawayo));
    }

    [Fact]
    public void The_closest_match_wins_when_a_section_shares_a_word_with_several()
    {
        // Both are Cheeseman DCs, so "Cheeseman" alone cannot separate them.
        Assert.Equal("KEFVIC", Resolve("Cheeseman DC Vic Falls", "KEFHRE", "KEFVIC"));
    }

    // ── Without a usable section, any centre beats the general warehouse ─────────────────────

    [Fact]
    public void Any_centre_is_preferred_when_the_account_has_no_section()
    {
        Assert.Equal(Graniteside, Resolve(null, General, Graniteside));
    }

    [Fact]
    public void Any_centre_is_preferred_when_the_section_names_none_of_them()
    {
        Assert.Equal(Graniteside, Resolve("Cheeseman DC Richwell", General, Graniteside, Bulawayo));
    }

    [Fact]
    public void The_first_centre_offered_wins_when_the_section_separates_none_of_them()
    {
        Assert.Equal(Graniteside, Resolve(null, Graniteside, Bulawayo, Machipisa));
    }

    // ── Generic words never tie two sites together ──────────────────────────────────────────

    [Fact]
    public void A_shared_abbreviation_does_not_make_two_sites_the_same()
    {
        // "Kefalos DC Store" says only that it is a DC, and the section is a different one. Were
        // "DC" long enough to count as a place word it would score and win; it must lose to the
        // offered order instead.
        Assert.Equal(General, Resolve("Cheeseman DC Richwell", General, "KEFDC"));
    }

    [Fact]
    public void A_shared_kind_of_place_does_not_make_two_sites_the_same()
    {
        // Nothing links "Harare Depot" to "Kefalos Depot" except the word for what they both are.
        Assert.Equal(General, Resolve("Harare Depot", General, "KEFDEP"));
    }

    // ── Accounts holding no centre at all ───────────────────────────────────────────────────

    [Fact]
    public void A_warehouse_naming_the_section_beats_the_offered_order_when_no_centre_is_held()
    {
        Assert.Equal("KEFFACT", Resolve("Factory", General, "KEFFACT"));
    }

    [Fact]
    public void The_first_code_stands_when_nothing_names_a_centre_or_the_section()
    {
        Assert.Equal(General, Resolve("Bulawayo", General, "KEFFACT"));
    }

    [Fact]
    public void An_unnamed_code_is_still_offered_rather_than_dropped()
    {
        // Master data was unreachable, so the picker is running on claim codes with no names.
        Assert.Equal("KEFXXX", HomeDepotResolver.Resolve(
            ["KEFXXX", Bulawayo], new Dictionary<string, string>(), "Bulawayo"));
    }

    [Fact]
    public void No_codes_resolves_to_nothing()
    {
        Assert.Null(Resolve("Bulawayo"));
    }
}
