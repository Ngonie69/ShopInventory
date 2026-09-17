using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Features.VanSalesDocuments;
using ShopInventory.Features.VanSalesDocuments.Queries.GetVanSalesCreditNotes;
using ShopInventory.Features.VanSalesDocuments.Queries.GetVanSalesInvoice;
using ShopInventory.Features.VanSalesDocuments.Queries.GetVanSalesInvoices;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The Van Sales → Invoices and Credit Notes pages list what the van sales app created, and say where each
/// document stands with ZIMRA and with SAP.
///
/// The failure these guard against is a list that looks complete and is not: a sale that was fiscalised but
/// never invoiced missing from it, a basket that was never a sale shown as one, or an online sale shown twice
/// because its receipt row is read as a second sale. None of those throws; each is just a wrong page.
/// </summary>
public sealed class VanSalesDocumentsTests : IDisposable
{
    private static readonly Guid Rep = Guid.Parse("77777777-7777-7777-7777-777777777777");

    /// <summary>A CAT trading day, and 09:00 UTC inside it.</summary>
    private static readonly DateTime Day = new(2026, 9, 15);
    private static readonly DateTime DuringDayUtc = new(2026, 9, 15, 9, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public VanSalesDocumentsTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        _context.Users.Add(new User
        {
            Id = Rep,
            Username = "van008",
            FirstName = "Tendai",
            LastName = "Moyo",
            PasswordHash = "x",
            Role = "Sales",
            IsActive = true
        });
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // --- Invoices ---

    [Fact]
    public async Task A_posted_online_sale_is_listed_once_with_its_receipt()
    {
        AddReservation("VAN-1", ReservationStatus.Confirmed, docEntry: 501, docNum: 9501);
        AddReceiptRow("VAN-1", signed: true, docNum: 9501);

        var result = await ListInvoicesAsync();

        var row = Assert.Single(result.Rows);
        Assert.Equal(VanSalesDocumentStates.Complete, row.State);
        Assert.Equal("vc-VAN-1", row.FiscalVerificationCode);
        Assert.Equal(9501, row.SapDocNum);
        Assert.Equal("Tendai Moyo", row.RepName);
        Assert.Equal("Mbare Corner Shop", row.CustomerName);
        Assert.True(row.AmountIncludesVat);
        Assert.Equal(115.50m, row.Amount);
    }

    /// <summary>The case the fiscalise-first change creates, and the one an operator most needs to find.</summary>
    [Fact]
    public async Task A_sale_fiscalised_but_not_in_SAP_is_listed_as_awaiting_SAP()
    {
        AddReservation("VAN-2", ReservationStatus.Pending);
        AddReceiptRow("VAN-2", signed: true);

        var row = Assert.Single((await ListInvoicesAsync()).Rows);

        Assert.Equal(VanSalesDocumentStates.AwaitingSap, row.State);
        Assert.Null(row.SapDocNum);
    }

    [Fact]
    public async Task A_sale_SAP_keeps_refusing_needs_attention()
    {
        AddReservation("VAN-3", ReservationStatus.Pending);
        AddReceiptRow("VAN-3", signed: true, postingError: "Quantity falls into negative inventory");

        var row = Assert.Single((await ListInvoicesAsync()).Rows);

        Assert.Equal(VanSalesDocumentStates.NeedsAttention, row.State);
        Assert.Contains("negative inventory", row.Problem);
    }

    [Fact]
    public async Task A_basket_that_never_became_a_sale_is_not_listed()
    {
        AddReservation("VAN-HELD", ReservationStatus.Pending);
        AddReservation("VAN-GONE", ReservationStatus.Cancelled);
        AddReservation("VAN-LAPSED", ReservationStatus.Expired);

        Assert.Empty((await ListInvoicesAsync()).Rows);
    }

    [Fact]
    public async Task A_queued_conversion_is_listed_before_it_is_signed()
    {
        var reservation = AddReservation("VAN-CONV", ReservationStatus.Pending);
        _context.InvoiceQueue.Add(new InvoiceQueueEntity
        {
            ReservationId = reservation.ReservationId,
            ExternalReference = "VAN-CONV",
            CustomerCode = "C-1",
            InvoicePayload = "{}",
            Status = InvoiceQueueStatus.Pending,
            SourceSystem = SaleSourceSystems.VanSales
        });
        await _context.SaveChangesAsync();

        var row = Assert.Single((await ListInvoicesAsync()).Rows);

        Assert.Equal(VanSalesDocumentStates.InProgress, row.State);
    }

    /// <summary>
    /// An online sale from before receipts were stored has no row of its own; the fiscal log, asked by
    /// DocNum, is what says it was fiscalised.
    /// </summary>
    [Fact]
    public async Task An_older_online_sale_takes_its_fiscal_status_from_the_fiscal_log()
    {
        AddReservation("VAN-OLD", ReservationStatus.Confirmed, docEntry: 400, docNum: 9400);
        _context.DesktopFiscalTransactions.Add(new DesktopFiscalTransactionEntity
        {
            ClientTransactionId = "invoice-fiscalisation-9400",
            DocumentType = "Invoice",
            DocNum = 9400,
            Status = "Success",
            VerificationCode = "vc-9400",
            ReceiptGlobalNo = 77
        });

        AddReservation("VAN-UNSIGNED", ReservationStatus.Confirmed, docEntry: 401, docNum: 9401);
        await _context.SaveChangesAsync();

        var rows = (await ListInvoicesAsync()).Rows.ToDictionary(r => r.Reference);

        Assert.Equal(VanSalesDocumentStates.Complete, rows["VAN-OLD"].State);
        Assert.Equal("vc-9400", rows["VAN-OLD"].FiscalVerificationCode);
        Assert.Equal(VanSalesDocumentStates.NotFiscalised, rows["VAN-UNSIGNED"].State);
    }

    [Fact]
    public async Task An_offline_sale_is_listed_and_other_sources_are_not()
    {
        AddOfflineSale("VAN-OFF", SaleSourceSystems.VanSales, docNum: 9600);
        AddOfflineSale("TILL-1", SaleSourceSystems.ShopTill, docNum: 9601);

        var row = Assert.Single((await ListInvoicesAsync()).Rows);

        Assert.Equal("VAN-OFF", row.Reference);
        Assert.Equal("Offline", row.Channel);
        Assert.Equal(VanSalesDocumentStates.Complete, row.State);
    }

    [Fact]
    public async Task The_state_counts_are_taken_before_the_state_filter()
    {
        AddReservation("VAN-A", ReservationStatus.Confirmed, docEntry: 1, docNum: 91);
        AddReceiptRow("VAN-A", signed: true, docNum: 91);
        AddReservation("VAN-B", ReservationStatus.Pending);
        AddReceiptRow("VAN-B", signed: true);

        var result = await ListInvoicesAsync(state: VanSalesDocumentStates.AwaitingSap);

        Assert.Equal("VAN-B", Assert.Single(result.Rows).Reference);
        Assert.Equal(2, result.Counts.All);
        Assert.Equal(1, result.Counts.Complete);
        Assert.Equal(1, result.Counts.AwaitingSap);
    }

    [Fact]
    public async Task The_detail_carries_the_lines_and_the_QR()
    {
        AddReservation("VAN-D", ReservationStatus.Confirmed, docEntry: 700, docNum: 9700);
        AddReceiptRow("VAN-D", signed: true, docNum: 9700);
        await _context.SaveChangesAsync();

        var result = await new GetVanSalesInvoiceHandler(_context)
            .Handle(new GetVanSalesInvoiceQuery("VAN-D"), CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.Equal("qr-VAN-D", result.Value.FiscalQrCode);
        Assert.Equal("CHS001", Assert.Single(result.Value.Lines).ItemCode);
    }

    [Fact]
    public async Task A_reference_the_app_did_not_create_is_not_found()
    {
        var result = await new GetVanSalesInvoiceHandler(_context)
            .Handle(new GetVanSalesInvoiceQuery("WEB-123"), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("VanSalesDocuments.InvoiceNotFound", result.FirstError.Code);
    }

    // --- Credit notes ---

    [Fact]
    public async Task A_SAP_credit_memo_against_a_van_invoice_is_listed_with_the_invoice_it_reverses()
    {
        AddReservation("VAN-CR", ReservationStatus.Confirmed, docEntry: 800, docNum: 9800);
        AddCreditMemo(docEntry: 50, docNum: 3050, baseEntry: 800);
        AddCreditMemo(docEntry: 51, docNum: 3051, baseEntry: 12345);

        var result = await ListCreditNotesAsync();

        var row = Assert.Single(result.Rows);
        Assert.Equal("3050", row.Number);
        var credited = Assert.Single(row.CreditedInvoices);
        Assert.Equal("VAN-CR", credited.Reference);
        Assert.Equal("Tendai Moyo", credited.RepName);
        Assert.Equal(VanSalesDocumentStates.NotFiscalised, row.State);
        Assert.True(result.SapProjectionCurrent);
    }

    [Fact]
    public async Task A_till_credit_against_an_offline_van_sale_is_listed_once_even_after_SAP_takes_it()
    {
        var sale = AddOfflineSale("VAN-OFF-CR", SaleSourceSystems.VanSales, docNum: 9900, docEntry: 900);

        _context.DesktopCreditNotes.Add(new DesktopCreditNoteEntity
        {
            Id = Guid.NewGuid(),
            Sale = sale,
            Number = "CR-VAN-OFF-CR-1",
            OriginalFiscalNumber = "VAN-OFF-CR",
            Reason = "Damaged",
            Currency = "USD",
            Amount = 20m,
            Status = DesktopCreditStatuses.Fiscalised,
            SapStatus = DesktopCreditSapStatuses.Posted,
            SapDocEntry = 60,
            SapDocNum = 3060,
            CreatedAtUtc = DuringDayUtc
        });
        AddCreditMemo(docEntry: 60, docNum: 3060, baseEntry: null);
        await _context.SaveChangesAsync();

        var row = Assert.Single((await ListCreditNotesAsync()).Rows);

        // Not in the memo projection by base document — the till credit is what ties it to the sale — so it is
        // listed as the till credit, carrying SAP's number.
        Assert.Equal(VanSalesDocumentStates.Complete, row.State);
        Assert.Equal(3060, row.SapDocNum);
        Assert.Equal("VAN-OFF-CR", Assert.Single(row.CreditedInvoices).Reference);
    }

    [Theory]
    [InlineData(true, true, false, VanSalesDocumentStates.Complete)]
    [InlineData(true, true, true, VanSalesDocumentStates.Complete)]
    [InlineData(true, false, false, VanSalesDocumentStates.AwaitingSap)]
    [InlineData(true, false, true, VanSalesDocumentStates.NeedsAttention)]
    [InlineData(false, true, false, VanSalesDocumentStates.NotFiscalised)]
    [InlineData(false, false, false, VanSalesDocumentStates.InProgress)]
    [InlineData(false, false, true, VanSalesDocumentStates.NeedsAttention)]
    public void The_state_rule(bool fiscalised, bool inSap, bool hasFailure, string expected) =>
        Assert.Equal(expected, VanSalesDocumentStates.Decide(fiscalised, inSap, hasFailure));

    // --- Helpers ---

    private async Task<VanSalesInvoicesResult> ListInvoicesAsync(string? state = null)
    {
        await _context.SaveChangesAsync();

        var result = await new GetVanSalesInvoicesHandler(_context).Handle(
            new GetVanSalesInvoicesQuery(Day, Day, State: state), CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        return result.Value;
    }

    private async Task<VanSalesCreditNotesResult> ListCreditNotesAsync()
    {
        await _context.SaveChangesAsync();

        var projection = StubProxy.For<ICreditNoteProjectionSyncService>((method, _) => method.Name switch
        {
            nameof(ICreditNoteProjectionSyncService.IsReadyForReadsAsync) => Task.FromResult(true),
            _ => throw new InvalidOperationException($"{method.Name} was not expected.")
        });

        var result = await new GetVanSalesCreditNotesHandler(_context, projection).Handle(
            new GetVanSalesCreditNotesQuery(Day, Day), CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        return result.Value;
    }

    private StockReservationEntity AddReservation(
        string reference,
        string status,
        int? docEntry = null,
        int? docNum = null)
    {
        var reservation = new StockReservationEntity
        {
            ExternalReferenceId = reference,
            SourceSystem = SaleSourceSystems.VanSales,
            CardCode = "VAN008",
            CardName = "Van 8",
            RouteCustomerCode = "RC-17",
            RouteCustomerName = "Mbare Corner Shop",
            Currency = "USD",
            PaymentMethod = "Cash",
            TotalValue = 100m,
            Status = status,
            CreatedAt = DuringDayUtc,
            ExpiresAt = DuringDayUtc.AddHours(1),
            CreatedBy = Rep.ToString(),
            SAPDocEntry = docEntry,
            SAPDocNum = docNum,
            Lines =
            [
                new StockReservationLineEntity
                {
                    LineNum = 0, ItemCode = "CHS001", OriginalQuantity = 10, ReservedQuantity = 10,
                    UnitPrice = 10m, LineTotal = 100m, WarehouseCode = "VAN008", TaxCode = "S1"
                }
            ]
        };

        _context.StockReservations.Add(reservation);
        return reservation;
    }

    private void AddReceiptRow(string reference, bool signed, int? docNum = null, string? postingError = null)
    {
        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = reference,
            SourceSystem = SaleSourceSystems.VanSalesOnline,
            CardCode = "VAN008",
            WarehouseCode = "VAN008",
            Currency = "USD",
            DocDate = Day,
            TotalAmount = 115.50m,
            VatAmount = 15.50m,
            ConsolidationStatus = DesktopSaleConsolidationStatus.Consolidated,
            FiscalizationStatus = signed ? DesktopSaleFiscalizationStatus.Success : DesktopSaleFiscalizationStatus.Pending,
            FiscalReceiptNumber = signed ? "812" : null,
            FiscalVerificationCode = signed ? $"vc-{reference}" : null,
            FiscalQRCode = signed ? $"qr-{reference}" : null,
            SapDocNum = docNum,
            LastPostingError = postingError,
            PostingAttempts = postingError is null ? 0 : 3,
            CreatedAt = DuringDayUtc
        });
    }

    private DesktopSaleEntity AddOfflineSale(string reference, string source, int? docNum, int? docEntry = null)
    {
        var sale = new DesktopSaleEntity
        {
            ExternalReferenceId = reference,
            SourceSystem = source,
            CardCode = "VAN008",
            RouteCustomerName = "Mbare Corner Shop",
            WarehouseCode = "VAN008",
            Currency = "USD",
            DocDate = Day,
            TotalAmount = 50m,
            VatAmount = 6.71m,
            ConsolidationStatus = DesktopSaleConsolidationStatus.Consolidated,
            FiscalizationStatus = DesktopSaleFiscalizationStatus.Success,
            FiscalReceiptNumber = "601",
            SapDocNum = docNum,
            SapDocEntry = docEntry,
            CreatedBy = Rep.ToString(),
            CreatedAt = DuringDayUtc,
            Lines =
            [
                new DesktopSaleLineEntity
                {
                    LineNum = 0, ItemCode = "CHS001", Quantity = 5, UnitPrice = 10m, LineTotal = 50m, WarehouseCode = "VAN008"
                }
            ]
        };

        _context.DesktopSales.Add(sale);
        return sale;
    }

    private void AddCreditMemo(int docEntry, int docNum, int? baseEntry)
    {
        _context.SapCreditNoteSnapshots.Add(new SapCreditNoteSnapshotEntity
        {
            SapDocEntry = docEntry,
            SapDocNum = docNum,
            DocDate = Day,
            CardCode = "VAN008",
            CardName = "Van 8",
            DocCurrency = "USD",
            DocTotal = 20m,
            VatSum = 2.68m,
            LastSeenInSapAtUtc = DuringDayUtc,
            SyncedAtUtc = DuringDayUtc,
            Lines =
            [
                new SapCreditNoteLineSnapshotEntity
                {
                    LineNum = 0,
                    ItemCode = "CHS001",
                    BaseType = baseEntry is null ? null : 13,
                    BaseEntry = baseEntry,
                    LineTotal = 20m,
                    CreditReason = "Damaged"
                }
            ]
        });
    }
}
