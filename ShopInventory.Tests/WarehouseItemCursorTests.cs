using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The warehouse item list is <c>OBTQ.Quantity &gt; 0</c> — what holds stock right now, not a
/// catalogue — so it changes between the pages of a single read, driven by the very reps reading
/// it. These pin the property that makes a multi-page read safe anyway: a page boundary is named
/// by the last code served, so codes arriving or leaving elsewhere cannot move it.
/// </summary>
public sealed class WarehouseItemCursorTests
{
    // Sorted the way GetAllItemCodesInWarehouseAsync sorts: ordinal, case-insensitive.
    private static List<string> Codes(params string[] codes) =>
        codes.OrderBy(code => code, StringComparer.OrdinalIgnoreCase).ToList();

    [Fact]
    public void No_cursor_starts_at_the_beginning()
    {
        Assert.Equal(0, WarehouseItemCursor.FindStart(Codes("A", "B", "C"), null));
    }

    [Fact]
    public void Blank_cursor_starts_at_the_beginning()
    {
        // An absent query parameter arrives as "" rather than null through some clients.
        Assert.Equal(0, WarehouseItemCursor.FindStart(Codes("A", "B", "C"), "   "));
    }

    [Fact]
    public void Cursor_resumes_after_the_code_it_names()
    {
        var codes = Codes("A", "B", "C", "D");

        Assert.Equal(2, WarehouseItemCursor.FindStart(codes, "B"));
    }

    [Fact]
    public void An_item_selling_out_does_not_shift_the_boundary()
    {
        // The bug this replaces. Page one served A..D and the cursor is D. B then sells out, so
        // under offsets every later code shifts one place left and skip=4 lands on F — E is
        // silently absent from the van's catalogue, with no error anywhere to say so.
        var afterTheSale = Codes("A", "C", "D", "E", "F");

        var start = WarehouseItemCursor.FindStart(afterTheSale, "D");

        Assert.Equal("E", afterTheSale[start]);
    }

    [Fact]
    public void A_delivery_before_the_cursor_does_not_repeat_an_item()
    {
        // The mirror case: stock arriving for a code that sorts early shifts the list right, and
        // an offset re-serves an item the caller already has.
        var afterTheDelivery = Codes("A", "B", "BB", "C", "D", "E");

        var start = WarehouseItemCursor.FindStart(afterTheDelivery, "D");

        Assert.Equal("E", afterTheDelivery[start]);
    }

    [Fact]
    public void A_cursor_whose_own_code_has_left_still_resumes_in_the_right_place()
    {
        // The last item on a page can be the one that sells out. The cursor names a code that is
        // no longer in the list, and resuming has to land on what follows where it used to be.
        var afterTheSale = Codes("A", "B", "C", "E", "F");

        var start = WarehouseItemCursor.FindStart(afterTheSale, "D");

        Assert.Equal("E", afterTheSale[start]);
    }

    [Fact]
    public void A_cursor_past_the_end_reports_exhaustion()
    {
        var codes = Codes("A", "B", "C");

        Assert.Equal(codes.Count, WarehouseItemCursor.FindStart(codes, "Z"));
    }

    [Fact]
    public void A_cursor_before_the_start_reads_everything()
    {
        Assert.Equal(0, WarehouseItemCursor.FindStart(Codes("B", "C"), "A"));
    }

    [Fact]
    public void Case_does_not_move_the_boundary()
    {
        // SAP item codes are not consistently cased, and the list is sorted case-insensitively.
        // Comparing ordinally here would put "item2" after "ITEM3" and skip a code.
        var codes = Codes("item1", "ITEM2", "Item3");

        var start = WarehouseItemCursor.FindStart(codes, "item2");

        Assert.Equal("Item3", codes[start]);
    }

    [Fact]
    public void An_empty_warehouse_has_nothing_to_resume_from()
    {
        Assert.Equal(0, WarehouseItemCursor.FindStart([], "ANYTHING"));
    }

    [Fact]
    public void Walking_the_whole_list_by_cursor_visits_every_code_exactly_once()
    {
        // The property that matters, on a list that does not change: pages tile it.
        var codes = Codes(Enumerable.Range(1, 250).Select(i => $"ITEM{i:D4}").ToArray());
        const int pageSize = 40;

        var seen = new List<string>();
        string? cursor = null;

        while (true)
        {
            var start = WarehouseItemCursor.FindStart(codes, cursor);
            if (start >= codes.Count)
            {
                break;
            }

            var page = codes.GetRange(start, Math.Min(pageSize, codes.Count - start));
            seen.AddRange(page);
            cursor = page[^1];
        }

        Assert.Equal(codes, seen);
    }
}
