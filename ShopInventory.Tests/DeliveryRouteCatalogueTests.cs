using ClosedXML.Excel;
using ShopInventory.Web.Common;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The delivery routes are generated into DeliveryRoutes.g.cs from the 2026
/// routes workbook, with every business partner code resolved against the SAP
/// customer master -- the workbook's own code column carries 28 codes that name
/// no partner in SAP and one that names a shop in the wrong province.
///
/// These hold the properties the POD report's route filter depends on: that a
/// code resolves to the route the workbook puts it on, that a shop's currency
/// variants all resolve together, and that a shop the workbook never listed
/// comes back unrouted rather than falling into someone else's route.
/// </summary>
public sealed class DeliveryRouteCatalogueTests
{
    [Fact]
    public void The_catalogue_is_not_empty()
    {
        Assert.NotEmpty(DeliveryRoutes.All);
        Assert.Equal(DeliveryRoutes.All.Count, DeliveryRoutes.Names.Count);
        Assert.All(DeliveryRoutes.All, route => Assert.NotEmpty(route.CardCodes));
        Assert.All(DeliveryRoutes.All, route => Assert.NotEmpty(route.Days));
    }

    [Fact]
    public void Every_route_name_is_distinct()
    {
        var distinct = DeliveryRoutes.Names.Distinct(StringComparer.OrdinalIgnoreCase).Count();
        Assert.Equal(DeliveryRoutes.Names.Count, distinct);
    }

    [Theory]
    // The workbook's own codes, confirmed against the SAP customer master.
    [InlineData("TMP092", "PNP NORTH")]      // Pick n Pay Westgate USD
    [InlineData("SPA059 USD", "WEST 2")]     // SPAR Athienitis -- code exists only with the suffix
    [InlineData("BHO002 USD", "WEST 1")]     // Bhola Mega Mart Longchen Plaza
    [InlineData("NRI037", "CBD1-CHITUNGWIZA")]
    [InlineData("GAI115", "MSASA")]          // Metro Hypermarket Msasa
    [InlineData("LAN008", "MVURWI-BIND/MTOKO")]
    public void A_workbook_code_resolves_to_its_route(string cardCode, string route)
    {
        Assert.True(DeliveryRoutes.IsOnRoute(cardCode, route),
            $"{cardCode} resolved to [{DeliveryRoutes.FormatRoutes(cardCode)}] rather than {route}");
    }

    [Theory]
    // Resolved by name because the workbook left the code column empty.
    [InlineData("BHO015", "WEST 1")]         // Bhola Supermarket Bishop Gaul
    [InlineData("ASS006", "CBD2")]           // AMP Meats (Coventry)
    [InlineData("LAN017", "KARIBA")]         // Megasave Chinhoyi
    [InlineData("SAV001", "MARONDERA-CHIPINGE")]
    [InlineData("TMP011", "PNP CENTRAL")]    // TM Kenneth Kaunda Ave
    public void A_stop_with_no_code_in_the_workbook_still_resolves(string cardCode, string route)
    {
        Assert.True(DeliveryRoutes.IsOnRoute(cardCode, route),
            $"{cardCode} resolved to [{DeliveryRoutes.FormatRoutes(cardCode)}] rather than {route}");
    }

    /// <summary>
    /// The workbook codes this stop tmp013, which SAP holds as TM Budiriro in
    /// Harare -- a different shop in a different province from the TM Chiredzi
    /// the workbook names beside it. The generator resolves the name, not the
    /// code, so the Midlands route must not pick up the Harare shop.
    /// </summary>
    [Fact]
    public void The_workbooks_wrong_code_for_TM_Chiredzi_is_not_used()
    {
        Assert.True(DeliveryRoutes.IsOnRoute("TMP039", "MIDLANDS 2"));
        Assert.False(DeliveryRoutes.IsOnRoute("TMP013", "MIDLANDS 2"));
    }

    /// <summary>
    /// A shop holds one code per currency, and an invoice carries whichever code
    /// matches its own currency. Filtering by route has to catch all of them or
    /// it silently drops a shop's USD invoices because the workbook happened to
    /// name its ZiG code.
    /// </summary>
    [Theory]
    // The currency sits in the suffix on some codes and only in the name on
    // others -- SPA050 carries " USD", OKZ104 does not -- so both shapes matter.
    [InlineData("OKZ049", "OKZ104", "OKZ159")]                // OK Hwange
    [InlineData("SPA002", "SPA050 USD", "SPA070")]            // Spar Bridge
    public void Every_currency_variant_of_a_shop_lands_on_the_same_route(
        string first, string second, string third)
    {
        var routes = DeliveryRoutes.GetRoutes(first);
        Assert.NotEmpty(routes);
        Assert.Equal(routes, DeliveryRoutes.GetRoutes(second));
        Assert.Equal(routes, DeliveryRoutes.GetRoutes(third));
    }

