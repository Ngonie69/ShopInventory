using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopIntegration.Commands.PostDesktopSaleToSap;
using ShopInventory.Features.Notifications;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pressing "Post to SAP" on an online van sale that SAP refused after its receipt was signed.
///
/// The van sales console follows Desktop Sales here: the same command, the same eligibility rule, the same
/// outcomes. What differs is the door the command has to use. The receipt row's invoice is its reservation's
/// to produce, so the post goes through <c>VanSaleFiscalFirstPoster</c> — never the till or offline-van
/// services, and never the device a second time.
/// </summary>
public sealed class PostOnlineVanSaleOnRequestTests : IDisposable
{
    private const string VanOrder = "VAN005-INV-20260917-E58A14";
    private static readonly Guid Admin = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTime SoldOn = new(2026, 9, 17, 10, 15, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    /// <summary>What reached the reservation service, in order.</summary>
    private readonly List<ConfirmReservationRequest> _confirms = [];

    private Func<ConfirmReservationRequest, ConfirmReservationResponseDto> _confirm = _ => Posted();

    public PostOnlineVanSaleOnRequestTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        _context.Users.Add(new User
        {
            Id = Admin,
            Username = "admin",
            FirstName = "Back",
            LastName = "Office",
            PasswordHash = "x",
            Role = "Admin",
            IsActive = true
        });
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_refused_online_sale_posts_through_its_reservation_and_reports_the_invoice()
    {
        var reservation = SeedRefusedSale();

        var result = await Handler().Handle(new PostDesktopSaleToSapCommand(Admin, VanOrder), CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.Equal(DesktopSalePostOutcomes.Posted, result.Value.Outcome);
        Assert.Equal(5001, result.Value.SapDocNum);
        Assert.Equal("Posted to SAP as invoice 5001.", result.Value.Message);

        // Through the reservation, dated the day of the sale, and signed already: fiscalisation off.
        var confirm = Assert.Single(_confirms);
        Assert.Equal(reservation.ReservationId, confirm.ReservationId);
        Assert.Equal("2026-09-17", confirm.DocDate);
        Assert.False(confirm.Fiscalize);

        var receipt = Receipt();
        Assert.Equal(5001, receipt.SapDocNum);
        Assert.Null(receipt.LastPostingError);

        // SAP's refusal had closed the reservation. The post reopened it and held the stock before asking
        // SAP; the stubbed confirm leaves it where the poster put it.
        var reopened = await _context.StockReservations
            .AsNoTracking()
            .SingleAsync(r => r.ReservationId == reservation.ReservationId);
        Assert.Equal(ReservationStatus.Pending, reopened.Status);
        Assert.True(reopened.ExpiresAt > DateTime.UtcNow.AddMinutes(30));

        // And the entry the queue had parked for review is closed over the invoice.
        var entry = await _context.InvoiceQueue.AsNoTracking().SingleAsync(q => q.ExternalReference == VanOrder);
        Assert.Equal(InvoiceQueueStatus.Completed, entry.Status);
        Assert.Equal(5001, entry.SapDocNum);
        Assert.Null(entry.LastError);
    }

    [Fact]
    public async Task An_invoice_SAP_already_holds_is_reported_as_already_in_SAP()
    {
        SeedRefusedSale();
        _confirm = _ => new ConfirmReservationResponseDto
        {
            Success = true,
            AlreadyPosted = true,
            Message = "Reservation already posted previously; returning existing SAP invoice",
            SAPDocEntry = 9001,
            SAPDocNum = 5001
        };

        var result = await Handler().Handle(new PostDesktopSaleToSapCommand(Admin, VanOrder), CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.Equal(DesktopSalePostOutcomes.AlreadyInSap, result.Value.Outcome);
        Assert.Equal("SAP already held this sale as invoice 5001.", result.Value.Message);
        Assert.Equal(5001, Receipt().SapDocNum);
    }

    [Fact]
    public async Task A_refusal_is_reported_in_SAPs_own_words_and_counted_against_the_sale()
    {
        SeedRefusedSale();
        _confirm = _ => new ConfirmReservationResponseDto
        {
            Success = false,
            Message = "Failed to post reservation to SAP",
            Errors = ["Quantity falls into negative inventory"]
        };

        var result = await Handler().Handle(new PostDesktopSaleToSapCommand(Admin, VanOrder), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("DesktopSales.SalePostFailed", result.FirstError.Code);
        Assert.Contains("negative inventory", result.FirstError.Description);

        var receipt = Receipt();
        Assert.Null(receipt.SapDocNum);
        Assert.Equal(2, receipt.PostingAttempts);
        Assert.Contains("negative inventory", receipt.LastPostingError);
    }

    [Fact]
    public async Task A_post_another_caller_holds_is_reported_in_progress_and_nothing_is_sent()
    {
        SeedRefusedSale(reservationStatus: ReservationStatus.Confirming);

        var result = await Handler().Handle(new PostDesktopSaleToSapCommand(Admin, VanOrder), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("DesktopSales.SalePostInProgress", result.FirstError.Code);
        Assert.Empty(_confirms);

        // Not counted as an attempt: nothing was tried.
        Assert.Equal(1, Receipt().PostingAttempts);
    }

    [Fact]
    public async Task A_sale_SAP_already_holds_is_refused_before_anything_is_sent()
    {
        SeedRefusedSale(sapDocNum: 777913);

        var result = await Handler().Handle(new PostDesktopSaleToSapCommand(Admin, VanOrder), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("DesktopSales.SaleNotPostable", result.FirstError.Code);
        Assert.Contains("already in SAP", result.FirstError.Description);
        Assert.Empty(_confirms);
    }

    [Fact]
    public async Task An_unsigned_online_sale_is_refused_before_anything_is_sent()
    {
        SeedRefusedSale(fiscalisation: DesktopSaleFiscalizationStatus.Failed);

        var result = await Handler().Handle(new PostDesktopSaleToSapCommand(Admin, VanOrder), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("DesktopSales.SaleNotPostable", result.FirstError.Code);
        Assert.Contains("no confirmed fiscal receipt", result.FirstError.Description);
        Assert.Empty(_confirms);
    }

    [Fact]
    public async Task A_receipt_with_no_reservation_is_refused_with_the_way_out()
    {
        SeedRefusedSale(withReservation: false);

        var result = await Handler().Handle(new PostDesktopSaleToSapCommand(Admin, VanOrder), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("DesktopSales.SaleNotPostable", result.FirstError.Code);
        Assert.Contains("no reservation", result.FirstError.Description);
        Assert.Empty(_confirms);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// The state the van sales console shows as "Needs attention · SAP refused": the receipt row signed and
    /// without a DocNum, the reservation closed by SAP's refusal, the queue entry parked for review.
    /// </summary>
    private StockReservationEntity SeedRefusedSale(
        string reservationStatus = ReservationStatus.Failed,
        DesktopSaleFiscalizationStatus fiscalisation = DesktopSaleFiscalizationStatus.Success,
        int? sapDocNum = null,
        bool withReservation = true)
    {
        var reservation = new StockReservationEntity
        {
            ExternalReferenceId = VanOrder,
            SourceSystem = SaleSourceSystems.VanSales,
            CardCode = "VAN005",
            CardName = "Test Sales",
            RouteCustomerCode = "CLAUDETESTSHOP",
            RouteCustomerName = "Claude Test Shop",
            Currency = "USD",
            PaymentMethod = TenderTypes.Cash,
            Status = reservationStatus,
            ExpiresAt = SoldOn.AddMinutes(60),
            CreatedAt = SoldOn,
            CreatedBy = Admin.ToString(),
            Lines =
            [
                new StockReservationLineEntity
                {
                    LineNum = 0, ItemCode = "CHS001", ItemDescription = "Gouda 1kg",
                    OriginalQuantity = 1, ReservedQuantity = 1, UnitPrice = 0.37m, LineTotal = 0.37m,
                    WarehouseCode = "VAN005", TaxCode = "S1"
                }
            ]
        };

        if (withReservation)
        {
            _context.StockReservations.Add(reservation);
        }

        var signed = fiscalisation == DesktopSaleFiscalizationStatus.Success;

        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = VanOrder,
            SourceSystem = SaleSourceSystems.VanSalesOnline,
            CardCode = "VAN005",
            CardName = "Claude Test Shop",
            RouteCustomerCode = "CLAUDETESTSHOP",
            RouteCustomerName = "Claude Test Shop",
            WarehouseCode = "VAN005",
            Currency = "USD",
            DocDate = new DateTime(2026, 9, 17),
            NumAtCard = VanOrder,
            Comments = "Sold on the handset",
            TotalAmount = 0.43m,
            VatAmount = 0.06m,
            AmountPaid = 0.43m,
            PaymentMethod = TenderTypes.Cash,
            ConsolidationStatus = DesktopSaleConsolidationStatus.Consolidated,
            FiscalizationStatus = fiscalisation,
            FiscalReceiptNumber = signed ? "219735" : null,
            FiscalVerificationCode = signed ? "vc-219735" : null,
            SapDocNum = sapDocNum,
            SapDocEntry = sapDocNum,
            PostingAttempts = 1,
            LastPostingError = sapDocNum is null ? "SAP did not accept the invoice." : null,
            CreatedAt = SoldOn,
            Lines =
            [
                new DesktopSaleLineEntity
                {
                    LineNum = 0, ItemCode = "CHS001", ItemDescription = "Gouda 1kg",
                    Quantity = 1, UnitPrice = 0.37m, LineTotal = 0.37m,
                    WarehouseCode = "VAN005", TaxCode = "S1", TaxPercent = 15.5m
                }
            ]
        });

        if (withReservation)
        {
            _context.InvoiceQueue.Add(new InvoiceQueueEntity
            {
                ReservationId = reservation.ReservationId,
                ExternalReference = VanOrder,
                CustomerCode = "VAN005",
                InvoicePayload = "{}",
                Status = InvoiceQueueStatus.RequiresReview,
                SourceSystem = SaleSourceSystems.VanSales,
                FiscalizationSuccess = true,
                FiscalReceiptNumber = "219735",
                LastError = "SAP did not take this fiscalised van sale (reservation Failed): SAP did not accept the invoice.",
                CreatedAt = SoldOn,
                ProcessingStartedAt = SoldOn,
                ProcessedAt = SoldOn.AddMinutes(1)
            });
        }

        _context.SaveChanges();
        return reservation;
    }

    private DesktopSaleEntity Receipt() =>
        _context.DesktopSales.AsNoTracking().Single(s => s.ExternalReferenceId == VanOrder);

    private static ConfirmReservationResponseDto Posted() => new()
    {
        Success = true,
        SAPDocEntry = 9001,
        SAPDocNum = 5001
    };

    private PostDesktopSaleToSapHandler Handler()
    {
        var reservations = StubProxy.For<IStockReservationService>((method, args) => method.Name switch
        {
            nameof(IStockReservationService.ConfirmReservationAsync) =>
                Task.FromResult(Confirm((ConfirmReservationRequest)args![0]!)),

            _ => throw new InvalidOperationException($"IStockReservationService.{method.Name} was not expected.")
        });

        // A signed row is never offered to the device again, so any call here is the failure this guards.
        var fiscalisation = StubProxy.For<IFiscalizationService>((method, _) =>
            throw new InvalidOperationException(
                $"The fiscal device must not be asked about a signed sale ({method.Name})."));

        var notifications = StubProxy.For<INotificationService>((method, _) => method.Name switch
        {
            nameof(INotificationService.CreateNotificationAsync) => Task.FromResult(0),
            _ => throw new InvalidOperationException($"INotificationService.{method.Name} was not expected.")
        });

        var tax = Options.Create(new TaxSettings { VatRate = 0.155m });

        var poster = new VanSaleFiscalFirstPoster(
            _context,
            reservations,
            new DesktopSaleFiscaliser(fiscalisation, notifications, tax, NullLogger<DesktopSaleFiscaliser>.Instance),
            DesktopCreditPosters.Idle(_context),
            tax,
            NullLogger<VanSaleFiscalFirstPoster>.Instance);

        // The other two routes are real and wired to a SAP client that refuses every call: an online van
        // sale reaching either would be a second invoice for one receipt, and this is where it would show.
        var sap = StubProxy.For<ISAPServiceLayerClient>((method, _) =>
            throw new InvalidOperationException(
                $"SAP must not be called directly for an online van sale ({method.Name})."));

        var circuit = new SapCircuitBreakerState(Options.Create(new SAPSettings()));

        var till = new DesktopSalePostingService(
            _context,
            sap,
            circuit,
            SaleBatchAllocators.Holding(),
            SalePostGuards.Backed(_connection),
            DesktopCreditPosters.Idle(_context),
            Options.Create(new DesktopSalePostingSettings()),
            NullLogger<DesktopSalePostingService>.Instance);

        var van = new VanSalesEndOfDayPostingService(
            _context,
            sap,
            circuit,
            SaleBatchAllocators.Holding(),
            new StockLedger(_context, Options.Create(new DailyStockSettings()), NullLogger<StockLedger>.Instance),
            SalePostGuards.Backed(_connection),
            DesktopCreditPosters.Idle(_context),
            Options.Create(new VanSalesPostingSettings()),
            NullLogger<VanSalesEndOfDayPostingService>.Instance);

        return new PostDesktopSaleToSapHandler(
            _context,
            till,
            van,
            poster,
            StubProxy.For<IAuditService>((_, _) => Task.CompletedTask),
            NullLogger<PostDesktopSaleToSapHandler>.Instance);
    }

    private ConfirmReservationResponseDto Confirm(ConfirmReservationRequest request)
    {
        _confirms.Add(request);
        return _confirm(request);
    }
}
