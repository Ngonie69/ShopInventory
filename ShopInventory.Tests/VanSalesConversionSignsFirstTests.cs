using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Idempotency;
using ShopInventory.Common.Sales;
using ShopInventory.Common.Stock;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopIntegration.Commands.ConvertSalesOrderToInvoice;
using ShopInventory.Features.VanSalesCompatibility;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// A sales order converted on a van is signed before the van is answered, as a direct van sale is.
/// </summary>
/// <remarks>
/// It used to be reserved and queued unsigned, and the queue signed it minutes later. The handset was
/// answered with nothing to print — no verification code, no QR, no sale number — so no converted invoice
/// ever printed a receipt, while a direct sale off the same van printed one every time. What these hold:
/// the van gets its receipt in the reply, the queue gets the invoice already signed and still based on the
/// order, and nothing is ever signed twice.
/// </remarks>
public sealed class VanSalesConversionSignsFirstTests : IDisposable
{
    private const string Rep = "rep-1";
    private const string VanOrder = "VAN006-INV-20261006-C0FFEE";
    private const int OrderId = 7004;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly ApplicationDbContext _context;
    private readonly FakeSalesOrders _orders = new();
    private readonly FiscalStub _fiscal = new();
    private Action? _reservationStarted;
    private bool _stockGone;

    public VanSalesConversionSignsFirstTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _context = new ApplicationDbContext(_options);
        _context.Database.EnsureCreated();

        _orders.Add(OrderId, "SO-7004");
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_van_conversion_answers_with_the_receipt_it_was_signed_with()
    {
        var result = await Handler().Handle(Command(), CancellationToken.None);

        Assert.False(result.IsError, Describe(result));
        Assert.Equal(1, _fiscal.Signed);

        var reply = result.Value;
        Assert.True(reply.Success);
        Assert.True(reply.WasQueued);
        Assert.Null(reply.SapDocNum);
        Assert.Equal("vc-server", reply.VerificationCode);
        Assert.Equal("qr-server", reply.QrCode);
        Assert.Equal("44", reply.FiscalDay);
        Assert.Equal("900", reply.ReceiptGlobalNo);
        Assert.Equal("SN-PLATFORM-1", reply.DeviceSerial);

        // The number the slip is headed with: the platform's own, there before SAP has the invoice.
        var sale = await _context.DesktopSales.SingleAsync();
        Assert.Equal(DesktopSaleNumber.Format(sale.Id), reply.SaleNumber);
        Assert.Equal(SaleSourceSystems.VanSalesOnline, sale.SourceSystem);
        Assert.Equal(DesktopSaleFiscalizationStatus.Success, sale.FiscalizationStatus);
        Assert.Equal(VanOrder, sale.ExternalReferenceId);

        Assert.Equal(1, _orders.FulfilledCount(OrderId));
    }

    [Fact]
    public async Task The_queue_is_handed_the_invoice_already_signed_and_based_on_the_order()
    {
        var result = await Handler().Handle(Command(), CancellationToken.None);
        Assert.False(result.IsError, Describe(result));

        var queued = await _context.InvoiceQueue.SingleAsync();

        // Fiscalized and VanSales is what PostQueuedVanInvoices takes, and what InvoicePostingJob does not
        // sign again. Pending — what the conversion used to write — is signed by the job.
        Assert.Equal(InvoiceQueueStatus.Fiscalized, queued.Status);
        Assert.Equal(SaleSourceSystems.VanSales, queued.SourceSystem);
        Assert.True(queued.FiscalizationSuccess);
        Assert.NotNull(queued.ProcessingStartedAt);

        // The order the invoice is posted on the base of, so SAP closes it.
        Assert.Equal(OrderId, queued.SalesOrderId);

        // The receipt, for U_Fiscal_Code and U_Fiscal_Url on the posted invoice.
        Assert.Equal("vc-server", queued.FiscalVerificationCode);
        Assert.Equal("qr-server", queued.FiscalQrCode);
        Assert.Equal(result.Value.QueueId, queued.Id);
    }

