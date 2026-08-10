using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.VanSalesCompatibility.Commands.IngestVanSalesOfflineSales;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// A van uploads its backlog whenever it finds signal. Every sale in that batch is finished business —
/// stamped with a ZIMRA receipt and printed hours earlier — so this endpoint takes custody of them and
/// holds them for the end-of-day posting run.
///
/// The two invariants worth the most: it must never fiscalise (the customer holds the receipt already),
/// and a retry must be answered rather than duplicated (a handset that lost the response will re-send).
/// </summary>
public sealed class VanSalesOfflineIngestTests : IDisposable
{
    private static readonly Guid VanUser = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public VanSalesOfflineIngestTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new ApplicationDbContext(options);
        _context.Database.EnsureCreated();

        _context.Users.Add(new User
        {
            Id = VanUser,
            Username = "van006",
            Email = "van006@example.com",
            PasswordHash = "x",
            Role = "Sales",
            IsActive = true,
            AssignedWarehouseCode = "VAN006",
            AssignedCostCentreCode = "CC006",
            // Stored as a JSON array, not a CSV — MobileAssignedCustomerScope deserializes it.
            AssignedCustomerCodes = """["SIM001","SIM002"]"""
        });
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private IngestVanSalesOfflineSalesHandler BuildHandler() =>
        new(_context, NullLogger<IngestVanSalesOfflineSalesHandler>.Instance);

