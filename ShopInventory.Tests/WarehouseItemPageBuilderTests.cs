using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Assembling one page of a warehouse, and walking several of them while the warehouse changes —
/// which it does, because what is being paged is the codes holding stock right now and the reps
/// reading it are the ones selling. A page that comes back short or skips a code does not fail; the
/// van is quietly missing a product, and the handset stores that and carries it offline.
/// </summary>
public sealed class WarehouseItemPageBuilderTests
{
    // ── Walking a warehouse that is changing underneath ─────────────────────

    [Fact]
    public async Task An_item_selling_out_between_pages_does_not_cost_the_next_one()
    {
        // Page one serves A B C, so the cursor is C. B then sells out and leaves the list. Under an
        // offset, page two at skip=3 would land on E and D would never be returned.
        var warehouse = Codes("A", "B", "C", "D", "E", "F");
        var first = await Build(warehouse, after: null, pageSize: 3);

        var afterTheSale = Codes("A", "C", "D", "E", "F");
        var second = await Build(afterTheSale, first.NextCursor, pageSize: 3);

        Assert.Equal(["A", "B", "C"], ItemCodes(first));
        Assert.Equal(["D", "E", "F"], ItemCodes(second));
    }

    [Fact]
    public async Task A_delivery_between_pages_does_not_repeat_an_item()
    {
        // The mirror case: stock arriving for a code that sorts early shifts the list right, and an
        // offset re-serves an item the caller already holds.
        var first = await Build(Codes("A", "B", "C", "D"), after: null, pageSize: 2);

        var afterTheDelivery = Codes("A", "AA", "B", "C", "D");
        var second = await Build(afterTheDelivery, first.NextCursor, pageSize: 2);

        Assert.Equal(["A", "B"], ItemCodes(first));
        Assert.Equal(["C", "D"], ItemCodes(second));
    }

    [Fact]
    public async Task The_cursors_own_item_selling_out_does_not_lose_the_next_page()
    {
        // The last item on a page is as likely to sell out as any other, and then the cursor names a
        // code that is no longer in the list.
        var first = await Build(Codes("A", "B", "C", "D"), after: null, pageSize: 2);
        Assert.Equal("B", first.NextCursor);

        var second = await Build(Codes("A", "C", "D"), first.NextCursor, pageSize: 2);

        Assert.Equal(["C", "D"], ItemCodes(second));
    }