    [Fact]
    public async Task The_van_reply_carries_the_receipt_under_the_names_the_direct_sale_uses()
    {
        var result = await Handler().Handle(Command(), CancellationToken.None);
        Assert.False(result.IsError, Describe(result));

        var json = JsonSerializer.Serialize(VanSalesCompatibilityMapper.MapConvertResponse(result.Value));

        Assert.Contains($"\"sale_number\":\"{result.Value.SaleNumber}\"", json);
        Assert.Contains("\"verification_code\":\"vc-server\"", json);
        Assert.Contains("\"qr_code\":\"qr-server\"", json);
        Assert.Contains("\"fiscal_day\":\"44\"", json);
        Assert.Contains("\"device_serial\":\"SN-PLATFORM-1\"", json);
        Assert.Contains("\"was_queued\":true", json);
    }

    [Fact]
    public async Task A_resend_after_a_lost_reply_is_answered_with_the_same_receipt_and_signs_nothing()
    {
        var handler = Handler();

        var first = await handler.Handle(Command(), CancellationToken.None);
        var resent = await handler.Handle(Command(), CancellationToken.None);

        Assert.False(resent.IsError, Describe(resent));
        Assert.Equal(1, _fiscal.Signed);
        Assert.Equal(first.Value.SaleNumber, resent.Value.SaleNumber);
        Assert.Equal("vc-server", resent.Value.VerificationCode);
        Assert.Equal(1, await _context.InvoiceQueue.CountAsync());
    }

    [Fact]
    public async Task A_resend_after_the_claim_has_expired_is_answered_from_the_receipt_row()
    {
        // The claim lives an hour. Past it the order is Fulfilled, and what answers is the queue entry
        // and the receipt behind it — read, not signed again.
        var first = await Handler().Handle(Command(), CancellationToken.None);
        await _context.IdempotencyRequests.ExecuteDeleteAsync();

        var resent = await Handler().Handle(Command(), CancellationToken.None);

        Assert.False(resent.IsError, Describe(resent));
        Assert.Equal(1, _fiscal.Signed);
        Assert.Equal(first.Value.SaleNumber, resent.Value.SaleNumber);
        Assert.Equal("qr-server", resent.Value.QrCode);
        Assert.Equal(1, await _context.InvoiceQueue.CountAsync());
    }

    [Fact]
    public async Task A_device_that_refuses_raises_nothing_and_the_same_conversion_goes_through_once_it_signs()
    {
        _fiscal.Refuse = true;

        var refused = await Handler().Handle(Command(), CancellationToken.None);

        Assert.True(refused.IsError);
        Assert.Equal("DesktopIntegration.ConversionFiscalisationFailed", refused.FirstError.Code);
        Assert.Contains("could not be fiscalised", refused.FirstError.Description);
        Assert.Empty(await _context.InvoiceQueue.ToListAsync());
        Assert.Equal(0, _orders.FulfilledCount(OrderId));

        // Nothing was sold, so the van's stock is not held for it. 2026-11-03: SO6710 on VAN011 was refused
        // at 09:25 and the handset showed its 24 lines as gone for the next hour.
        await AssertHoldsNothingAsync();

        // The resend carries the same reference, checks the stock again and holds it again under the same row.
        _fiscal.Refuse = false;
        var resent = await Handler().Handle(Command(), CancellationToken.None);

        Assert.False(resent.IsError, Describe(resent));
        Assert.Equal("vc-server", resent.Value.VerificationCode);
        Assert.Equal(1, await _context.StockReservations.CountAsync());
        Assert.Equal(1, await _context.DesktopSales.CountAsync());
        Assert.Equal(1, await _context.InvoiceQueue.CountAsync());

        var reservation = await _context.StockReservations.AsNoTracking().SingleAsync();
        Assert.Equal(ReservationStatus.Pending, reservation.Status);
        Assert.Null(reservation.CancellationReason);
        Assert.Equal(2m, (await AwaitingSapAsync())["CHE011"]);
    }