    /// <summary>
    /// Shops the workbook omits that were added on top of it, each one in a town
    /// the route demonstrably already stops in. The workbook gives
    /// MARONDERA-CHIPINGE no Marondera stop at all despite naming the town.
    /// </summary>
    [Theory]
    [InlineData("BHO023", "MARONDERA-CHIPINGE")]   // Bhola Marondera
    [InlineData("TMP089", "MARONDERA-CHIPINGE")]   // TM Main Street Marondera
    [InlineData("LAN013", "MARONDERA-CHIPINGE")]   // Megasave Marondera
    [InlineData("BHO013", "MARONDERA-CHIPINGE")]   // Bhola Mutare Town
    [InlineData("GAI118", "MARONDERA-CHIPINGE")]   // Gain Checheche, Chipinge district
    [InlineData("GAI114", "MIDLANDS 1")]           // Metro Peech Kwekwe
    [InlineData("SPA075", "MIDLANDS 1")]           // Spar Express Kadoma
    [InlineData("GAI054", "MIDLANDS 2")]           // Gains Chiredzi
    [InlineData("GAI110", "MIDLANDS 2")]           // Gains Triangle
    public void A_shop_the_workbook_omits_is_added_to_the_route_serving_its_town(
        string cardCode, string route)
    {
        Assert.True(DeliveryRoutes.IsOnRoute(cardCode, route),
            $"{cardCode} resolved to [{DeliveryRoutes.FormatRoutes(cardCode)}] rather than {route}");
    }

    /// <summary>
    /// Two shops sit in towns that two routes both stop in, so the town cannot
    /// place them. The routes run on different days and their own stops invoice
    /// overwhelmingly on that day, which breaks the tie. Both samples are small
    /// (5 and 3 invoices), so these are the first to revisit if a drop moves.
    /// </summary>
    [Theory]
    // Zvishavane: MIDLANDS 1 stops at TM Zvishavane, MIDLANDS 2 at NR Zvishavane.
    [InlineData("GAI080", "MIDLANDS 2", "MIDLANDS 1")]
    // Chinhoyi: KARIBA stops at TM/NR Chinhoyi, PNP CENTRAL at Bhola Chinhoyi.
    [InlineData("GAI026", "KARIBA", "PNP CENTRAL")]
    public void A_shop_in_a_town_two_routes_serve_lands_on_the_one_its_invoices_match(
        string cardCode, string expected, string rejected)
    {
        Assert.True(DeliveryRoutes.IsOnRoute(cardCode, expected),
            $"{cardCode} resolved to [{DeliveryRoutes.FormatRoutes(cardCode)}]");
        Assert.False(DeliveryRoutes.IsOnRoute(cardCode, rejected));
    }

    /// <summary>
    /// "Cheese Galore ( Packaging)" is a supplier as well as a customer, so a
    /// partner dump that was not filtered to customers puts supplier codes on a
    /// delivery route. The generator filters, but this pins the result.
    /// </summary>
    [Theory]
    [InlineData("CHE008")]
    [InlineData("CHE011")]
    public void A_supplier_code_never_reaches_a_route(string cardCode) =>
        Assert.Empty(DeliveryRoutes.GetRoutes(cardCode));

