using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Validation;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// The invoice is validated by the same rules as every other sales document.
/// </summary>
/// <remarks>
/// It was not. Quotations, sales orders, transfers and purchase orders all went through
/// <see cref="UomQuantityValidation.ValidateAndNormalizeLineQuantitiesAsync"/>; the invoice had a
/// hand-rolled loop of its own. So the rule that only KG items may carry a fractional quantity was
/// enforced on every document except the one that actually moves the stock — a weighed quantity on
/// a whole-unit item reached SAP, was rounded there, and the invoice and the shelf disagreed by the
/// remainder.
///
/// <para>
/// The batch and serial selection rules had the opposite problem: one implementation, but sitting
/// so late that a caller met it as an <c>ArgumentException</c> thrown from inside the posting
/// client, after allocation and after the locks.
/// </para>
/// </remarks>
public sealed class InvoiceLineValidationParityTests
{
    // ---------------------------------------------------------------
    // The rule the invoice used to skip
    // ---------------------------------------------------------------

    [Fact]
    public async Task A_fractional_quantity_on_a_whole_unit_item_is_refused()
    {
        await using var context = await ContextWithProduct("BOX010", uom: "PC");

        var errors = await ValidateInvoiceLines(context, ("BOX010", 2.5m, "PC"));

        Assert.Contains(errors, e => e.Contains("not valid for unit 'PC'"));
    }

    [Fact]
    public async Task A_fractional_quantity_on_a_KG_item_is_allowed()
    {
        await using var context = await ContextWithProduct("CHE011", uom: "KG");

        // 1.234 kg of cheese is the case the whole rule exists to permit.
        var errors = await ValidateInvoiceLines(context, ("CHE011", 1.234m, "KG"));

        Assert.Empty(errors);
    }

    [Fact]
    public async Task A_whole_quantity_needs_no_unit_at_all()
    {
        await using var context = await ContextWithProduct("BOX010", uom: "PC");

        // The common case: no UoMCode sent, nothing fractional, nothing to complain about. This is
        // what keeps the rule from breaking every caller that has never sent a unit.
        var errors = await ValidateInvoiceLines(context, ("BOX010", 3m, null));

        Assert.Empty(errors);
    }

    [Fact]
    public async Task A_fractional_quantity_with_no_unit_anywhere_is_refused_rather_than_guessed()
    {
        // No Products row, no UoMCode on the line. The API's own Products table is never populated,
        // so this is the normal state for most items and the reason the web page now sends the unit.
        await using var context = EmptyContext();

        var errors = await ValidateInvoiceLines(context, ("UNKNOWN01", 2.5m, null));

        Assert.Contains(errors, e => e.Contains("requires a unit of measure"));
    }

    [Fact]
    public async Task The_line_keeps_the_unit_that_was_resolved_for_it()
    {
        await using var context = await ContextWithProduct("CHE011", uom: "KG");

        var request = Invoice(("CHE011", 1.234m, null));
        await ValidateInvoiceLines(context, request);

        // Normalisation is the other half of the helper's job: the line goes on to SAP carrying the
        // unit its quantity was judged against.
        Assert.Equal("KG", request.Lines![0].UoMCode);
    }

    // ---------------------------------------------------------------
    // Selection rules, now applied before the SAP client sees the document
    // ---------------------------------------------------------------

    [Fact]
    public void A_batch_selection_that_does_not_add_up_is_named_by_line()
    {
        var problems = UomQuantityValidation.DescribeLineSelectionProblems(
            lineIndex: 0,
            itemCode: "CHE011",
            quantity: 12m,
            batches: [("B-1", 5m), ("B-2", 4m)],
            serialNumbers: null).ToList();

        // SAP answers this with -4014, which names neither the line nor the item.
        Assert.Contains(problems, p => p.Contains("covers 9 of 12"));
        Assert.Contains(problems, p => p.Contains("Line 1"));
    }

    [Fact]
    public void A_batch_selection_that_adds_up_is_accepted()
    {
        var problems = UomQuantityValidation.DescribeLineSelectionProblems(
            0, "CHE011", 12m, [("B-1", 5m), ("B-2", 7m)], null).ToList();

        Assert.Empty(problems);
    }

    [Fact]
    public void One_serial_number_per_unit_is_required_and_duplicates_are_caught()
    {
        Assert.Contains(
            UomQuantityValidation.DescribeLineSelectionProblems(0, "SER01", 3m, null, ["S-1", "S-2"]),
            p => p.Contains("covers 2 of 3"));

        Assert.Contains(
            UomQuantityValidation.DescribeLineSelectionProblems(0, "SER01", 2m, null, ["S-1", "S-1"]),
            p => p.Contains("listed more than once"));

        Assert.Contains(
            UomQuantityValidation.DescribeLineSelectionProblems(0, "SER01", 1.5m, null, ["S-1"]),
            p => p.Contains("whole number of units"));
    }