    [Fact]
    public async Task A_resend_after_the_goods_have_gone_is_refused_and_signs_nothing()
    {
        _fiscal.Refuse = true;
        await Handler().Handle(Command(), CancellationToken.None);

        // The released stock was sold in the meantime.
        _fiscal.Refuse = false;
        _stockGone = true;
        var resent = await Handler().Handle(Command(), CancellationToken.None);

        Assert.True(resent.IsError);
        Assert.Equal("DesktopIntegration.ReservationFailed", resent.FirstError.Code);
        Assert.Contains("Insufficient stock for CHE011", resent.FirstError.Description);
        Assert.Equal(1, _fiscal.Signed);
        Assert.Empty(await _context.InvoiceQueue.ToListAsync());
        Assert.Equal(0, _orders.FulfilledCount(OrderId));
        await AssertHoldsNothingAsync();
    }

    [Fact]
    public async Task A_resend_the_fiscal_device_cannot_be_asked_about_lets_go_of_the_stock_again()
    {
        _fiscal.Refuse = true;
        await Handler().Handle(Command(), CancellationToken.None);

        // The resend takes the stock back, then cannot ask the platform whether it already signed.
        _fiscal.Refuse = false;
        _fiscal.Unreachable = true;
        var resent = await Handler().Handle(Command(), CancellationToken.None);

        Assert.True(resent.IsError);
        Assert.Equal("DesktopIntegration.ConversionFiscalDeviceUnavailable", resent.FirstError.Code);
        Assert.Equal(1, _fiscal.Signed);
        await AssertHoldsNothingAsync();

        // And once it can be asked, the same reference goes through.
        _fiscal.Unreachable = false;
        var third = await Handler().Handle(Command(), CancellationToken.None);

        Assert.False(third.IsError, Describe(third));
        Assert.Equal(1, await _context.StockReservations.CountAsync());
        Assert.Equal(2m, (await AwaitingSapAsync())["CHE011"]);
    }

    private Task<Dictionary<string, decimal>> AwaitingSapAsync() =>
        VanSalesAwaitingSap.ByItemAsync(_context, "VAN006", ["CHE011"], DateTime.UtcNow, 30, CancellationToken.None);

    /// <summary>The refused attempt's reservation is kept, released, and counted nowhere.</summary>
    private async Task AssertHoldsNothingAsync()
    {
        var reservation = await _context.StockReservations.AsNoTracking().SingleAsync();
        Assert.Equal(VanOrder, reservation.ExternalReferenceId);
        Assert.Equal(ReservationStatus.Expired, reservation.Status);
        Assert.StartsWith(UnsignedHold.ReleasedPrefix, reservation.CancellationReason);

        Assert.Empty(await ReservationHolds.LiveAsOf(_context, DateTime.UtcNow).ToListAsync());
        Assert.Empty(await AwaitingSapAsync());
    }

