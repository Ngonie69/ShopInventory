using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.VanSalesCompatibility.Commands.IngestVanSalesOfflineSales;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

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
    private readonly RecordingAuditService _audit = new();

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
        new(
            _context,
            _audit,
            Options.Create(new FiscalisationSettings()),
            NullLogger<IngestVanSalesOfflineSalesHandler>.Instance);

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
    /// The unit a line was sold in reaches the stored line.
    /// </summary>
    /// <remarks>
    /// It is what makes a line's quantity totallable at all. <c>VanSaleLineFact</c> says so in as many
    /// words — sum quantity across items without it and eaches are added to kilograms — so a van report
    /// built on a null UoM produces a figure that looks like a number and is not one. The handset was
    /// dropping the value the product endpoints already send it, and there was no field here to put it
    /// in even if it had not.
    /// </remarks>
    [Fact]
    public async Task The_unit_a_line_was_sold_in_is_stored()
    {
        var sale = BuildSale("VAN006-INV-20260810-AAA111");
        sale.Items[0].UoMCode = "KG";

        await IngestAsync(sale);

        var line = await _context.DesktopSaleLines.SingleAsync();
        Assert.Equal("KG", line.UoMCode);
    }

    /// <summary>
    /// A discount is recorded against the line and never applied to it.
    /// </summary>
    /// <remarks>
    /// <c>Price</c> is the tax-inclusive unit price the device signed and is already net of the
    /// discount. Recomputing the line total from the percentage would restate a figure ZIMRA holds a
    /// signature over, and the platform refuses a receipt whose recomputed payload does not hash to
    /// what was signed. So the percentage is history, and the money is left exactly as it arrived.
    /// </remarks>
    [Fact]
    public async Task A_discount_is_recorded_without_restating_the_signed_price()
    {
        var sale = BuildSale("VAN006-INV-20260810-AAA111");
        sale.Items[0].DiscountPercent = 10m;

        await IngestAsync(sale);

        var line = await _context.DesktopSaleLines.SingleAsync();
        Assert.Equal(10m, line.DiscountPercent);

        // 2 x 50.00, exactly as sent. Not 90.00, which is what applying the percentage would give.
        Assert.Equal(50m, line.UnitPrice);
        Assert.Equal(100m, line.LineTotal);
    }

    /// <summary>
    /// A handset that predates these two fields is not a handset that sold in no unit at a full
    /// discount. Absent reads as absent.
    /// </summary>
    [Fact]
    public async Task An_older_handset_reports_no_unit_and_no_discount()
    {
        await IngestAsync(BuildSale("VAN006-INV-20260810-AAA111"));

        var line = await _context.DesktopSaleLines.SingleAsync();
        Assert.Null(line.UoMCode);
        Assert.Equal(0m, line.DiscountPercent);
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

        // The receipt as signed. Without it the sale still posts to SAP, but ZIMRA never receives the
        // receipt the customer is holding — so it belongs in the default fixture, not only in the test
        // that names it.
        ReceiptDate = new DateTime(2026, 8, 10, 11, 30, 0, DateTimeKind.Unspecified),
        FiscalDayOpenedAt = new DateTime(2026, 8, 10, 6, 15, 0, DateTimeKind.Unspecified),
        PreviousReceiptHash = "cGJ2aW91c2hhc2g=",
        DeviceSignatureHash = "aGFzaC1vZi10aGUtcGF5bG9hZA==",
        DeviceSignatureValue = "c2lnbmF0dXJlLW92ZXItdGhlLXBheWxvYWQ=",
        Items =
        [
            new VanSalesOfflineSaleItemRequest
            {
                Code = "CHE011",
                Description = "Cheese 1kg",
                Quantity = 2m,
                Price = 50m,
                TaxCode = "15.5% Output VAT USD",
                TaxId = 517,
                TaxPercent = 15.5m,
                HsCode = "04031000"
            }
        ]
    };

    /// <summary>
    /// The receipt has to survive the upload intact, because handing it to the fiscalisation platform is
    /// the only route by which ZIMRA ever learns this sale happened. Nothing here may be re-derived: the
    /// platform rebuilds the signed payload from exactly these values.
    /// </summary>
    [Fact]
    public async Task The_signed_receipt_is_stored_and_queued_for_zimra()
    {
        await IngestAsync(BuildSale("VAN006-INV-20260810-AAA111"));

        var sale = await _context.DesktopSales.Include(s => s.Lines).SingleAsync();

        Assert.Equal(DesktopSaleReceiptIngestStatus.Pending, sale.ReceiptIngestStatus);
        Assert.Equal(new DateTime(2026, 8, 10, 11, 30, 0), sale.ReceiptDate);
        Assert.Equal(new DateTime(2026, 8, 10, 6, 15, 0), sale.FiscalDayOpenedAt);
        Assert.Equal("cGJ2aW91c2hhc2g=", sale.PreviousReceiptHash);
        Assert.Equal("aGFzaC1vZi10aGUtcGF5bG9hZA==", sale.DeviceSignatureHash);
        Assert.Equal("c2lnbmF0dXJlLW92ZXItdGhlLXBheWxvYWQ=", sale.DeviceSignatureValue);

        // The tax the line was signed under, not whatever the catalogue says later.
        var line = sale.Lines.Single();
        Assert.Equal(517, line.TaxId);
        Assert.Equal(15.5m, line.TaxPercent);
        Assert.Equal("04031000", line.HsCode);
    }

    /// <summary>
    /// A sale that arrives without a signature is a receipt that can never be submitted. It is still
    /// accepted — the customer paid, and refusing the upload would strand the takings on the handset as
    /// well as losing the receipt — but it is marked as needing a person rather than left looking normal.
    /// </summary>
    [Fact]
    public async Task A_sale_with_no_signature_is_accepted_but_flagged_as_unsubmittable()
    {
        var sale = BuildSale("VAN006-INV-20260810-AAA111");
        sale.DeviceSignatureHash = null;
        sale.DeviceSignatureValue = null;

        var response = await IngestAsync(sale);

        Assert.Equal(1, response.Accepted);
        Assert.Contains("cannot be submitted to ZIMRA", response.Results.Single().Message);

        var stored = await _context.DesktopSales.SingleAsync();
        Assert.Equal(DesktopSaleReceiptIngestStatus.Unsignable, stored.ReceiptIngestStatus);

        // Still held for posting: the money is real whatever the fiscal side says.
        Assert.Equal(DesktopSaleConsolidationStatus.Pending, stored.ConsolidationStatus);
    }

    /// <summary>
    /// A day's takings used to arrive as one anonymous POST. The batch row is what makes an upload
    /// answerable afterwards: how much arrived, and whether any of it was turned away.
    /// </summary>
    [Fact]
    public async Task A_clean_batch_is_audited_with_its_counts_and_value()
    {
        await IngestAsync(
            BuildSale("VAN006-INV-20260810-AAA111"),
            BuildSale("VAN006-INV-20260810-CCC333", receiptGlobalNo: 503));

        var batch = _audit.Single(AuditActions.IngestVanSalesOfflineBatch);

        Assert.True(batch.IsSuccess);
        Assert.Equal("VanSalesOfflineSaleBatch", batch.EntityType);
        Assert.Equal("VAN006", batch.EntityId);
        Assert.Contains("2 accepted, 0 duplicate, 0 rejected", batch.Details);
        Assert.Contains("200.00 USD", batch.Details);
    }

    /// <summary>
    /// The one outcome that is stored nowhere else. A rejected sale exists only in the reply the handset
    /// is about to receive, so if the audit trail does not name it and its reason, a lost reply takes the
    /// takings with it and leaves nothing to investigate.
    /// </summary>
    [Fact]
    public async Task A_rejected_sale_is_audited_by_reference_and_reason()
    {
        var bad = BuildSale("VAN006-INV-20260810-BBB222", receiptGlobalNo: 502);
        bad.CustomerCode = "OTHER001";

        await IngestAsync(BuildSale("VAN006-INV-20260810-AAA111"), bad);

        var rejection = _audit.Single(AuditActions.RejectVanSalesOfflineSale);

        Assert.False(rejection.IsSuccess);
        Assert.Equal("VAN006-INV-20260810-BBB222", rejection.EntityId);
        Assert.Contains("not assigned", rejection.Details);

        // And the batch row carries the failure too, so the upload reads as needing attention without
        // having to open the individual sales.
        var batch = _audit.Single(AuditActions.IngestVanSalesOfflineBatch);
        Assert.False(batch.IsSuccess);
        Assert.Contains("VAN006-INV-20260810-BBB222", batch.Details);
        Assert.Contains("1 sale rejected", batch.ErrorMessage);
    }

    /// <summary>
    /// Accepted, but its receipt can never reach ZIMRA. That is a fiscal day that will close short, and
    /// until now it was visible only to whoever was reading the server logs.
    /// </summary>
    [Fact]
    public async Task An_unsignable_receipt_is_audited_against_the_sale()
    {
        var sale = BuildSale("VAN006-INV-20260810-AAA111");
        sale.DeviceSignatureHash = null;
        sale.DeviceSignatureValue = null;

        await IngestAsync(sale);

        var unsignable = _audit.Single(AuditActions.UnsignableVanSalesOfflineSale);

        Assert.False(unsignable.IsSuccess);
        Assert.Equal("VAN006-INV-20260810-AAA111", unsignable.EntityId);

        var batch = _audit.Single(AuditActions.IngestVanSalesOfflineBatch);
        Assert.False(batch.IsSuccess);
        Assert.Contains("cannot reach ZIMRA", batch.Details);
    }

    /// <summary>
    /// A van turned away for a misconfigured user keeps its whole day on the handset and the rep sees
    /// only a failed upload. Somebody has to be able to find that from the audit trail.
    /// </summary>
    [Fact]
    public async Task A_batch_refused_for_a_misconfigured_van_is_audited()
    {
        var van = await _context.Users.SingleAsync(u => u.Id == VanUser);
        van.AssignedWarehouseCode = null;
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var result = await BuildHandler().Handle(
            new IngestVanSalesOfflineSalesCommand(
                new VanSalesOfflineSaleBatchRequest { Sales = [BuildSale("VAN006-INV-20260810-AAA111")] },
                VanUser),
            CancellationToken.None);

        Assert.True(result.IsError);

        var batch = _audit.Single(AuditActions.IngestVanSalesOfflineBatch);
        Assert.False(batch.IsSuccess);
        Assert.Contains("no assigned warehouse", batch.Details);
        Assert.Contains("stay on the handset", batch.Details);
    }

    /// <summary>
    /// A batch of duplicates is a handset retrying, not a problem. It must not read as a failure or the
    /// trail fills with alarms for the one thing this endpoint is designed to absorb.
    /// </summary>
    [Fact]
    public async Task A_batch_of_duplicates_is_audited_as_a_success()
    {
        await IngestAsync(BuildSale("VAN006-INV-20260810-AAA111"));
        _audit.Clear();

        await IngestAsync(BuildSale("VAN006-INV-20260810-AAA111"));

        var batch = _audit.Single(AuditActions.IngestVanSalesOfflineBatch);

        Assert.True(batch.IsSuccess);
        Assert.Null(batch.ErrorMessage);
        Assert.Contains("0 accepted, 1 duplicate, 0 rejected", batch.Details);
    }

    private sealed record AuditEntry(
        string Action,
        string? EntityType,
        string? EntityId,
        string? Details,
        bool IsSuccess,
        string? ErrorMessage);

    private sealed class RecordingAuditService : IAuditService
    {
        private readonly List<AuditEntry> _entries = [];

        public AuditEntry Single(string action) =>
            Assert.Single(_entries, entry => entry.Action == action);

        public void Clear() => _entries.Clear();

        public Task LogAsync(string action, string username, string userRole, string? entityType = null,
            string? entityId = null, string? details = null, string? endpoint = null,
            bool isSuccess = true, string? errorMessage = null)
        {
            _entries.Add(new AuditEntry(action, entityType, entityId, details, isSuccess, errorMessage));
            return Task.CompletedTask;
        }

        public Task LogAsync(string action, string? entityType = null, string? entityId = null) =>
            LogAsync(action, string.Empty, string.Empty, entityType, entityId);

        public Task LogAsync(string action, string? entityType, string? entityId, string? details,
            bool isSuccess, string? errorMessage = null) =>
            LogAsync(action, string.Empty, string.Empty, entityType, entityId, details, null, isSuccess, errorMessage);
    }
}
