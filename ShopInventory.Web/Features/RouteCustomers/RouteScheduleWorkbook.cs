using ClosedXML.Excel;
using ErrorOr;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.RouteCustomers;

/// <summary>
/// Reads the office's "Customer Database per Route" workbook: a sheet per truck, a column of outlet
/// names under each weekday heading, and the calling order in the SEQ column beside it.
/// </summary>
/// <remarks>
/// <para>
/// The workbook is kept by hand and is not a template, so this reads it the way a person does rather
/// than by fixed cell positions. A weekday heading in the first few rows starts a column; the names
/// below it, down to the first long gap, are that day's calls. The SEQ number is
/// whatever number sits in the next column on the same row — one sheet has no SEQ heading at all.
/// </para>
/// <para>
/// Nothing here decides which shop a name means. That needs the van's customer list, which only the
/// API holds; this only turns cells into outlets. And nothing is dropped without saying so: a column
/// with no weekday, a weekday heading used twice on one sheet, and names stranded below a gap are each
/// reported in <see cref="RouteScheduleSheetModel.Skipped"/> for the page to show.
/// </para>
/// <para>
/// Rows with no SEQ number are kept. Most are area headings ("Zengeza 3", "Unit F") that will match no
/// shop, but some are shops somebody forgot to number, and only the match can tell them apart.
/// </para>
/// </remarks>
public static class RouteScheduleWorkbook
{
    /// <summary>How many rows from the top a weekday heading is looked for in.</summary>
    public const int HeaderSearchRows = 5;

    /// <summary>A run of this many blank rows ends a day's list. Single blank lines inside a list are common.</summary>
    public const int GapRows = 5;

    private static readonly Dictionary<string, DayOfWeek> Weekdays = new(StringComparer.Ordinal)
    {
        ["monday"] = DayOfWeek.Monday,
        ["tuesday"] = DayOfWeek.Tuesday,
        ["wednesday"] = DayOfWeek.Wednesday,
        ["thursday"] = DayOfWeek.Thursday,
        ["friday"] = DayOfWeek.Friday,
        ["saturday"] = DayOfWeek.Saturday,
        ["sunday"] = DayOfWeek.Sunday,
    };

    /// <summary>The words of the summary block some sheets keep at the foot of a column.</summary>
    private static readonly HashSet<string> SummaryWords = new(StringComparer.Ordinal)
    {
        "total", "comments", "started", "current", "decline",
    };

    /// <summary>
    /// The summary words that end a list when met inside it. Not <c>total</c>: a Total service station
    /// is a shop, and sits in the CZA Tuesday list with a SEQ number of its own. The summary block's
    /// own "Total" line always comes after a gap, which ends the list first.
    /// </summary>
    private static readonly HashSet<string> EndWords = new(StringComparer.Ordinal)
    {
        "comments", "started", "current", "decline",
    };

    public static ErrorOr<RouteScheduleWorkbookModel> Read(Stream content)
    {
        XLWorkbook workbook;
        try
        {
            workbook = new XLWorkbook(content);
        }
        catch (Exception)
        {
            return Errors.RouteSchedule.FileUnreadable;
        }

        using (workbook)
        {
            var result = new RouteScheduleWorkbookModel();

            foreach (var sheet in workbook.Worksheets)
            {
                if (ReadSheet(sheet) is { } read)
                {
                    result.Sheets.Add(read);
                }
            }

            return result.Sheets.Any(sheet => sheet.Outlets.Count > 0)
                ? result
                : Errors.RouteSchedule.NoWeekdays;
        }
    }