    [Fact]
    public async Task A_device_that_cannot_say_whether_it_signed_holds_the_conversion_for_a_person()
    {
        _fiscal.Unresolved = true;

        var result = await Handler().Handle(Command(), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("DesktopIntegration.ConversionFiscalOutcomeUnknown", result.FirstError.Code);
        Assert.Contains("did not confirm whether", result.FirstError.Description);

        var queued = await _context.InvoiceQueue.SingleAsync();
        Assert.Equal(InvoiceQueueStatus.RequiresReview, queued.Status);
        Assert.Equal(OrderId, queued.SalesOrderId);

        // The receipt may exist, so the order cannot stay open to be converted a second way.
        Assert.Equal(1, _orders.FulfilledCount(OrderId));
    }

    [Fact]
    public async Task A_resend_of_an_unresolved_conversion_is_told_the_same_and_the_device_is_not_asked_again()
    {
        _fiscal.Unresolved = true;
        await Handler().Handle(Command(), CancellationToken.None);

        var resent = await Handler().Handle(Command(), CancellationToken.None);

        Assert.True(resent.IsError);
        Assert.Equal("DesktopIntegration.ConversionFiscalOutcomeUnknown", resent.FirstError.Code);
        Assert.Equal(1, _fiscal.Signed);
    }

    [Fact]
    public async Task A_conversion_that_does_not_ask_to_be_signed_is_queued_unsigned_as_before()
    {
        var result = await Handler().Handle(Command(signFirst: false), CancellationToken.None);

        Assert.False(result.IsError, Describe(result));
        Assert.Equal(0, _fiscal.Signed);
        Assert.Null(result.Value.VerificationCode);

        var queued = await _context.InvoiceQueue.SingleAsync();
        Assert.Equal(InvoiceQueueStatus.Pending, queued.Status);
        Assert.Empty(await _context.DesktopSales.ToListAsync());
    }

    [Fact]
    public async Task A_phone_that_hangs_up_while_stock_is_reserved_does_not_stop_the_conversion()
    {
        // 2026-10-07 10:19: a VAN006 conversion sat in its reservation for 31 s, the handset gave up at
        // 30, ASP.NET cancelled the request token, and the server answered "The operation was canceled"
        // to nobody. The rep was shown "Not confirmed" for a conversion the server had abandoned.
        using var hangUp = new CancellationTokenSource();
        _reservationStarted = () => hangUp.Cancel();

        var result = await Handler().Handle(Command(), hangUp.Token);

        Assert.True(hangUp.IsCancellationRequested);
        Assert.False(result.IsError, Describe(result));
        Assert.Equal(1, _fiscal.Signed);
        Assert.Equal("vc-server", result.Value.VerificationCode);

        var queued = await _context.InvoiceQueue.SingleAsync();
        Assert.Equal(InvoiceQueueStatus.Fiscalized, queued.Status);
        Assert.Equal(OrderId, queued.SalesOrderId);
        Assert.Equal(1, _orders.FulfilledCount(OrderId));

        // The claim is completed, not released, so the handset's resend under the same reference is
        // answered with this receipt rather than starting over.
        var claim = await _context.IdempotencyRequests.AsNoTracking()
            .SingleAsync(r => r.Scope == ConvertSalesOrderToInvoiceHandler.IdempotencyScope);
        Assert.Equal(IdempotencyRequestStatus.Completed, claim.Status);

        // And the order's own claim, so no other reference converts it while that one lives.
        var orderClaim = await _context.IdempotencyRequests.AsNoTracking()
            .SingleAsync(r => r.Scope == ConvertSalesOrderToInvoiceHandler.OrderIdempotencyScope);
        Assert.Equal(IdempotencyRequestStatus.Completed, orderClaim.Status);

        _reservationStarted = null;
        var resent = await Handler().Handle(Command(), CancellationToken.None);

        Assert.False(resent.IsError, Describe(resent));
        Assert.Equal(result.Value.SaleNumber, resent.Value.SaleNumber);
        Assert.Equal(1, _fiscal.Signed);
        Assert.Equal(1, await _context.InvoiceQueue.CountAsync());
    }

    [Fact]
    public async Task A_phone_that_hung_up_before_the_conversion_started_starts_nothing()
    {
        using var goneAlready = new CancellationTokenSource();
        goneAlready.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Handler().Handle(Command(), goneAlready.Token));

        Assert.Empty(await _context.IdempotencyRequests.ToListAsync());
        Assert.Empty(await _context.StockReservations.ToListAsync());
        Assert.Equal(0, _fiscal.Signed);
    }

    private ConvertSalesOrderToInvoiceHandler Handler()
    {
        var reservations = new ReservationStub(_context, () => _reservationStarted?.Invoke(), () => _stockGone).Service;

        return new ConvertSalesOrderToInvoiceHandler(
            _orders.AsService(),
            reservations,
            new InvoiceQueueService(_context, StubProxy.Unused<IStockLedger>(), NullLogger<InvoiceQueueService>.Instance),
            new IdempotencyRequestStore(
                new SingleDbContextScopeFactory(_options),
                Options.Create(new SecuritySettings())),
            NullLogger<ConvertSalesOrderToInvoiceHandler>.Instance,
            _context,
            new VanSaleFiscalFirstPoster(
                _context,
                reservations,
                new DesktopSaleFiscaliser(
                    _fiscal.Service,
                    StubProxy.For<INotificationService>((_, _) => Task.FromResult(0)),
                    Options.Create(new TaxSettings()),
                    NoFiscalPrintFormChoices.Instance,
                    NullLogger<DesktopSaleFiscaliser>.Instance),
                DesktopCreditPosters.Idle(_context),
                Options.Create(new TaxSettings()),
                NullLogger<VanSaleFiscalFirstPoster>.Instance));
    }

