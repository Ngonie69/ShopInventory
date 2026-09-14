using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using ShopInventory.Web.Features.Vending;
using ShopInventory.Web.Models;

namespace ShopInventory.Tests;

/// <summary>
/// The vendor upload template, written and then read back the way someone would fill it in: typed into
/// Excel, saved, uploaded.
/// </summary>
public sealed class VendorImportWorkbookTests
{
    private static readonly List<VendingDepotModel> Depots =
    [
        new() { BusinessPartnerCode = "COR006", WarehouseCodes = ["KEFGRC"], VendorCodePrefix = "VMP", NextVendorCode = "VMP004" },
        new() { BusinessPartnerCode = "COR008", WarehouseCodes = ["KEFBYC"], VendorCodePrefix = "VMB", NextVendorCode = "VMB001" },
    ];

    private static readonly Dictionary<string, string> Names = new() { ["COR006"] = "Cortina Graniteside Vending" };

    private static byte[] Template() => VendorImportWorkbook.BuildTemplate(Depots, Names);

    private static byte[] Fill(byte[] template, Action<IXLWorksheet> fill)
    {
        using var workbook = new XLWorkbook(new MemoryStream(template));
        fill(workbook.Worksheet(VendorImportWorkbook.VendorsSheet));
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static ErrorOr.ErrorOr<List<ImportVendorRowModel>> Read(byte[] content) =>
        VendorImportWorkbook.Read(new MemoryStream(content));

    [Fact]
    public void The_template_lists_each_depot_with_its_prefix_and_next_code()
    {
        using var workbook = new XLWorkbook(new MemoryStream(Template()));

        Assert.Equal(
            [VendorImportWorkbook.VendorsSheet, VendorImportWorkbook.DepotsSheet, VendorImportWorkbook.GuideSheet],
            workbook.Worksheets.Select(sheet => sheet.Name));

        var vendors = workbook.Worksheet(VendorImportWorkbook.VendorsSheet);
        Assert.Equal("Vendor code", vendors.Cell(1, 1).GetString());
        Assert.Equal("Depot", vendors.Cell(1, 2).GetString());
        Assert.Equal("First name *", vendors.Cell(1, 3).GetString());

        var depots = workbook.Worksheet(VendorImportWorkbook.DepotsSheet);
        Assert.Equal("COR006", depots.Cell(2, 1).GetString());
        Assert.Equal("Cortina Graniteside Vending", depots.Cell(2, 2).GetString());
        Assert.Equal("VMP", depots.Cell(2, 4).GetString());
        Assert.Equal("VMP004", depots.Cell(2, 5).GetString());
        Assert.Equal("COR008", depots.Cell(3, 2).GetString()); // no name known: the code stands in
        Assert.Equal(XLColor.White, depots.Cell(1, 6).Style.Font.FontColor); // the Note header, not muted with its column
    }

    [Fact]
    public void The_depot_column_offers_the_depots_as_a_list()
    {
        using var workbook = new XLWorkbook(new MemoryStream(Template()));
        var vendors = workbook.Worksheet(VendorImportWorkbook.VendorsSheet);

        var validation = vendors.Cell(2, 2).GetDataValidation();
        Assert.Equal(XLAllowedValues.List, validation.AllowedValues);
        Assert.Contains("Depots", validation.Value);
        Assert.Contains("$A$3", validation.Value);
    }

    [Fact]
    public void The_template_is_a_valid_package()
    {
        var errors = Validate(Template());
        Assert.Empty(errors);
    }

    [Fact]
    public void The_package_validator_catches_a_broken_data_validation()
    {
        // Negative control for the test above: without it, "no errors" might only mean the validator
        // does not look at data validations.
        using var stream = new MemoryStream();
        stream.Write(Template());
        using (var document = SpreadsheetDocument.Open(stream, true))
        {
            var sheet = document.WorkbookPart!.WorksheetParts
                .Single(part => part.Worksheet.Descendants<DataValidation>().Any(v => v.Type?.Value == DataValidationValues.List));
            var validation = sheet.Worksheet.Descendants<DataValidation>().First(v => v.Type?.Value == DataValidationValues.List);
            validation.SequenceOfReferences = null;
            sheet.Worksheet.Save();
        }

        Assert.NotEmpty(Validate(stream.ToArray()));
    }

    [Fact]
    public void A_blank_template_has_no_vendors()
    {
        var result = Read(Template());

        Assert.True(result.IsError);
        Assert.Equal("Vending.FileHasNoVendors", result.FirstError.Code);
    }

    [Fact]
    public void Filled_rows_read_back_with_their_row_numbers_and_leading_zeros()
    {
        var filled = Fill(Template(), sheet =>
        {
            sheet.Cell(2, 1).Value = "VMB001";
            sheet.Cell(2, 3).Value = "Tendai";
            sheet.Cell(2, 4).Value = "Moyo";
            sheet.Cell(2, 5).Value = "0771234567";
            // Row 3 left blank, as happens when someone deletes a vendor out of the middle.
            sheet.Cell(4, 2).Value = "COR006";
            sheet.Cell(4, 3).Value = "  Rudo  ";
            sheet.Cell(4, 8).Value = "Stand 12, Graniteside";
        });

        var rows = Read(filled);

        Assert.False(rows.IsError, rows.IsError ? rows.FirstError.Description : null);
        Assert.Equal([2, 4], rows.Value.Select(row => row.RowNumber));

        var first = rows.Value[0];
        Assert.Equal("VMB001", first.Code);
        Assert.Null(first.Depot);
        Assert.Equal("Moyo", first.Surname);
        Assert.Equal("0771234567", first.Phone);

        var second = rows.Value[1];
        Assert.Null(second.Code);
        Assert.Equal("COR006", second.Depot);
        Assert.Equal("Rudo", second.Name);
        Assert.Equal("Stand 12, Graniteside", second.Address);
    }

    [Fact]
    public void Columns_are_found_by_header_not_position()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Sheet1");
        sheet.Cell(1, 1).Value = "Vendors for September";
        sheet.Cell(2, 1).Value = "Name";
        sheet.Cell(2, 2).Value = "Notes";
        sheet.Cell(2, 3).Value = "VENDOR CODE";
        sheet.Cell(3, 1).Value = "Tendai";
        sheet.Cell(3, 2).Value = "ignored";
        sheet.Cell(3, 3).Value = "vmp010";
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);

        var rows = Read(stream.ToArray());

        var row = Assert.Single(rows.Value);
        Assert.Equal(3, row.RowNumber);
        Assert.Equal("Tendai", row.Name);
        Assert.Equal("vmp010", row.Code); // passed on as typed; the API normalises and judges it
    }

    [Fact]
    public void A_sheet_that_is_not_the_template_is_refused()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Stock");
        sheet.Cell(1, 1).Value = "Item";
        sheet.Cell(2, 1).Value = "CHE011";
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);

        Assert.Equal("Vending.NotTheTemplate", Read(stream.ToArray()).FirstError.Code);
    }

    [Fact]
    public void A_file_that_is_not_a_workbook_is_refused()
    {
        Assert.Equal("Vending.FileUnreadable", Read("Code,Name\nVMB001,Tendai"u8.ToArray()).FirstError.Code);
    }

    private static List<ValidationErrorInfo> Validate(byte[] content)
    {
        using var document = SpreadsheetDocument.Open(new MemoryStream(content), false);
        return new OpenXmlValidator(FileFormatVersions.Microsoft365).Validate(document).ToList();
    }
}