    private async Task<VanSalesOfflineSaleBatchResponse> IngestAsync(params VanSalesOfflineSaleRequest[] sales)
    {
        var result = await BuildHandler().Handle(
            new IngestVanSalesOfflineSalesCommand(
                new VanSalesOfflineSaleBatchRequest { Sales = [.. sales] }, VanUser),
            CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        return result.Value;
    }

    /// <summary>
    /// The whole point of the endpoint: the sale lands as held work for tonight, already marked fiscal so
    /// nothing downstream tries to stamp it again.
    /// </summary>
    [Fact]
    public async Task An_uploaded_sale_is_held_as_already_fiscalised()
    {
        var response = await IngestAsync(BuildSale("VAN006-INV-20260810-AAA111"));

        Assert.Equal(1, response.Accepted);

        var sale = await _context.DesktopSales.Include(s => s.Lines).SingleAsync();
        Assert.Equal(SaleSourceSystems.VanSales, sale.SourceSystem);
        Assert.Equal(DesktopSaleConsolidationStatus.Pending, sale.ConsolidationStatus);

        // Success, not Pending: re-fiscalising a printed receipt can only be undone with a manual
        // credit note, so nothing downstream may treat this as still needing a stamp.
        Assert.Equal(DesktopSaleFiscalizationStatus.Success, sale.FiscalizationStatus);
        Assert.Equal(501, sale.ReceiptGlobalNo);
        Assert.Equal("VAN006", sale.WarehouseCode);
        Assert.Equal("CC006", sale.CostCentreCode);
        Assert.Single(sale.Lines);
    }

    /// <summary>
    /// The trading day comes from the handset. A sale made at 22:00 Monday and uploaded Tuesday morning
    /// belongs to Monday — that is the day its fiscal receipt is in, and the day it must post against.
    /// </summary>
    [Fact]
    public async Task The_trading_day_comes_from_the_handset_not_the_upload()
    {
        var sale = BuildSale("VAN006-INV-20260810-AAA111");
        sale.SoldAt = new DateTime(2026, 8, 10, 22, 15, 0, DateTimeKind.Unspecified);

        await IngestAsync(sale);

        var stored = await _context.DesktopSales.SingleAsync();
        Assert.Equal(new DateTime(2026, 8, 10), stored.DocDate);
    }

    /// <summary>
    /// A handset that never saw the response re-sends. That must be answered as a duplicate — a success
    /// from its point of view, so it clears its queue — and must not create a second row.
    /// </summary>
    [Fact]
    public async Task A_resent_sale_is_reported_as_a_duplicate_and_not_stored_twice()
    {
        await IngestAsync(BuildSale("VAN006-INV-20260810-AAA111"));

        var second = await IngestAsync(BuildSale("VAN006-INV-20260810-AAA111"));

        Assert.Equal(0, second.Accepted);
        Assert.Equal(1, second.Duplicates);
        Assert.Equal("duplicate", second.Results.Single().Status);
        Assert.Equal(1, await _context.DesktopSales.CountAsync());
    }

    /// <summary>
    /// A partially delivered batch is re-sent whole. The overlap has to be tolerated per row, or the
    /// unique index turns the retry into a batch-wide failure and the van can never drain its queue.
    /// </summary>
    [Fact]
    public async Task A_batch_mixing_new_and_already_received_sales_stores_only_the_new_ones()
    {
        await IngestAsync(BuildSale("VAN006-INV-20260810-AAA111"));

        var response = await IngestAsync(
            BuildSale("VAN006-INV-20260810-AAA111"),
            BuildSale("VAN006-INV-20260810-BBB222", receiptGlobalNo: 502));

        Assert.Equal(1, response.Accepted);
        Assert.Equal(1, response.Duplicates);
        Assert.Equal(2, await _context.DesktopSales.CountAsync());
    }

    /// <summary>
    /// The same reference twice inside one payload would otherwise only fail at SaveChanges, taking the
    /// whole batch — including every good sale — down with it.
    /// </summary>
    [Fact]
    public async Task A_reference_repeated_within_one_batch_is_stored_once()
    {
        var response = await IngestAsync(
            BuildSale("VAN006-INV-20260810-AAA111"),
            BuildSale("VAN006-INV-20260810-AAA111"));

        Assert.Equal(1, response.Accepted);
        Assert.Equal(1, response.Duplicates);
        Assert.Equal(1, await _context.DesktopSales.CountAsync());
    }

    /// <summary>
    /// A van's backlog is a day's takings. One malformed sale must be reported and skipped, not used as
    /// a reason to refuse everything behind it.
    /// </summary>
    [Fact]
    public async Task One_rejected_sale_does_not_block_the_rest_of_the_batch()
    {
        var bad = BuildSale("VAN006-INV-20260810-BBB222", receiptGlobalNo: 502);
        bad.Items = [];

        var response = await IngestAsync(
            BuildSale("VAN006-INV-20260810-AAA111"),
            bad,
            BuildSale("VAN006-INV-20260810-CCC333", receiptGlobalNo: 503));

        Assert.Equal(2, response.Accepted);
        Assert.Equal(1, response.Rejected);
        Assert.Equal(2, await _context.DesktopSales.CountAsync());
    }

    /// <summary>
    /// The receipt's global number is the only durable link back to the ZIMRA receipt the customer holds.
    /// Without it the SAP invoice this becomes can never be reconciled against FDMS.
    /// </summary>
    [Fact]
    public async Task A_sale_with_no_receipt_number_is_rejected()
    {
        var sale = BuildSale("VAN006-INV-20260810-AAA111");
        sale.ReceiptGlobalNo = null;

        var response = await IngestAsync(sale);

        Assert.Equal(1, response.Rejected);
        Assert.Contains("receipt_global_no", response.Results.Single().Message);
        Assert.Empty(_context.DesktopSales);
    }

    /// <summary>A van may only invoice the customers assigned to it, offline capture included.</summary>
    [Fact]
    public async Task A_sale_against_an_unassigned_customer_is_rejected()
    {
        var sale = BuildSale("VAN006-INV-20260810-AAA111");
        sale.CustomerCode = "OTHER001";

        var response = await IngestAsync(sale);

        Assert.Equal(1, response.Rejected);
        Assert.Contains("not assigned", response.Results.Single().Message);
    }

    /// <summary>The idempotency key is not optional — everything downstream keys off it.</summary>
    [Fact]
    public async Task A_sale_with_no_reference_is_rejected()
    {
        var sale = BuildSale("VAN006-INV-20260810-AAA111");
        sale.VanOrder = "   ";

        var response = await IngestAsync(sale);

        Assert.Equal(1, response.Rejected);
        Assert.Empty(_context.DesktopSales);
    }

    private static VanSalesOfflineSaleRequest BuildSale(string reference, int receiptGlobalNo = 501) => new()
    {
        VanOrder = reference,
        CustomerCode = "SIM001",
        CustomerName = "Simbisa",
        SoldAt = new DateTime(2026, 8, 10, 11, 30, 0, DateTimeKind.Unspecified),
        Currency = "USD",
        Total = 100m,
        VatAmount = 13.04m,
        AmountPaid = 100m,
        PaymentMethod = "Cash",
        FiscalDeviceId = "35410",
        FiscalDayNo = 19,
        ReceiptGlobalNo = receiptGlobalNo,
        ReceiptCounter = 4,
        VerificationCode = "A1B2C3D4E5F60718",
        QrCode = "https://fdms.example/verify/000003541010082026000000050 1A1B2C3D4E5F60718",
        Items =
        [
            new VanSalesOfflineSaleItemRequest
            {
                Code = "CHE011",
                Description = "Cheese 1kg",
                Quantity = 2m,
                Price = 50m
            }
        ]
    };
}