    private static ConvertSalesOrderToInvoiceCommand Command(bool signFirst = true) =>
        new(new ConvertSalesOrderToInvoiceRequest
        {
            SalesOrderId = OrderId,
            ExternalReferenceId = VanOrder,
            SourceSystem = SaleSourceSystems.VanSales,
            NumAtCard = VanOrder,
            DocCurrency = "USD",
            PaymentMethod = "Cash",
            Fiscalize = true,
            Lines =
            [
                new CreateDesktopInvoiceLineRequest
                {
                    LineNum = 0,
                    ItemCode = "CHE011",
                    ItemDescription = "Cheese 1kg",
                    Quantity = 2m,
                    UnitPrice = 43.29m,
                    WarehouseCode = "VAN006",
                    CostCentreCode = "CC006",
                    AutoAllocateBatches = true
                }
            ]
        }, Rep, SignBeforeAnswering: signFirst);

    private static string Describe<T>(ErrorOr.ErrorOr<T> result) =>
        result.IsError ? string.Join("; ", result.Errors.Select(e => $"{e.Code}: {e.Description}")) : "ok";

    /// <summary>
    /// The office's fiscal device. A receipt signed once is remembered, so a resend asking for it finds it —
    /// which is what the device does.
    /// </summary>
    private sealed class FiscalStub
    {
        private FiscalizationResult? _issued;

        public int Signed { get; private set; }
        public bool Refuse { get; set; }
        public bool Unresolved { get; set; }

        /// <summary>The platform cannot be asked whether it holds a receipt, as on 2026-11-03.</summary>
        public bool Unreachable { get; set; }

        public IFiscalizationService Service => StubProxy.For<IFiscalizationService>((method, _) => method.Name switch
        {
            nameof(IFiscalizationService.FindPreSapReceiptAsync) => Unreachable
                ? Task.FromException<FiscalizationResult?>(new InvalidOperationException("The service is unavailable."))
                : (object)Task.FromResult(_issued),
            nameof(IFiscalizationService.FiscalizePreSapInvoiceAsync) => Task.FromResult(Sign()),
            _ => throw new InvalidOperationException($"IFiscalizationService.{method.Name} was not expected.")
        });

        private FiscalizationResult Sign()
        {
            Signed++;

            if (Unresolved)
            {
                return new FiscalizationResult
                {
                    Success = false,
                    RequiresReconciliation = true,
                    Message = "The device did not answer"
                };
            }

            if (Refuse)
            {
                return new FiscalizationResult { Success = false, Message = "Fiscal day is not open" };
            }

            return _issued = new FiscalizationResult
            {
                Success = true,
                ReceiptGlobalNo = "900",
                DeviceSerial = "SN-PLATFORM-1",
                VerificationCode = "vc-server",
                QRCode = "qr-server",
                FiscalDayNo = "44"
            };
        }
    }

    /// <summary>
    /// The reservation service over the test's own database, as the real one behaves for a reference it has
    /// seen: a pending reservation under it is handed back rather than a second one made.
    /// </summary>
    /// <remarks>
    /// It honours the token it is handed, as the real service's SAP and database reads do. A stub that
    /// ignored it would let the hang-up test pass against the very code it was written to catch.
    /// </remarks>
    private sealed class ReservationStub(ApplicationDbContext context, Action started, Func<bool> stockGone)
    {
        public IStockReservationService Service => StubProxy.For<IStockReservationService>((method, args) => method.Name switch
        {
            nameof(IStockReservationService.CreateReservationAsync) =>
                (object)CreateAsync((CreateStockReservationRequest)args![0]!, (CancellationToken)args[2]!),
            nameof(IStockReservationService.ValidateStockAvailabilityAsync) => Task.FromResult(Check()),
            _ => throw new InvalidOperationException($"IStockReservationService.{method.Name} was not expected.")
        });