    [Fact]
    public async Task A_full_walk_returns_the_warehouse_exactly_once()
    {
        var warehouse = Codes([.. Enumerable.Range(1, 250).Select(i => $"ITEM{i:D4}")]);

        var seen = new List<string>();
        string? cursor = null;

        do
        {
            var page = await Build(warehouse, cursor, pageSize: 40);
            seen.AddRange(ItemCodes(page));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(warehouse, seen);
    }

    // ── Pages stay dense ────────────────────────────────────────────────────

    /// <summary>
    /// Only live inventory items come back, so a code still carrying batch stock for an item since
    /// marked invalid in SAP resolves to nothing. Left as a gap, the page is short — and a whole
    /// window of them is an empty page with more behind it, which reads like the end of the
    /// catalogue to anything walking pages.
    /// </summary>
    [Fact]
    public async Task Codes_that_resolve_to_nothing_are_filled_over()
    {
        var warehouse = Codes("A", "B", "C", "D", "E", "F");

        var page = await Build(warehouse, after: null, pageSize: 3, dead: ["B", "C"]);

        Assert.Equal(["A", "D", "E"], ItemCodes(page));
        Assert.True(page.HasMore);
    }

    [Fact]
    public async Task A_window_of_nothing_but_dead_codes_still_yields_a_full_page()
    {
        var warehouse = Codes("A", "B", "C", "D", "E", "F");

        var page = await Build(warehouse, after: null, pageSize: 2, dead: ["A", "B"]);

        Assert.Equal(["C", "D"], ItemCodes(page));
    }

    /// <summary>
    /// The cursor names the last code consumed rather than the last item returned. Naming the item
    /// instead would leave the dead codes behind it to be read again on every following page.
    /// </summary>
    [Fact]
    public async Task The_cursor_covers_dead_codes_at_the_end_of_a_page()
    {
        var warehouse = Codes("A", "B", "C", "D");

        // pageSize 1: the first window takes A alone, which resolves. B is not examined yet.
        var page = await Build(warehouse, after: null, pageSize: 1, dead: ["B"]);

        Assert.Equal(["A"], ItemCodes(page));
        Assert.Equal("A", page.NextCursor);

        // The next page consumes the dead B and returns C, rather than stalling on B.
        var next = await Build(warehouse, page.NextCursor, pageSize: 1, dead: ["B"]);

        Assert.Equal(["C"], ItemCodes(next));
        Assert.Equal("C", next.NextCursor);
    }

    [Fact]
    public async Task Filling_is_bounded_but_still_advances_the_cursor()
    {
        // Every code dead, so no window can fill the page. It must give up rather than scan the
        // warehouse — and must leave a cursor past what it consumed so the walk still progresses.
        var warehouse = Codes([.. Enumerable.Range(1, 200).Select(i => $"ITEM{i:D4}")]);

        var page = await Build(warehouse, after: null, pageSize: 10, dead: [.. warehouse]);

        Assert.Empty(page.Items);
        Assert.True(page.HasMore);
        Assert.Equal(warehouse[(WarehouseItemPageBuilder.MaxFillWindows * 10) - 1], page.NextCursor);
    }

    [Fact]
    public async Task A_bounded_fill_does_not_read_more_than_it_was_allowed()
    {
        var warehouse = Codes([.. Enumerable.Range(1, 200).Select(i => $"ITEM{i:D4}")]);
        var windows = 0;

        await WarehouseItemPageBuilder.BuildAsync(
            warehouse,
            after: null,
            pageSize: 10,
            resolveCodes: (_, _) =>
            {
                windows++;
                return Task.FromResult(new List<Item>());
            });

        Assert.Equal(WarehouseItemPageBuilder.MaxFillWindows, windows);
    }

    // ── Ends and edges ──────────────────────────────────────────────────────

    [Fact]
    public async Task The_last_page_reports_no_more_and_carries_no_cursor()
    {
        var page = await Build(Codes("A", "B", "C"), after: null, pageSize: 10);

        Assert.Equal(["A", "B", "C"], ItemCodes(page));
        Assert.False(page.HasMore);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task A_page_that_exactly_empties_the_warehouse_reports_no_more()
    {
        // The off-by-one worth pinning: consuming the final code is the end, not a page boundary.
        var page = await Build(Codes("A", "B"), after: null, pageSize: 2);

        Assert.False(page.HasMore);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task A_cursor_past_the_end_reads_nothing_without_asking()
    {
        var asked = false;

        var page = await WarehouseItemPageBuilder.BuildAsync(
            Codes("A", "B"),
            after: "Z",
            pageSize: 10,
            resolveCodes: (_, _) =>
            {
                asked = true;
                return Task.FromResult(new List<Item>());
            });

        Assert.Empty(page.Items);
        Assert.False(page.HasMore);
        Assert.Null(page.NextCursor);
        Assert.False(asked);
    }

    [Fact]
    public async Task An_empty_warehouse_reads_as_empty()
    {
        var page = await Build([], after: null, pageSize: 10);

        Assert.Empty(page.Items);
        Assert.False(page.HasMore);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task A_page_size_of_nothing_still_makes_progress(int pageSize)
    {
        // Asking for no rows at all would otherwise return an empty page forever.
        var page = await Build(Codes("A", "B"), after: null, pageSize: pageSize);

        Assert.Equal(["A"], ItemCodes(page));
        Assert.Equal("A", page.NextCursor);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>Sorted the way the warehouse query sorts: ordinal, case-insensitive.</summary>
    private static List<string> Codes(params string[] codes) =>
        [.. codes.OrderBy(code => code, StringComparer.OrdinalIgnoreCase)];

    private static string[] ItemCodes(WarehouseItemPage page) =>
        [.. page.Items.Select(item => item.ItemCode!)];

    /// <summary>
    /// Resolves codes to items, dropping any listed in <paramref name="dead"/> the way SAP drops a
    /// code that is not a live inventory item.
    /// </summary>
    private static Task<WarehouseItemPage> Build(
        IReadOnlyList<string> warehouse,
        string? after,
        int pageSize,
        string[]? dead = null)
    {
        var missing = (dead ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return WarehouseItemPageBuilder.BuildAsync(
            warehouse,
            after,
            pageSize,
            (codes, _) => Task.FromResult(
                codes.Where(code => !missing.Contains(code))
                     .Select(code => new Item { ItemCode = code, ItemName = $"Item {code}" })
                     .ToList()));
    }
}