    /// <summary>
    /// No route stops anywhere in Matabeleland -- the workbook's BULAWAYO route
    /// is one 24T run to the depot, which distributes locally. These shops are
    /// deliberately unrouted; routing them would claim a Harare truck calls at
    /// each one.
    /// </summary>
    [Theory]
    [InlineData("TMP114")]      // TM Lobengula
    [InlineData("TMP110")]      // Pick n Pay Bradfield
    [InlineData("TMP128")]      // Pick n Pay Gwanda
    [InlineData("FAZ002 USD")]  // Fazak Home & Hyper
    public void Matabeleland_shops_stay_unrouted(string cardCode) =>
        Assert.Empty(DeliveryRoutes.GetRoutes(cardCode));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("CIS006")]      // Kefalos Shop -- an internal account, never on a truck
    [InlineData("VAN009")]      // Van sales
    [InlineData("TMP110 ")]     // Pick n Pay Bradfield, served off the Bulawayo depot
    public void A_partner_the_workbook_never_listed_is_unrouted(string? cardCode)
    {
        Assert.Empty(DeliveryRoutes.GetRoutes(cardCode));
        Assert.Equal(string.Empty, DeliveryRoutes.FormatRoutes(cardCode));
    }

    /// <summary>
    /// Codes arrive from SAP with the currency as a suffix, so the lookup has to
    /// survive the spacing and casing an invoice or a hand-typed filter carries.
    /// </summary>
    [Theory]
    [InlineData("SPA059 USD")]
    [InlineData("spa059 usd")]
    [InlineData("  SPA059   USD  ")]
    public void The_lookup_is_indifferent_to_spacing_and_case(string cardCode) =>
        Assert.True(DeliveryRoutes.IsOnRoute(cardCode, "WEST 2"));

    [Fact]
    public void A_route_label_names_the_day_it_runs()
    {
        Assert.Equal("BORROWDALE (Tue)", DeliveryRoutes.GetLabel("BORROWDALE"));
        // The workbook runs Bulawayo twice a week, in two separate columns.
        Assert.Equal("BULAWAYO (Mon/Fri)", DeliveryRoutes.GetLabel("BULAWAYO"));
        Assert.Equal(string.Empty, DeliveryRoutes.GetLabel(null));
        Assert.Equal("NOT A ROUTE", DeliveryRoutes.GetLabel("NOT A ROUTE"));
    }

    /// <summary>
    /// A shop can be called on twice a week, so the filter is a membership test
    /// rather than a label. If this ever collapses to one route per partner the
    /// filter would start hiding invoices from the route it did not pick.
    /// </summary>
    [Fact]
    public void A_partner_may_sit_on_more_than_one_route()
    {
        var routes = DeliveryRoutes.GetRoutes("ASS006");   // AMP Meats (Coventry)
        Assert.True(routes.Count > 1, $"expected more than one route, got [{string.Join(", ", routes)}]");
    }

    /// <summary>
    /// The route sits between Card Code and Invoice Date on every POD sheet that
    /// lists invoices. The column was inserted rather than appended, so this
    /// reads the produced bytes back to confirm nothing downstream of it shifted
    /// out from under its header.
    /// </summary>
    [Fact]
    public void The_exported_workbook_carries_the_route_beside_the_card_code()
    {
        var report = new PodUploadStatusReport
        {
            FromDate = "2026-08-01",
            ToDate = "2026-08-20",
            TotalInvoices = 2,
            UploadedCount = 1,
            PendingCount = 1,
            CreditNoteDataComplete = true,
            Items =
            [
                // CreatedLocation is required: ApplyPodReportingScope drops an
                // invoice with no generated location before the sheet is built.
                new PodUploadStatusItem
                {
                    DocEntry = 1, DocNum = 5001, DocDate = "2026-08-04",
                    CardCode = "SPA059 USD", CardName = "SPAR Athienitis",
                    DocTotal = 120.50m, DocCurrency = "USD", CreatedLocation = "Cheeseman",
                    HasPod = true, HasProductPod = true, ProductPodCount = 1, PodCount = 1,
                    PodUploadedAt = new DateTime(2026, 8, 5, 9, 30, 0, DateTimeKind.Utc)
                },
                new PodUploadStatusItem
                {
                    DocEntry = 2, DocNum = 5002, DocDate = "2026-08-06",
                    CardCode = "ABS006", CardName = "Absolute Refregiration",
                    DocTotal = 80m, DocCurrency = "USD", CreatedLocation = "Cheeseman"
                }
            ]
        };

        var bytes = new ReportExportService().ExportPodUploadStatusToExcel(report);

        using var stream = new MemoryStream(bytes);
        using var workbook = new XLWorkbook(stream);

        foreach (var sheetName in new[] { "Product Invoices", "Pending PODs", "Uploaded PODs" })
        {
            var sheet = workbook.Worksheets.Worksheet(sheetName);
            var headerRow = FindHeaderRow(sheet);

            Assert.Equal("Card Code", sheet.Cell(headerRow, 3).GetString());
            Assert.Equal("Delivery Route", sheet.Cell(headerRow, 4).GetString());
            Assert.Equal("Invoice Date", sheet.Cell(headerRow, 5).GetString());

            // The columns after the insert must still sit under their own header.
            var headers = Enumerable.Range(1, sheet.LastColumnUsed()!.ColumnNumber())
                .Select(column => sheet.Cell(headerRow, column).GetString())
                .ToList();
            Assert.Contains("TOTAL", headers);
            Assert.Equal("TOTAL", headers[^1]);
        }

        var product = workbook.Worksheets.Worksheet("Product Invoices");
        var productHeader = FindHeaderRow(product);

        // Row order follows the report, so the routed invoice is first.
        Assert.Equal("5001", product.Cell(productHeader + 1, 1).GetString());
        Assert.Equal("SPA059 USD", product.Cell(productHeader + 1, 3).GetString());
        Assert.Equal("WEST 2", product.Cell(productHeader + 1, 4).GetString());

        // A shop the workbook never placed on a truck says so rather than
        // borrowing the route of whatever sits next to it.
        Assert.Equal("5002", product.Cell(productHeader + 2, 1).GetString());
        Assert.Equal("-", product.Cell(productHeader + 2, 4).GetString());

        // The insert must not have pushed a value out from under its header.
        Assert.Equal(new DateTime(2026, 8, 4), product.Cell(productHeader + 1, 5).GetDateTime());
        Assert.Equal("120.5", product.Cell(productHeader + 1, 7).GetString());
        Assert.Equal("Uploaded", product.Cell(productHeader + 1, 8).GetString());
    }

    private static int FindHeaderRow(IXLWorksheet sheet)
    {
        for (var row = 1; row <= sheet.LastRowUsed()!.RowNumber(); row++)
        {
            if (sheet.Cell(row, 1).GetString() == "Invoice #")
            {
                return row;
            }
        }

        throw new Xunit.Sdk.XunitException($"no header row on {sheet.Name}");
    }
}