        private (bool IsValid, List<StockReservationErrorDto> Errors) Check() =>
            stockGone()
                ? (false, [new StockReservationErrorDto { Message = "Insufficient stock for CHE011 in VAN006: 0 available, 2 asked" }])
                : (true, []);

        private Task<StockReservationResponseDto> CreateAsync(CreateStockReservationRequest request, CancellationToken cancellationToken)
        {
            started();

            return cancellationToken.IsCancellationRequested
                ? Task.FromCanceled<StockReservationResponseDto>(cancellationToken)
                : Task.FromResult(Create(request));
        }

        private StockReservationResponseDto Create(CreateStockReservationRequest request)
        {
            var existing = context.StockReservations.AsNoTracking()
                .FirstOrDefault(r => r.ExternalReferenceId == request.ExternalReferenceId);

            if (existing is not null)
            {
                return new StockReservationResponseDto
                {
                    Success = existing.Status == ReservationStatus.Pending,
                    Reservation = new StockReservationDto { ReservationId = existing.ReservationId, Status = existing.Status }
                };
            }

            var reservation = new StockReservationEntity
            {
                ExternalReferenceId = request.ExternalReferenceId!,
                SourceSystem = request.SourceSystem!,
                DocumentType = request.DocumentType,
                CardCode = request.CardCode,
                CardName = request.CardName,
                Currency = request.Currency,
                PaymentMethod = request.PaymentMethod,
                Status = ReservationStatus.Pending,
                ExpiresAt = DateTime.UtcNow.AddMinutes(request.ReservationDurationMinutes),
                Lines = request.Lines.Select(line => new StockReservationLineEntity
                {
                    LineNum = line.LineNum,
                    ItemCode = line.ItemCode,
                    ItemDescription = line.ItemDescription,
                    OriginalQuantity = line.Quantity,
                    ReservedQuantity = line.Quantity,
                    UnitPrice = line.UnitPrice,
                    LineTotal = line.UnitPrice * line.Quantity,
                    WarehouseCode = line.WarehouseCode,
                    CostCentreCode = line.CostCentreCode
                }).ToList()
            };

            context.StockReservations.Add(reservation);
            context.SaveChanges();

            return new StockReservationResponseDto
            {
                Success = true,
                Reservation = new StockReservationDto { ReservationId = reservation.ReservationId, Status = reservation.Status }
            };
        }
    }

    private sealed class FakeSalesOrders
    {
        private readonly Dictionary<int, SalesOrderDto> _orders = [];
        private readonly Dictionary<int, int> _fulfilled = [];

        public void Add(int id, string number) => _orders[id] = new SalesOrderDto
        {
            Id = id,
            OrderNumber = number,
            CardCode = "C001",
            CardName = "Tuck Shop",
            Status = SalesOrderStatus.Approved,
            Currency = "USD",
            WarehouseCode = "VAN006",
            Lines = [new SalesOrderLineDto { ItemCode = "CHE011", Quantity = 2m, UnitPrice = 43.29m }]
        };

        public int FulfilledCount(int id) => _fulfilled.GetValueOrDefault(id);

        public ISalesOrderService AsService() => StubProxy.For<ISalesOrderService>((method, args) => method.Name switch
        {
            nameof(ISalesOrderService.GetByIdFromLocalAsync) =>
                Task.FromResult(_orders.GetValueOrDefault((int)args![0]!)),
            nameof(ISalesOrderService.MarkAsFulfilledAsync) => Fulfil((int)args![0]!),
            _ => throw new InvalidOperationException($"ISalesOrderService.{method.Name} is not used by the conversion.")
        });

        private Task<SalesOrderDto> Fulfil(int id)
        {
            _fulfilled[id] = FulfilledCount(id) + 1;
            _orders[id].Status = SalesOrderStatus.Fulfilled;
            return Task.FromResult(_orders[id]);
        }
    }
}
