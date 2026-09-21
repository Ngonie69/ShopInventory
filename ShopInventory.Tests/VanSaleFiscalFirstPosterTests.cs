using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopCreditNotes;
using ShopInventory.Features.Notifications;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// A van sale is fiscalised first and posted to SAP second.
///
/// Every case here is about the one-way door between the two. Before the device signs, the sale can still
/// be refused and nothing may be left behind. After it signs, a receipt exists that cannot be withdrawn, so
/// the sale must stand — its stock held and its invoice retried — however SAP answers, and the device must
/// never be asked to sign it a second time.
/// </summary>
public sealed class VanSaleFiscalFirstPosterTests : IDisposable
{
    private const string Reference = "VAN008-20260917-0042";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    /// <summary>What reached the device and SAP, in the order it reached them.</summary>
    private readonly List<string> _calls = [];

    private readonly List<InvoiceDto> _signed = [];
    private readonly List<ConfirmReservationRequest> _confirms = [];

    private readonly TaxSettings _tax = new()
    {
        VatRate = 0.155m,
        RatesByTaxCode = new(StringComparer.OrdinalIgnoreCase) { ["S1"] = 0.155m, ["O0"] = 0m }
    };

    public VanSaleFiscalFirstPosterTests()
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

    private StockReservationEntity SeedReservation(
        string status = ReservationStatus.Pending,
        string reference = Reference)
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
            PaymentMethod = TenderTypes.Cash,
            Status = status,
            ExpiresAt = DateTime.UtcNow.AddMinutes(-5),
            CreatedBy = Guid.NewGuid().ToString(),
            Lines =
            [
                new StockReservationLineEntity
                {
                    LineNum = 0, ItemCode = "CHS001", ItemDescription = "Gouda 1kg",
                    OriginalQuantity = 2, ReservedQuantity = 2, UnitPrice = 10m, LineTotal = 20m,
                    WarehouseCode = "VAN008"
                },
                new StockReservationLineEntity
                {
                    LineNum = 1, ItemCode = "MLK002", ItemDescription = "Fresh milk 2L",
                    OriginalQuantity = 1, ReservedQuantity = 1, UnitPrice = 5m, LineTotal = 5m,
                    WarehouseCode = "VAN008"
                }
            ]
        };

        _context.StockReservations.Add(reservation);
        _context.SapItemTaxGroups.AddRange(
            new SapItemTaxGroupEntity { ItemCode = "CHS001", VatGroup = "S1", ResolvedAtUtc = DateTime.UtcNow },
            new SapItemTaxGroupEntity { ItemCode = "MLK002", VatGroup = "O0", ResolvedAtUtc = DateTime.UtcNow });
        _context.SaveChanges();

        return reservation;
    }

    private static FiscalizationResult Signed() => new()
    {
        Success = true,
        ReceiptGlobalNo = "771",
        DeviceSerial = "DEV-1",
        QRCode = "qr",
        VerificationCode = "vc",
        FiscalDayNo = "12"
    };

    private static ConfirmReservationResponseDto Posted() => new()
    {
        Success = true,
        SAPDocEntry = 9001,
        SAPDocNum = 5001
    };

    private VanSaleFiscalFirstPoster BuildPoster(
        Func<FiscalizationResult>? sign = null,
        Func<ConfirmReservationRequest, ConfirmReservationResponseDto>? confirm = null,
        Func<FiscalizationResult?>? existingReceipt = null,
        DesktopCreditSapPoster? creditPoster = null)
    {
        var fiscalisation = StubProxy.For<IFiscalizationService>((method, args) => method.Name switch
        {
            nameof(IFiscalizationService.FindPreSapReceiptAsync) =>
                (object)Task.FromResult(Record("lookup", existingReceipt is null ? null : existingReceipt())),

            nameof(IFiscalizationService.FiscalizePreSapInvoiceAsync) =>
                Task.FromResult(Sign((InvoiceDto)args![0]!, sign ?? Signed)),

            _ => throw new InvalidOperationException($"IFiscalizationService.{method.Name} was not expected.")
        });

        var notifications = StubProxy.For<INotificationService>((method, _) => method.Name switch
        {
            nameof(INotificationService.CreateNotificationAsync) => Task.FromResult(0),
            _ => throw new InvalidOperationException($"INotificationService.{method.Name} was not expected.")
        });

        var reservations = StubProxy.For<IStockReservationService>((method, args) => method.Name switch
        {
            nameof(IStockReservationService.ConfirmReservationAsync) =>
                Task.FromResult(Confirm((ConfirmReservationRequest)args![0]!, confirm ?? (_ => Posted()))),

            _ => throw new InvalidOperationException($"IStockReservationService.{method.Name} was not expected.")
        });

        var fiscaliser = new DesktopSaleFiscaliser(
            fiscalisation,
            notifications,
            Options.Create(_tax),
            NullLogger<DesktopSaleFiscaliser>.Instance);

        return new VanSaleFiscalFirstPoster(
            _context,
            reservations,
            fiscaliser,
            creditPoster ?? DesktopCreditPosters.Idle(_context),
            Options.Create(_tax),
            NullLogger<VanSaleFiscalFirstPoster>.Instance);
    }

    private T Record<T>(string call, T value)
    {
        _calls.Add(call);
        return value;
    }

    private FiscalizationResult Sign(InvoiceDto invoice, Func<FiscalizationResult> sign)
    {
        _calls.Add("sign");
        _signed.Add(invoice);
        return sign();
    }

    private ConfirmReservationResponseDto Confirm(
        ConfirmReservationRequest request,
        Func<ConfirmReservationRequest, ConfirmReservationResponseDto> confirm)
    {
        _calls.Add("post");
        _confirms.Add(request);
        return confirm(request);
    }

    private static VanSaleFiscalFirstRequest Request(string reservationId, bool mayAlreadyBeFiscalised = false) =>
        new(reservationId, DocDate: "2026-09-17", MayAlreadyBeFiscalised: mayAlreadyBeFiscalised);

    private DesktopSaleEntity StoredSale() =>
        _context.DesktopSales.AsNoTracking().Include(s => s.Lines).Single(s => s.ExternalReferenceId == Reference);

    private StockReservationEntity StoredReservation() =>
        _context.StockReservations.AsNoTracking().Include(r => r.Lines).Single(r => r.ExternalReferenceId == Reference);

    /// <summary>
    /// A credit taken while SAP would not take the invoice is raised the moment it does.
    /// </summary>
    /// <remarks>
    /// This window is what signing first costs: between the receipt and the invoice the sale exists at
    /// ZIMRA and nowhere else, so a return handed back in it can only be credited fiscally — there is
    /// no invoice for a memo to be based on. It is not a rare corner either, because a refused sale
    /// spends the window on the posting queue. Without this hook the memo waited for
    /// <c>DesktopCreditSapSweep</c> to notice, which is the fallback and not the path.
    /// </remarks>
    [Fact]
    public async Task A_credit_taken_while_SAP_refused_is_raised_when_the_invoice_posts()
    {
        var reservation = SeedReservation();
        var attempts = 0;

        // SAP refuses the first time and takes it on the retry, which is what the queue lives for.
        ConfirmReservationResponseDto Refusing(ConfirmReservationRequest _) =>
            attempts++ == 0
                ? new ConfirmReservationResponseDto { Success = false, Message = "SAP said no." }
                : Posted();

        var sap = new RecordingSap();
        var credits = DesktopCreditPosters.Recording(_context, sap);

        var refused = await BuildPoster(confirm: Refusing, creditPoster: credits)
            .FiscaliseThenPostAsync(Request(reservation.ReservationId), default);

        Assert.Equal(VanSaleFiscalFirstStatus.AwaitingSap, refused.Status);

        // The receipt stands and the invoice does not exist. This is the state the drawer shows as
        // "ZIMRA signed, SAP refused", and the only thing that can reverse the sale is a credit.
        var signed = StoredSale();
        Assert.Equal(DesktopSaleFiscalizationStatus.Success, signed.FiscalizationStatus);
        Assert.Null(signed.SapDocEntry);

        // The customer brings the cheese back before the invoice has posted.
        var note = await DesktopCreditPosters.GivenCreditAsync(_context, signed);
        Assert.Empty(sap.Created);

        var posted = await BuildPoster(confirm: Refusing, creditPoster: credits)
            .FiscaliseThenPostAsync(Request(reservation.ReservationId, mayAlreadyBeFiscalised: true), default);

        Assert.Equal(VanSaleFiscalFirstStatus.Posted, posted.Status);

        // Signed once, posted twice: the retry must not ask the device again.
        Assert.Equal(["sign", "post", "post"], _calls);

        // And the memo is based on the invoice that has just appeared.
        Assert.Equal(9001, Assert.Single(sap.Created).OriginalInvoiceDocEntry);

        var settled = await _context.DesktopCreditNotes.AsNoTracking().SingleAsync(n => n.Id == note.Id);
        Assert.Equal(DesktopCreditSapStatuses.Posted, settled.SapStatus);
    }

    [Fact]
    public async Task The_device_signs_before_SAP_is_asked_and_both_are_recorded()
    {
        var reservation = SeedReservation();

        var outcome = await BuildPoster().FiscaliseThenPostAsync(Request(reservation.ReservationId), default);

        Assert.Equal(VanSaleFiscalFirstStatus.Posted, outcome.Status);
        Assert.Equal(["sign", "post"], _calls);

        var sale = StoredSale();
        Assert.Equal(SaleSourceSystems.VanSalesOnline, sale.SourceSystem);
        Assert.Equal(DesktopSaleFiscalizationStatus.Success, sale.FiscalizationStatus);
        Assert.Equal("vc", sale.FiscalVerificationCode);
        Assert.Equal(5001, sale.SapDocNum);
        Assert.Equal(DesktopSaleConsolidationStatus.Consolidated, sale.ConsolidationStatus);
        Assert.Equal(DesktopSaleReceiptIngestStatus.NotApplicable, sale.ReceiptIngestStatus);

        // Signed under the van order, which is also what the invoice is posted under.
        Assert.Equal("Mbare Corner Shop", _signed.Single().CardName);
        Assert.False(_confirms.Single().Fiscalize);
    }

    [Fact]
    public async Task The_receipt_and_the_invoice_are_charged_at_the_same_VAT_group()
    {
        var reservation = SeedReservation();

        await BuildPoster().FiscaliseThenPostAsync(Request(reservation.ReservationId), default);

        // The receipt: cheese standard-rated and grossed up, milk zero-rated and not.
        var signedLines = (_signed.Single().Lines ?? []).OrderBy(l => l.LineNum).ToList();
        Assert.Equal(["S1", "O0"], signedLines.Select(l => l.TaxCode));
        Assert.Equal([23.10m, 5.00m], signedLines.Select(l => l.GrossTotal));

        // The invoice: the same codes written onto the lines SAP posts from, so SAP cannot pick another.
        Assert.Equal(["S1", "O0"], StoredReservation().Lines.OrderBy(l => l.LineNum).Select(l => l.TaxCode));

        var sale = StoredSale();
        Assert.Equal(3.10m, sale.VatAmount);
        Assert.Equal(28.10m, sale.TotalAmount);
    }

    [Fact]
    public async Task A_reservation_no_longer_holding_stock_is_never_signed()
    {
        var reservation = SeedReservation(ReservationStatus.Expired);

        var outcome = await BuildPoster().FiscaliseThenPostAsync(Request(reservation.ReservationId), default);

        Assert.Equal(VanSaleFiscalFirstStatus.NotPostable, outcome.Status);
        Assert.Empty(_calls);
        Assert.False(_context.DesktopSales.Any());
    }

    [Fact]
    public async Task A_device_refusal_leaves_nothing_in_SAP()
    {
        var reservation = SeedReservation();

        var outcome = await BuildPoster(sign: () => new FiscalizationResult { Success = false, Message = "Bad tax id" })
            .FiscaliseThenPostAsync(Request(reservation.ReservationId), default);

        Assert.Equal(VanSaleFiscalFirstStatus.FiscalFailed, outcome.Status);
        Assert.False(outcome.IsFiscalised);
        Assert.Equal(["sign"], _calls);
    }

    [Fact]
    public async Task An_unresolved_receipt_is_neither_posted_nor_offered_to_the_device_again()
    {
        var reservation = SeedReservation();
        var poster = BuildPoster(sign: () => new FiscalizationResult
        {
            Success = false,
            RequiresReconciliation = true,
            Message = "Timed out waiting for the device"
        });

        var first = await poster.FiscaliseThenPostAsync(Request(reservation.ReservationId), default);
        var second = await poster.FiscaliseThenPostAsync(Request(reservation.ReservationId), default);

        Assert.Equal(VanSaleFiscalFirstStatus.FiscalUnresolved, first.Status);
        Assert.Equal(VanSaleFiscalFirstStatus.FiscalUnresolved, second.Status);
        Assert.Equal(["sign"], _calls);
    }

    [Fact]
    public async Task A_SAP_refusal_after_signing_keeps_the_sale_and_holds_its_stock()
    {
        var reservation = SeedReservation();

        var outcome = await BuildPoster(confirm: _ =>
            {
                // What the real confirm does to a non-transient refusal: the reservation is closed, and with
                // it the hold on stock that has already left under a receipt.
                _context.StockReservations
                    .Where(r => r.ReservationId == reservation.ReservationId)
                    .ExecuteUpdate(u => u.SetProperty(r => r.Status, ReservationStatus.Failed));

                return new ConfirmReservationResponseDto
                {
                    Success = false,
                    Message = "Failed to post reservation to SAP",
                    Errors = ["Quantity falls into negative inventory"]
                };
            })
            .FiscaliseThenPostAsync(Request(reservation.ReservationId), default);

        Assert.Equal(VanSaleFiscalFirstStatus.AwaitingSap, outcome.Status);
        Assert.True(outcome.IsFiscalised);
        Assert.Contains("negative inventory", outcome.Error);

        var stored = StoredReservation();
        Assert.Equal(ReservationStatus.Pending, stored.Status);
        Assert.True(stored.ExpiresAt > DateTime.UtcNow.AddMinutes(30));

        var sale = StoredSale();
        Assert.Equal(DesktopSaleFiscalizationStatus.Success, sale.FiscalizationStatus);
        Assert.Null(sale.SapDocNum);
        Assert.Equal(1, sale.PostingAttempts);
    }

    [Fact]
    public async Task A_retry_after_SAP_refused_posts_without_signing_again()
    {
        var reservation = SeedReservation();

        await BuildPoster(confirm: _ => new ConfirmReservationResponseDto { Success = false, Message = "Service unavailable" })
            .FiscaliseThenPostAsync(Request(reservation.ReservationId), default);

        var outcome = await BuildPoster()
            .FiscaliseThenPostAsync(Request(reservation.ReservationId, mayAlreadyBeFiscalised: true), default);

        Assert.Equal(VanSaleFiscalFirstStatus.Posted, outcome.Status);
        Assert.Equal(["sign", "post", "post"], _calls);
        Assert.Equal(5001, StoredSale().SapDocNum);
        Assert.Null(StoredSale().LastPostingError);
    }

    [Fact]
    public async Task A_sale_the_device_may_already_hold_adopts_that_receipt_instead_of_signing()
    {
        var reservation = SeedReservation();

        var outcome = await BuildPoster(existingReceipt: () => new FiscalizationResult
            {
                Success = true,
                AlreadyFiscalised = true,
                ReceiptGlobalNo = "640",
                VerificationCode = "vc-640",
                QRCode = "qr-640"
            })
            .FiscaliseThenPostAsync(Request(reservation.ReservationId, mayAlreadyBeFiscalised: true), default);

        Assert.Equal(VanSaleFiscalFirstStatus.Posted, outcome.Status);
        Assert.Equal(["lookup", "post"], _calls);
        Assert.Equal("640", StoredSale().FiscalReceiptNumber);
        Assert.Equal("vc-640", StoredSale().FiscalVerificationCode);
    }

    [Fact]
    public async Task A_device_that_cannot_be_asked_is_sent_nothing()
    {
        var reservation = SeedReservation();

        var outcome = await BuildPoster(existingReceipt: () => throw new HttpRequestException("device offline"))
            .FiscaliseThenPostAsync(Request(reservation.ReservationId, mayAlreadyBeFiscalised: true), default);

        Assert.Equal(VanSaleFiscalFirstStatus.FiscalUnchecked, outcome.Status);
        Assert.True(outcome.Transient);
        Assert.Empty(_calls);
        Assert.Equal(DesktopSaleFiscalizationStatus.Pending, StoredSale().FiscalizationStatus);
    }

    [Fact]
    public async Task A_reference_already_used_by_another_kind_of_sale_is_refused_unsigned()
    {
        var reservation = SeedReservation();
        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = Reference,
            SourceSystem = SaleSourceSystems.VanSales,
            CardCode = "VAN008",
            WarehouseCode = "VAN008",
            Currency = "USD",
            DocDate = DateTime.UtcNow.Date,
            CreatedAt = DateTime.UtcNow
        });
        _context.SaveChanges();

        var outcome = await BuildPoster().FiscaliseThenPostAsync(Request(reservation.ReservationId), default);

        Assert.Equal(VanSaleFiscalFirstStatus.NotPostable, outcome.Status);
        Assert.Empty(_calls);
    }

    /// <summary>
    /// The queue entry a refused sale was handed to is closed by whichever post lands first.
    /// </summary>
    /// <remarks>
    /// After SAP refuses a signed sale the request hands it to the invoice queue, and the queue parks it for
    /// review once SAP refuses again. A person pressing Post on the van sales console then posts it through
    /// this same poster — and an entry left open would keep the sale in the Exception Center over an invoice
    /// that exists, or have the queue re-confirm it on its next run.
    /// </remarks>
    [Fact]
    public async Task A_post_that_lands_closes_the_queue_entry_that_was_waiting_for_the_sale()
    {
        var reservation = SeedReservation();

        await BuildPoster(confirm: _ => new ConfirmReservationResponseDto { Success = false, Message = "SAP said no." })
            .FiscaliseThenPostAsync(Request(reservation.ReservationId), default);

        // As CreateVanSalesDirectInvoiceHandler queues it, and as PostQueuedVanInvoices then parks it.
        _context.InvoiceQueue.Add(new InvoiceQueueEntity
        {
            ReservationId = reservation.ReservationId,
            ExternalReference = Reference,
            CustomerCode = "VAN008",
            InvoicePayload = "{}",
            Status = InvoiceQueueStatus.RequiresReview,
            SourceSystem = SaleSourceSystems.VanSales,
            FiscalizationSuccess = true,
            FiscalReceiptNumber = "771",
            LastError = "SAP did not take this fiscalised van sale (reservation Failed): SAP said no.",
            ProcessedAt = DateTime.UtcNow
        });
        _context.SaveChanges();

        var outcome = await BuildPoster()
            .FiscaliseThenPostAsync(Request(reservation.ReservationId, mayAlreadyBeFiscalised: true), default);

        Assert.Equal(VanSaleFiscalFirstStatus.Posted, outcome.Status);
        Assert.False(outcome.Adopted);
        Assert.Equal(["sign", "post", "post"], _calls);

        var entry = _context.InvoiceQueue.AsNoTracking().Single(q => q.ExternalReference == Reference);
        Assert.Equal(InvoiceQueueStatus.Completed, entry.Status);
        Assert.Equal(5001, entry.SapDocNum);
        Assert.Equal("9001", entry.SapDocEntry);
        Assert.Null(entry.LastError);
        Assert.NotNull(entry.ProcessedAt);
    }

    [Fact]
    public async Task An_invoice_SAP_already_held_is_reported_as_adopted()
    {
        var reservation = SeedReservation();

        var outcome = await BuildPoster(confirm: _ => new ConfirmReservationResponseDto
            {
                Success = true,
                AlreadyPosted = true,
                Message = "Reservation already posted previously; returning existing SAP invoice",
                SAPDocEntry = 9001,
                SAPDocNum = 5001
            })
            .FiscaliseThenPostAsync(Request(reservation.ReservationId), default);

        Assert.Equal(VanSaleFiscalFirstStatus.Posted, outcome.Status);
        Assert.True(outcome.Adopted);
        Assert.Equal(5001, StoredSale().SapDocNum);
    }
}