    private static RouteScheduleSheetModel? ReadSheet(IXLWorksheet sheet)
    {
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 0;
        var lastColumn = sheet.LastColumnUsed()?.ColumnNumber() ?? 0;

        if (lastRow == 0)
        {
            return null;
        }

        var read = new RouteScheduleSheetModel { Name = sheet.Name.Trim() };
        var headerRows = Math.Min(lastRow, HeaderSearchRows);
        var seen = new HashSet<DayOfWeek>();
        var weekdayColumns = new HashSet<int>();

        for (var row = 1; row <= headerRows; row++)
        {
            for (var column = 1; column <= lastColumn; column++)
            {
                if (WeekdayOf(sheet.Cell(row, column).GetString()) is not { } day)
                {
                    continue;
                }

                weekdayColumns.Add(column);

                if (!seen.Add(day))
                {
                    read.Skipped.Add(
                        $"Column {ColumnLetter(column)} is a second {Title(day)} heading{FirstNameNote(sheet, row, column, lastRow)}. " +
                        "Only the first one was read; give this column its own weekday heading to include it.");
                    continue;
                }

                ReadDay(sheet, read, day, row, column, lastRow);
            }
        }

        if (weekdayColumns.Count == 0)
        {
            read.Skipped.Add("No weekday heading was found in the first rows of this sheet, so nothing on it was read.");
            return read;
        }

        // A SEQ heading names the column of numbers beside a list of outlets. One whose outlet column
        // has no weekday heading is a list of calls on no stated day.
        for (var row = 1; row <= headerRows; row++)
        {
            for (var column = 2; column <= lastColumn; column++)
            {
                if (Key(sheet.Cell(row, column).GetString()) != "seq" || weekdayColumns.Contains(column - 1))
                {
                    continue;
                }

                var names = NamesBelow(sheet, row + 1, column - 1, lastRow);
                if (names.Count > 0)
                {
                    read.Skipped.Add(
                        $"Column {ColumnLetter(column - 1)} has {names.Count} names (first: “{names[0]}”) but no weekday heading. " +
                        "Put the day above it to include it.");
                }
            }
        }

        return read;
    }

    private static void ReadDay(
        IXLWorksheet sheet,
        RouteScheduleSheetModel read,
        DayOfWeek day,
        int headerRow,
        int column,
        int lastRow)
    {
        var blankRun = 0;
        var row = headerRow + 1;

        for (; row <= lastRow; row++)
        {
            var text = sheet.Cell(row, column).GetFormattedString().Trim();

            if (!IsName(text))
            {
                if (++blankRun >= GapRows)
                {
                    break;
                }

                continue;
            }

            if (EndWords.Contains(Key(text)))
            {
                break;
            }

            blankRun = 0;
            read.Outlets.Add(new RouteScheduleOutletModel
            {
                Ref = $"{read.Name}!{ColumnLetter(column)}{row}",
                Name = text,
                Day = day,
                Sequence = SequenceAt(sheet.Cell(row, column + 1)),
            });
        }

        var stranded = NamesBelow(sheet, row + 1, column, lastRow);
        if (stranded.Count > 0)
        {
            read.Skipped.Add(
                $"{stranded.Count} names further down the {Title(day)} column, after a gap (first: “{stranded[0]}”), were not read. " +
                "Move them up into the list if they are calls on that day.");
        }
    }

    /// <summary>The names in a column from a row down, leaving out the summary block.</summary>
    private static List<string> NamesBelow(IXLWorksheet sheet, int fromRow, int column, int lastRow)
    {
        var names = new List<string>();

        for (var row = fromRow; row <= lastRow; row++)
        {
            var text = sheet.Cell(row, column).GetFormattedString().Trim();
            if (IsName(text) && !SummaryWords.Contains(Key(text)))
            {
                names.Add(text);
            }
        }

        return names;
    }

    private static string FirstNameNote(IXLWorksheet sheet, int headerRow, int column, int lastRow)
    {
        var names = NamesBelow(sheet, headerRow + 1, column, lastRow);
        return names.Count == 0 ? " with nothing under it" : $" with {names.Count} names under it (first: “{names[0]}”)";
    }

    /// <summary>A cell holds a name when it has a letter in it. Numbers, dates and status icons do not.</summary>
    private static bool IsName(string text) => text.Any(char.IsLetter);

    private static int? SequenceAt(IXLCell cell)
    {
        if (cell.DataType == XLDataType.Number)
        {
            var value = cell.GetDouble();
            return value >= 0 && value <= int.MaxValue && value == Math.Floor(value) ? (int)value : null;
        }

        return int.TryParse(cell.GetFormattedString().Trim(), out var parsed) && parsed >= 0 ? parsed : null;
    }

    private static DayOfWeek? WeekdayOf(string text) =>
        Weekdays.TryGetValue(Key(text), out var day) ? day : null;

    private static string Key(string text) =>
        new(text.Where(char.IsLetter).Select(char.ToLowerInvariant).ToArray());

    private static string Title(DayOfWeek day) => day.ToString();

    private static string ColumnLetter(int column) => XLHelper.GetColumnLetterFromNumber(column);
}