    [Fact]
    public void A_line_carrying_no_selection_is_left_alone()
    {
        // Auto-allocation fills these in later. An empty selection is not an incomplete one.
        Assert.Empty(UomQuantityValidation.DescribeLineSelectionProblems(0, "CHE011", 12m, null, null));
        Assert.Empty(UomQuantityValidation.DescribeLineSelectionProblems(0, "CHE011", 12m, [], []));
    }

    // ---------------------------------------------------------------
    // The rule has one home
    // ---------------------------------------------------------------

    [Fact]
    public void Nothing_reimplements_the_batch_sum_rule()
    {
        // The plan for this phase asked for "a grep proving no call site builds its own quantity
        // check". A grep run once proves nothing a week later, so it lives here instead.
        //
        // The marker is the sentence the rule puts in its message. Two copies of it meant two
        // implementations that could drift, and the one in the SAP client was the only thing
        // enforcing it at all.
        var offenders = SourceFiles()
            .Where(file => File.ReadAllText(file).Contains(
                "SAP requires the batch quantities on a line to add up to the line quantity"))
            .Select(file => Path.GetFileName(file))
            .Where(name => name != "UomQuantityValidation.cs")
            .OrderBy(name => name)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "The batch-sum rule belongs in UomQuantityValidation.DescribeLineSelectionProblems. "
            + "These files state it themselves: " + string.Join(", ", offenders));
    }

    [Fact]
    public void The_invoice_handler_validates_its_lines_through_the_shared_helper()
    {
        // What this proves and what it does not: it pins that CreateInvoiceHandler routes line
        // validation through UomQuantityValidation and does not carry a quantity loop of its own.
        // It does not exercise the handler — that needs eleven dependencies stood up and nothing in
        // the suite does it yet — so the rules themselves are covered by the cases above, against
        // the helper the handler calls.
        //
        // Worth having anyway: the defect this phase fixed was not a wrong rule, it was a second
        // implementation quietly diverging from the first. That is a shape a source check catches.
        var handler = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "ShopInventory", "Features", "Invoices", "Commands", "CreateInvoice",
            "CreateInvoiceHandler.cs"));

        Assert.Contains("UomQuantityValidation.ValidateAndNormalizeLineQuantitiesAsync", handler);
        Assert.Contains("UomQuantityValidation.DescribeLineSelectionProblems", handler);

        // The hand-rolled loop that used to stand in for both.
        Assert.DoesNotContain("Quantity must be greater than zero. Current value:", handler);
    }

    [Fact]
    public void Issuable_stock_is_defined_once()
    {
        // "On hand less committed" decides whether a document may take stock. It was written out in
        // BatchInventoryValidationService and again in SAPServiceLayerClient; it now lives on the
        // DTO that carries the two numbers.
        var pattern = new Regex(@"\.InStock\s*-\s*\w*\.?Committed");

        var offenders = SourceFiles()
            .Where(file => pattern.IsMatch(File.ReadAllText(file)))
            .Select(Path.GetFileName)
            .Where(name => name != "StockDto.cs")
            .OrderBy(name => name)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Use StockQuantityDto.Issuable rather than restating it: " + string.Join(", ", offenders));
    }

    // ---------------------------------------------------------------

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(
            Path.Combine(RepositoryRoot(), "ShopInventory"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !file.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ShopInventory.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    /// <summary>
    /// Runs the shared line validation exactly as <c>CreateInvoiceHandler.ValidateLinesAsync</c>
    /// does, against the same selectors.
    /// </summary>
    private static Task<List<string>> ValidateInvoiceLines(
        ApplicationDbContext context,
        params (string ItemCode, decimal Quantity, string? UoMCode)[] lines)
        => ValidateInvoiceLines(context, Invoice(lines));

    private static Task<List<string>> ValidateInvoiceLines(
        ApplicationDbContext context,
        CreateInvoiceRequest request)
        => UomQuantityValidation.ValidateAndNormalizeLineQuantitiesAsync(
            context,
            request.Lines,
            line => line.ItemCode,
            line => line.Quantity,
            line => line.UoMCode,
            (line, uomCode) => line.UoMCode = uomCode,
            CancellationToken.None);

    private static CreateInvoiceRequest Invoice(
        params (string ItemCode, decimal Quantity, string? UoMCode)[] lines) => new()
    {
        CardCode = "C-1",
        DocCurrency = "USD",
        Lines = lines.Select(line => new CreateInvoiceLineRequest
        {
            ItemCode = line.ItemCode,
            Quantity = line.Quantity,
            UnitPrice = 5m,
            UoMCode = line.UoMCode,
            WarehouseCode = "KEFSHOP"
        }).ToList()
    };

    private static async Task<ApplicationDbContext> ContextWithProduct(string itemCode, string uom)
    {
        var context = EmptyContext();
        context.Products.Add(new ProductEntity
        {
            ItemCode = itemCode,
            ItemName = itemCode,
            InventoryUOM = uom
        });
        await context.SaveChangesAsync();
        return context;
    }

    private static ApplicationDbContext EmptyContext()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        var context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection)
                .Options);
        context.Database.EnsureCreated();
        return context;
    }
}
