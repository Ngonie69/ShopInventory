using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Sync.Commands.SyncItemTaxGroups;
using ShopInventory.Features.VanSalesCompatibility;
using ShopInventory.Models;
using ShopInventory.Services;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the HS code a receipt line declares, which SAP holds in <c>OITM.FrgnName</c>.
///
/// Every receipt this app built itself used to declare <c>Fiscalisation:DefaultHsCode</c> on every
/// line, so a till sale of a Jumbo bun and two Minute Maid juices went to ZIMRA as three lines of
/// 04031000, yoghurt. The platform already read FrgnName for the documents it fiscalises out of SAP;
/// the till and the van did not.
/// </summary>
public sealed class ItemHsCodeTests : IDisposable
{
    private const string DefaultHsCode = "04031000";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public ItemHsCodeTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private Task<SyncItemTaxGroupsHandler.ApplyOutcome> SyncAsync(params (string Item, string? ForeignName)[] rows) =>
        SyncItemTaxGroupsHandler.ApplyAsync(
            _context,
            rows.ToDictionary(
                r => r.Item, r => new SapItemTaxMaster("O01", r.ForeignName), StringComparer.OrdinalIgnoreCase),
            DateTime.UtcNow,
            NullLogger.Instance,
            CancellationToken.None);

    private string? StoredHsCode(string itemCode)
    {
        _context.ChangeTracker.Clear();
        return _context.SapItemTaxGroups.Single(row => row.ItemCode == itemCode).HsCode;
    }

    // ---- reading FrgnName -------------------------------------------------------------------

    [Theory]
    [InlineData("19059000", "19059000")]
    [InlineData(" 22029900 ", "22029900")]
    [InlineData("0403", "0403")]
    // A leading zero lost on the way into SAP, padded the way the platform pads it.
    [InlineData("4031000", "04031000")]
    [InlineData("403", "0403")]
    public void A_foreign_name_that_is_an_HS_code_is_kept(string foreignName, string expected)
    {
        Assert.Equal(expected, ItemHsCodes.Normalize(foreignName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // FDMS refuses anything that is not 4 or 8 digits (RCPT048), so none of these is stored.
    [InlineData("Minute Maid Guava")]
    [InlineData("1905.90.00")]
    [InlineData("190590001")]
    public void A_foreign_name_that_is_not_an_HS_code_is_not(string? foreignName)
    {
        Assert.Null(ItemHsCodes.Normalize(foreignName));
    }

    // ---- the stored copy --------------------------------------------------------------------

    [Fact]
    public async Task The_sync_stores_each_items_own_HS_code()
    {
        await SyncAsync(("BUN001", "19059000"), ("MMD400G", "22029900"), ("NOHS01", null));

        Assert.Equal("19059000", StoredHsCode("BUN001"));
        Assert.Equal("22029900", StoredHsCode("MMD400G"));
        Assert.Null(StoredHsCode("NOHS01"));
    }

    [Fact]
    public async Task A_code_changed_in_SAP_replaces_the_stored_one()
    {
        await SyncAsync(("BUN001", "04031000"));

        var outcome = await SyncAsync(("BUN001", "19059000"));

        Assert.Equal(1, outcome.HsCodesChanged);
        Assert.Equal("19059000", StoredHsCode("BUN001"));
    }

    [Fact]
    public async Task A_code_cleared_in_SAP_clears_the_stored_one()
    {
        await SyncAsync(("BUN001", "19059000"));

        await SyncAsync(("BUN001", null));

        Assert.Null(StoredHsCode("BUN001"));
    }

    [Fact]
    public async Task The_lookup_answers_only_the_items_that_have_a_code()
    {
        await SyncAsync(("BUN001", "19059000"), ("NOHS01", null));

        var resolved = await new ItemHsCodes(_context, NullLogger<ItemHsCodes>.Instance)
            .ResolveAsync(["BUN001", "NOHS01", "UNKNOWN", null], CancellationToken.None);

        Assert.Equal("19059000", Assert.Single(resolved).Value);
        Assert.True(resolved.ContainsKey("bun001"));
    }

    // ---- what the till declares -------------------------------------------------------------

    [Fact]
    public async Task A_till_receipt_declares_each_items_own_HS_code()
    {
        await SyncAsync(("BUN001", "19059000"), ("MMD400T", "22029900"));

        SubmitReceiptApiRequest? submitted = null;
        var client = StubProxy.For<IFiscalisationApiClient>((method, args) =>
        {
            if (method.Name == nameof(IFiscalisationApiClient.SubmitReceiptAsync))
            {
                submitted = (SubmitReceiptApiRequest)args![0]!;
                return Task.FromResult(new SubmitReceiptApiResponse());
            }

            throw new InvalidOperationException($"Unexpected call to {method.Name}");
        });

        var service = new FiscalizationService(
            client,
            StubProxy.For<IFiscalDeviceConfigCache>((_, _) => Task.FromResult<FiscalConfigApiResponse?>(null)),
            Options.Create(new FiscalisationSettings
            {
                Enabled = true,
                DefaultTaxId = 515,
                DefaultHsCode = DefaultHsCode
            }),
            Options.Create(new TaxSettings()),
            NullLogger<FiscalizationService>.Instance,
            new ItemHsCodes(_context, NullLogger<ItemHsCodes>.Instance));

        await service.FiscalizePreSapInvoiceAsync(
            new InvoiceDto
            {
                DocDate = "2026-09-30",
                DocCurrency = "USD",
                DocTotal = 2.50m,
                Lines =
                [
                    new InvoiceLineDto { LineNum = 1, ItemCode = "BUN001", ItemDescription = "Jumbo bun", Quantity = 1m, GrossPrice = 0.50m },
                    new InvoiceLineDto { LineNum = 2, ItemCode = "MMD400T", ItemDescription = "MINUTE MAID DELIGHT Tropical 400 ml", Quantity = 1m, GrossPrice = 1.00m },
                    new InvoiceLineDto { LineNum = 3, ItemCode = "NOHS01", ItemDescription = "Not in the item master", Quantity = 1m, GrossPrice = 1.00m }
                ]
            },
            "KEF-FAC-20260930-HSCODE");

        Assert.NotNull(submitted);
        Assert.Equal(["19059000", "22029900", DefaultHsCode], submitted.Lines.Select(line => line.HsCode));
    }

    // ---- what a van handset is told ---------------------------------------------------------

    [Fact]
    public void A_van_lease_carries_each_items_own_HS_code()
    {
        var itemTaxes = VanSalesFiscalLeaseMapper.BuildItemTaxes(
            new Dictionary<string, string> { ["BUN001"] = "O01", ["NOHS01"] = "O01" },
            new FiscalisationSettings
            {
                DefaultTaxId = 515,
                DefaultHsCode = DefaultHsCode
            },
            [new VanSalesFiscalTaxDto { TaxId = 515, Percent = 15.5m }],
            out _,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["bun001"] = "19059000" });

        Assert.Equal("19059000", itemTaxes.Single(item => item.ItemCode == "BUN001").HsCode);
        Assert.Equal(DefaultHsCode, itemTaxes.Single(item => item.ItemCode == "NOHS01").HsCode);
    }
}
