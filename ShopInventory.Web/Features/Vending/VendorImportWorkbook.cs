using ClosedXML.Excel;
using ErrorOr;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.Vending;

/// <summary>
/// The vendor upload template: writing a blank one, and reading a filled-in one back into rows.
/// </summary>
/// <remarks>
/// Columns are found by their header text, not their position, so a sheet whose columns were
/// reordered or that gained a notes column still reads. Nothing here judges a row — whether a code
/// fits its depot, or is taken, is the API's call, and a copy of that rule here would only be a second
/// answer to disagree with. Reading only turns cells into text.
/// </remarks>
public static class VendorImportWorkbook
{
    public const string VendorsSheet = "Vendors";
    public const string DepotsSheet = "Depots";
    public const string GuideSheet = "How to fill";
    public const int MaxRows = 1000;

    /// <summary>The template's columns, in order: header text and the field it fills.</summary>
    public static readonly IReadOnlyList<(string Header, string Field)> Columns =
    [
        ("Vendor code", nameof(ImportVendorRowModel.Code)),
        ("Depot", nameof(ImportVendorRowModel.Depot)),
        ("First name", nameof(ImportVendorRowModel.Name)),
        ("Surname", nameof(ImportVendorRowModel.Surname)),
        ("Phone", nameof(ImportVendorRowModel.Phone)),
        ("VAT number", nameof(ImportVendorRowModel.VatNumber)),
        ("Email", nameof(ImportVendorRowModel.Email)),
        ("Address", nameof(ImportVendorRowModel.Address)),
    ];

    private static readonly XLColor HeaderFill = XLColor.FromHtml("#1a237e");
    private static readonly XLColor HeaderInk = XLColor.White;
    private static readonly XLColor MutedInk = XLColor.FromHtml("#616161");
    private static readonly XLColor RuleGray = XLColor.FromHtml("#bdbdbd");

    // ── Writing ────────────────────────────────────────────────────────────

    /// <param name="depots">The depots to offer, each with its code prefix and next free code.</param>
    /// <param name="depotNames">Business partner names by code, for the Depots sheet; a missing name shows the code.</param>
    public static byte[] BuildTemplate(
        IReadOnlyList<VendingDepotModel> depots,
        IReadOnlyDictionary<string, string> depotNames)
    {
        using var workbook = new XLWorkbook();
        workbook.Style.Font.FontName = "Calibri";
        workbook.Style.Font.FontSize = 11;
        workbook.Properties.Title = "Vendor upload template";

        var vendors = workbook.Worksheets.Add(VendorsSheet);
        var depotSheet = workbook.Worksheets.Add(DepotsSheet);
        var guide = workbook.Worksheets.Add(GuideSheet);

        WriteVendorsSheet(vendors, depots.Count);
        WriteDepotsSheet(depotSheet, depots, depotNames);
        WriteGuideSheet(guide, depots);

        vendors.SetTabActive();
        vendors.Cell(2, 1).SetActive();

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static void WriteVendorsSheet(IXLWorksheet sheet, int depotCount)
    {
        for (var index = 0; index < Columns.Count; index++)
        {
            var cell = sheet.Cell(1, index + 1);
            cell.Value = Columns[index].Header == "First name" ? "First name *" : Columns[index].Header;
        }

        StyleHeader(sheet.Range(1, 1, 1, Columns.Count));
        sheet.SheetView.FreezeRows(1);

        var lastRow = MaxRows + 1;

        // Text, not General: Excel otherwise reads 0771234567 as a number and drops the leading zero,
        // and a code typed as 001 loses its digits the same way.
        foreach (var field in new[] { nameof(ImportVendorRowModel.Code), nameof(ImportVendorRowModel.Depot), nameof(ImportVendorRowModel.Phone), nameof(ImportVendorRowModel.VatNumber) })
        {
            var column = ColumnOf(field);
            sheet.Range(2, column, lastRow, column).Style.NumberFormat.Format = "@";
        }

        var code = sheet.Range(2, ColumnOf(nameof(ImportVendorRowModel.Code)), lastRow, ColumnOf(nameof(ImportVendorRowModel.Code)))
            .CreateDataValidation();
        code.IgnoreBlanks = true;
        code.InputTitle = "Vendor code";
        code.InputMessage = "The depot's prefix and three digits, like VMB001. Leave blank to take the depot's next number.";
        code.ShowInputMessage = true;

        if (depotCount > 0)
        {
            var depot = sheet.Range(2, ColumnOf(nameof(ImportVendorRowModel.Depot)), lastRow, ColumnOf(nameof(ImportVendorRowModel.Depot)))
                .CreateDataValidation();
            depot.List($"='{DepotsSheet}'!$A$2:$A${depotCount + 1}", true);
            depot.IgnoreBlanks = true;
            depot.InputTitle = "Depot";
            depot.InputMessage = "Pick the depot. It can be left blank when the vendor code is filled in, because the code's prefix names the depot.";
            depot.ShowInputMessage = true;
            // A warning, not a stop: a warehouse code (KEFBYC) is accepted too.
            depot.ErrorStyle = XLErrorStyle.Warning;
            depot.ErrorTitle = "Not a depot on the list";
            depot.ErrorMessage = "Use a depot from the Depots sheet, or its warehouse code.";
            depot.ShowErrorMessage = true;
        }

        var widths = new[] { 14, 12, 18, 18, 16, 16, 26, 36 };
        for (var index = 0; index < widths.Length; index++)
        {
            sheet.Column(index + 1).Width = widths[index];
        }
    }

    private static void WriteDepotsSheet(
        IXLWorksheet sheet,
        IReadOnlyList<VendingDepotModel> depots,
        IReadOnlyDictionary<string, string> depotNames)
    {
        string[] headers = ["Depot", "Name", "Warehouse", "Code prefix", "Next free code", "Note"];
        for (var index = 0; index < headers.Length; index++)
        {
            sheet.Cell(1, index + 1).Value = headers[index];
        }

        StyleHeader(sheet.Range(1, 1, 1, headers.Length));
        sheet.SheetView.FreezeRows(1);

        var row = 2;
        foreach (var depot in depots)
        {
            sheet.Cell(row, 1).Value = depot.BusinessPartnerCode;
            sheet.Cell(row, 2).Value = depotNames.TryGetValue(depot.BusinessPartnerCode, out var name) ? name : depot.BusinessPartnerCode;
            sheet.Cell(row, 3).Value = string.Join(", ", depot.WarehouseCodes);
            sheet.Cell(row, 4).Value = depot.VendorCodePrefix ?? string.Empty;
            sheet.Cell(row, 5).Value = depot.NextVendorCode ?? string.Empty;
            sheet.Cell(row, 6).Value = depot.VendorCodeProblem is null ? string.Empty : $"Vendors cannot be added: {depot.VendorCodeProblem}.";
            row++;
        }

        sheet.Columns(1, 5).AdjustToContents();
        sheet.Column(6).Width = 60;
        if (row > 2)
        {
            sheet.Range(2, 6, row - 1, 6).Style.Font.FontColor = MutedInk;
        }
    }

    private static void WriteGuideSheet(IXLWorksheet sheet, IReadOnlyList<VendingDepotModel> depots)
    {
        var prefixes = depots
            .Where(depot => depot.VendorCodePrefix is not null)
            .Select(depot => $"{depot.VendorCodePrefix} — {string.Join(", ", depot.WarehouseCodes)}")
            .Distinct()
            .ToList();

        List<string> lines =
        [
            "Uploading vendors",
            "",
            "Fill in one vendor per row on the Vendors sheet, from row 2. Keep the header row as it is.",
            "",
            "Vendor code",
            "Every vendor code is the depot's prefix followed by three digits, such as VMB001.",
            .. prefixes.Select(prefix => "   " + prefix),
            "Leave the code blank to give the vendor the depot's next free number (see the Depots sheet).",
            "A code that belongs to a removed vendor at the same depot brings that vendor back.",
            "A code already used by a live vendor is refused — edit that vendor on the page instead.",
            "",
            "Depot",
            "Pick the depot from the list. It may be left blank when the vendor code is filled in,",
            "because the code's prefix already says which depot the vendor buys from.",
            "",
            "First name is required. Surname, phone, VAT number, email and address are optional.",
            "",
            $"Up to {MaxRows} vendors per file. The upload is checked first and shows every problem;",
            "nothing is added until every row is right.",
        ];

        for (var index = 0; index < lines.Count; index++)
        {
            sheet.Cell(index + 1, 1).Value = lines[index];
        }

        sheet.Cell(1, 1).Style.Font.Bold = true;
        sheet.Cell(1, 1).Style.Font.FontSize = 14;
        sheet.Cell(1, 1).Style.Font.FontColor = HeaderFill;

        foreach (var heading in new[] { "Vendor code", "Depot" })
        {
            var index = lines.IndexOf(heading);
            sheet.Cell(index + 1, 1).Style.Font.Bold = true;
        }

        sheet.Column(1).Width = 100;
        sheet.ShowGridLines = false;
    }

    private static void StyleHeader(IXLRange header)
    {
        header.Style.Font.Bold = true;
        header.Style.Font.FontColor = HeaderInk;
        header.Style.Fill.BackgroundColor = HeaderFill;
        header.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        header.Style.Border.BottomBorderColor = RuleGray;
        header.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        header.Worksheet.Row(header.FirstRow().RowNumber()).Height = 22;
    }

    private static int ColumnOf(string field) =>
        Columns.Select((column, index) => (column, index)).First(pair => pair.column.Field == field).index + 1;

    // ── Reading ────────────────────────────────────────────────────────────

    /// <summary>
    /// The vendor rows of a filled-in template. Blank rows are skipped; every other row is returned as
    /// typed, with the spreadsheet row number it came from.
    /// </summary>
    public static ErrorOr<List<ImportVendorRowModel>> Read(Stream content)
    {
        XLWorkbook workbook;
        try
        {
            workbook = new XLWorkbook(content);
        }
        catch (Exception)
        {
            return Errors.Vending.FileUnreadable;
        }

        using (workbook)
        {
            var sheet = workbook.Worksheets.FirstOrDefault(candidate =>
                            string.Equals(candidate.Name, VendorsSheet, StringComparison.OrdinalIgnoreCase))
                        ?? workbook.Worksheets.FirstOrDefault();

            if (sheet is null)
            {
                return Errors.Vending.NotTheTemplate;
            }

            var lastColumn = sheet.LastColumnUsed()?.ColumnNumber() ?? 0;
            var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 0;

            // The header is looked for in the first few rows rather than assumed to be row 1, so a title
            // line someone typed above it does not make the file unreadable.
            int? headerRow = null;
            Dictionary<string, int> fields = [];
            for (var row = 1; row <= Math.Min(lastRow, 10) && headerRow is null; row++)
            {
                var found = new Dictionary<string, int>();
                for (var column = 1; column <= lastColumn; column++)
                {
                    if (FieldFor(sheet.Cell(row, column).GetString()) is { } field && !found.ContainsKey(field))
                    {
                        found[field] = column;
                    }
                }

                if (found.ContainsKey(nameof(ImportVendorRowModel.Name)))
                {
                    headerRow = row;
                    fields = found;
                }
            }

            if (headerRow is null ||
                (!fields.ContainsKey(nameof(ImportVendorRowModel.Code)) && !fields.ContainsKey(nameof(ImportVendorRowModel.Depot))))
            {
                return Errors.Vending.NotTheTemplate;
            }

            var rows = new List<ImportVendorRowModel>();
            for (var row = headerRow.Value + 1; row <= lastRow; row++)
            {
                string? Text(string field) =>
                    fields.TryGetValue(field, out var column) && sheet.Cell(row, column).GetFormattedString().Trim() is { Length: > 0 } text
                        ? text
                        : null;

                var vendor = new ImportVendorRowModel
                {
                    RowNumber = row,
                    Code = Text(nameof(ImportVendorRowModel.Code)),
                    Depot = Text(nameof(ImportVendorRowModel.Depot)),
                    Name = Text(nameof(ImportVendorRowModel.Name)),
                    Surname = Text(nameof(ImportVendorRowModel.Surname)),
                    Phone = Text(nameof(ImportVendorRowModel.Phone)),
                    VatNumber = Text(nameof(ImportVendorRowModel.VatNumber)),
                    Email = Text(nameof(ImportVendorRowModel.Email)),
                    Address = Text(nameof(ImportVendorRowModel.Address)),
                };

                if (vendor is { Code: null, Depot: null, Name: null, Surname: null, Phone: null, VatNumber: null, Email: null, Address: null })
                {
                    continue;
                }

                rows.Add(vendor);
            }

            if (rows.Count == 0)
            {
                return Errors.Vending.FileHasNoVendors;
            }

            if (rows.Count > MaxRows)
            {
                return Errors.Vending.FileTooLong(rows.Count, MaxRows);
            }

            return rows;
        }
    }

    /// <summary>Which field a header names, forgiving case, spacing, punctuation and a required-marker star.</summary>
    private static string? FieldFor(string header)
    {
        var key = new string(header.Where(char.IsLetter).Select(char.ToLowerInvariant).ToArray());
        return key switch
        {
            "vendorcode" or "code" => nameof(ImportVendorRowModel.Code),
            "depot" => nameof(ImportVendorRowModel.Depot),
            "firstname" or "name" => nameof(ImportVendorRowModel.Name),
            "surname" or "lastname" => nameof(ImportVendorRowModel.Surname),
            "phone" or "phonenumber" => nameof(ImportVendorRowModel.Phone),
            "vatnumber" or "vat" => nameof(ImportVendorRowModel.VatNumber),
            "email" => nameof(ImportVendorRowModel.Email),
            "address" => nameof(ImportVendorRowModel.Address),
            _ => null
        };
    }
}
