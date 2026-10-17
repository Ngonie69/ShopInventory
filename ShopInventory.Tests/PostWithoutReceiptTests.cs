using ErrorOr;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopIntegration.Commands.PostDesktopSaleToSap;
using ShopInventory.Features.Invoices.Commands.FiscalizeInvoice;
using ShopInventory.Features.Invoices.Queries.GetInvoiceByDocEntry;
using ShopInventory.Features.Notifications;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The Admin's fiscal outage override: posting a till or vending sale to SAP before it has a receipt.
///
/// While ZIMRA cannot be reached every till sale sits Failed or Pending and the ordinary rule holds all of
/// them out of SAP. The override lets an Admin post them and leaves the receipt to the sweep, which signs a
/// sale under its own reference. What has to hold for that to be safe is pinned here: only an Admin's own
/// account may use it, it reaches no further than unsigned till and vending sales, the invoice says in SAP
/// that its receipt is owed, and the invoice is claimed by its sale so nothing fiscalises it by DocNum and
/// gives the sale a second receipt.
/// </summary>
public sealed class PostWithoutReceiptTests : IDisposable
{
    private const string Reference = "KEF-FAC-20261017-5773CB1C50FB";
    private static readonly Guid Admin = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Cashier = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly List<CreateInvoiceRequest> _created = [];

    public PostWithoutReceiptTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        _context.Users.Add(Account(Admin, "admin", "Admin"));
        _context.Users.Add(Account(Cashier, "cashier", "Cashier"));
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // ---------------------------------------------------------------
    // The rule
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(SaleSourceSystems.ShopTill, DesktopSaleFiscalizationStatus.Failed)]
    [InlineData(SaleSourceSystems.ShopTill, DesktopSaleFiscalizationStatus.Pending)]
    [InlineData(SaleSourceSystems.Vending, DesktopSaleFiscalizationStatus.Failed)]
    [InlineData(SaleSourceSystems.Vending, DesktopSaleFiscalizationStatus.Pending)]
    [InlineData(SaleSourceSystems.ShopTill, DesktopSaleFiscalizationStatus.Success)]
    public void An_unsigned_or_signed_till_sale_may_be_posted_under_the_override(
        string source, DesktopSaleFiscalizationStatus fiscal)
    {
        Assert.Null(DesktopSalePostEligibility.RefusalWithoutReceipt(
            source, DesktopSaleConsolidationStatus.Pending, fiscal, null));
    }

    [Fact]
    public void A_skipped_sale_stays_refused()
    {
        // Nothing signs a Skipped sale unless a person retries it, so its invoice would be owed forever.
        Assert.NotNull(DesktopSalePostEligibility.RefusalWithoutReceipt(
            SaleSourceSystems.ShopTill, DesktopSaleConsolidationStatus.Pending, DesktopSaleFiscalizationStatus.Skipped, null));
    }

    [Theory]
    [InlineData(SaleSourceSystems.VanSales)]
    [InlineData(SaleSourceSystems.VanSalesOnline)]
    public void A_van_sale_stays_refused(string source)
    {
        Assert.NotNull(DesktopSalePostEligibility.RefusalWithoutReceipt(
            source, DesktopSaleConsolidationStatus.Pending, DesktopSaleFiscalizationStatus.Failed, null));
    }

    [Theory]
    [InlineData(DesktopSaleConsolidationStatus.Consolidated)]
    [InlineData(DesktopSaleConsolidationStatus.Excluded)]
    public void A_sale_in_sap_or_excluded_stays_refused(DesktopSaleConsolidationStatus status)
    {
        Assert.NotNull(DesktopSalePostEligibility.RefusalWithoutReceipt(
            SaleSourceSystems.ShopTill, status, DesktopSaleFiscalizationStatus.Failed, null));
    }

    // ---------------------------------------------------------------
    // The post
    // ---------------------------------------------------------------

    [Fact]
    public async Task Without_the_override_an_unsigned_sale_is_still_refused()
    {
        SeedSale(DesktopSaleFiscalizationStatus.Failed);

        var result = await Handler().Handle(
            new PostDesktopSaleToSapCommand(Admin, Reference), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("DesktopSales.SaleNotPostable", result.FirstError.Code);
        Assert.Empty(_created);
    }

    [Fact]
    public async Task A_cashier_may_not_use_the_override()
    {
        SeedSale(DesktopSaleFiscalizationStatus.Failed);

        var result = await Handler().Handle(
            new PostDesktopSaleToSapCommand(Cashier, Reference, WithoutReceipt: true), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("DesktopSales.PostWithoutReceiptNotPermitted", result.FirstError.Code);
        Assert.Empty(_created);
        Assert.Null(Sale().SapDocNum);
    }

    [Fact]
    public async Task An_admin_posts_an_unsigned_sale_marked_as_owed_its_receipt()
    {
        SeedSale(DesktopSaleFiscalizationStatus.Failed);

        var result = await Handler().Handle(
            new PostDesktopSaleToSapCommand(Admin, Reference, WithoutReceipt: true), CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.Equal(DesktopSalePostOutcomes.Posted, result.Value.Outcome);
        Assert.Contains("fiscal receipt is still owed", result.Value.Message);

        // SAP is told, on the document itself, that the receipt is owed and is the sale's to sign.
        var comments = Assert.Single(_created).Comments;
        Assert.NotNull(comments);
        Assert.Contains(DesktopSaleInvoiceRemarks.PendingReceiptRemark, comments);
        Assert.True(comments.Length <= DesktopSaleInvoiceRemarks.MaxLength);

        // Marked in the same save as the DocNum, and still owed: the sweep is what signs it.
        var sale = Sale();
        Assert.Equal(9000, sale.SapDocNum);
        Assert.NotNull(sale.PostedBeforeReceiptAt);
        Assert.Equal(DesktopSaleFiscalizationStatus.Failed, sale.FiscalizationStatus);
        Assert.Equal(DesktopSaleConsolidationStatus.Consolidated, sale.ConsolidationStatus);
    }

    [Fact]
    public async Task A_signed_sale_posted_with_the_override_flag_is_not_marked()
    {
        // A mixed batch sends the flag for every sale in it. A signed one must post exactly as it always has.
        SeedSale(DesktopSaleFiscalizationStatus.Success, receipt: "1187");

        var result = await Handler().Handle(
            new PostDesktopSaleToSapCommand(Admin, Reference, WithoutReceipt: true), CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        Assert.DoesNotContain("still owed", result.Value.Message);
        Assert.DoesNotContain(DesktopSaleInvoiceRemarks.PendingReceiptRemark, Assert.Single(_created).Comments);
        Assert.Null(Sale().PostedBeforeReceiptAt);
    }

    // ---------------------------------------------------------------
    // The invoice belongs to the sale
    // ---------------------------------------------------------------

    [Fact]
    public async Task The_invoice_is_claimed_by_its_sale_until_the_sale_is_signed()
    {
        SeedSale(DesktopSaleFiscalizationStatus.Failed);
        await Handler().Handle(
            new PostDesktopSaleToSapCommand(Admin, Reference, WithoutReceipt: true), CancellationToken.None);
        _context.ChangeTracker.Clear();

        // Owed: claimed as pending, and not reported as fiscalised.
        Assert.Equal(Reference, await PerSaleInvoiceRegistry.FindReceiptPendingByDocNumAsync(_context, 9000, CancellationToken.None));
        Assert.Null(await PerSaleInvoiceRegistry.FindByDocNumAsync(_context, 9000, CancellationToken.None));

        var invoice = new InvoiceDto { DocNum = 9000, DocEntry = 9000 };
        await FiscalDocumentStatusProjector.EnrichInvoiceAsync(_context, invoice, CancellationToken.None);
        Assert.Equal("Receipt pending", invoice.FiscalizationStatus);
        Assert.False(invoice.IsFiscalized);
        Assert.Equal(Reference, invoice.ReceiptPendingOnSale);

        // Signed later by the sweep: now the ordinary per-sale claim answers, and the pending one stops.
        await _context.DesktopSales
            .Where(s => s.ExternalReferenceId == Reference)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.FiscalizationStatus, DesktopSaleFiscalizationStatus.Success)
                .SetProperty(s => s.FiscalReceiptNumber, "1190"));
        _context.ChangeTracker.Clear();

        Assert.Null(await PerSaleInvoiceRegistry.FindReceiptPendingByDocNumAsync(_context, 9000, CancellationToken.None));
        Assert.Equal(Reference, (await PerSaleInvoiceRegistry.FindByDocNumAsync(_context, 9000, CancellationToken.None))?.ExternalReferenceId);

        var signed = new InvoiceDto { DocNum = 9000, DocEntry = 9000 };
        await FiscalDocumentStatusProjector.EnrichInvoiceAsync(_context, signed, CancellationToken.None);
        Assert.Equal("Fiscalised", signed.FiscalizationStatus);
        Assert.Null(signed.ReceiptPendingOnSale);
    }

    [Fact]
    public async Task The_invoice_cannot_be_fiscalised_by_its_docnum_while_the_receipt_is_owed()
    {
        SeedSale(DesktopSaleFiscalizationStatus.Failed);
        await Handler().Handle(
            new PostDesktopSaleToSapCommand(Admin, Reference, WithoutReceipt: true), CancellationToken.None);
        _context.ChangeTracker.Clear();

        var submitted = new List<int>();
        var result = await FiscaliseInvoice(new InvoiceDto { DocEntry = 9000, DocNum = 9000 }, submitted);

        Assert.True(result.IsError);
        var error = Assert.Single(result.Errors);
        Assert.Equal("Invoice.ReceiptPendingOnSale", error.Code);
        Assert.Equal(ErrorType.Conflict, error.Type);
        Assert.Contains(Reference, error.Description);
        Assert.Empty(submitted);
    }

    [Fact]
    public async Task An_older_unsigned_sale_in_sap_without_the_marker_is_not_claimed()
    {
        // Sales that reached SAP unsigned before this existed were fiscalised from their invoices, if at
        // all. Claiming them would hide the Fiscalise button they still need.
        SeedSale(DesktopSaleFiscalizationStatus.Failed);
        await _context.DesktopSales
            .Where(s => s.ExternalReferenceId == Reference)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.SapDocNum, 8123)
                .SetProperty(s => s.ConsolidationStatus, DesktopSaleConsolidationStatus.Consolidated));

        Assert.Null(await PerSaleInvoiceRegistry.FindReceiptPendingByDocNumAsync(_context, 8123, CancellationToken.None));
        Assert.Empty(await PerSaleInvoiceRegistry.FindReceiptPendingSalesAsync(_context, [8123], CancellationToken.None));
    }

    // ---------------------------------------------------------------

    private static User Account(Guid id, string username, string role) => new()
    {
        Id = id,
        Username = username,
        FirstName = username,
        LastName = "Test",
        PasswordHash = "x",
        Role = role,
        IsActive = true
    };

    private void SeedSale(DesktopSaleFiscalizationStatus fiscal, string? receipt = null)
    {
        _context.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = Reference,
            SourceSystem = SaleSourceSystems.ShopTill,
            CardCode = "CIS006",
            DocDate = DateTime.UtcNow.Date,
            CreatedAt = DateTime.UtcNow,
            TotalAmount = 10.03m,
            VatAmount = 1.35m,
            Currency = "USD",
            WarehouseCode = "KEFSHOP",
            FiscalizationStatus = fiscal,
            FiscalReceiptNumber = receipt,
            ConsolidationStatus = DesktopSaleConsolidationStatus.Pending,
            PaymentMethod = "Cash",
            // Zero, so the settlement step stays out of the way and these stay about the invoice.
            AmountPaid = 0m,
            Lines =
            [
                new DesktopSaleLineEntity
                {
                    LineNum = 1,
                    ItemCode = "CHE011",
                    Quantity = 1m,
                    UnitPrice = 10.03m,
                    LineTotal = 10.03m,
                    WarehouseCode = "KEFSHOP"
                }
            ]
        });
        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    private DesktopSaleEntity Sale() =>
        _context.DesktopSales.AsNoTracking().Single(s => s.ExternalReferenceId == Reference);

    private Task<ErrorOr<FiscalizationResult>> FiscaliseInvoice(InvoiceDto invoice, List<int> submitted)
        => new FiscalizeInvoiceHandler(
                _context,
                StubProxy.For<ISender>((method, args) => args?[0] switch
                {
                    GetInvoiceByDocEntryQuery => Task.FromResult<ErrorOr<InvoiceDto>>(invoice),
                    var request => throw new InvalidOperationException(
                        $"ISender.{method.Name} was called with an unexpected request: {request?.GetType().Name ?? "null"}.")
                }),
                StubProxy.For<IFiscalizationService>((method, args) =>
                {
                    submitted.Add(((InvoiceDto)args![0]!).DocNum);
                    return Task.FromResult(new FiscalizationResult { Success = true });
                }),
                StubProxy.For<IAuditService>((_, _) => Task.CompletedTask),
                Options.Create(new FiscalisationSettings()),
                NullLogger<FiscalizeInvoiceHandler>.Instance)
            .Handle(new FiscalizeInvoiceCommand(invoice.DocEntry, null, "operator"), CancellationToken.None);

    private PostDesktopSaleToSapHandler Handler()
    {
        var docNum = 9000;

        var sap = StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetInvoiceByVanSaleOrderAsync) =>
                (object)Task.FromResult<Invoice?>(null),

            nameof(ISAPServiceLayerClient.CreateInvoiceAsync) => Create((CreateInvoiceRequest)args![0]!),

            _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
        });

        Task<Invoice> Create(CreateInvoiceRequest request)
        {
            _created.Add(request);
            var number = docNum++;
            return Task.FromResult(new Invoice { DocEntry = number, DocNum = number });
        }

        // Neither the device nor the van routes have any business here: the override never signs anything.
        var fiscalisation = StubProxy.For<IFiscalizationService>((method, _) =>
            throw new InvalidOperationException($"The fiscal device must not be asked ({method.Name})."));
        var notifications = StubProxy.For<INotificationService>((method, _) =>
            throw new InvalidOperationException($"INotificationService.{method.Name} was not expected."));
        var reservations = StubProxy.For<IStockReservationService>((method, _) =>
            throw new InvalidOperationException($"IStockReservationService.{method.Name} was not expected."));
        var refusingSap = StubProxy.For<ISAPServiceLayerClient>((method, _) =>
            throw new InvalidOperationException($"The van route must not reach SAP ({method.Name})."));

        var tax = Options.Create(new TaxSettings { VatRate = 0.155m });
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
            refusingSap,
            circuit,
            SaleBatchAllocators.Holding(),
            new StockLedger(_context, Options.Create(new DailyStockSettings()), NullLogger<StockLedger>.Instance),
            SalePostGuards.Backed(_connection),
            DesktopCreditPosters.Idle(_context),
            Options.Create(new VanSalesPostingSettings()),
            NullLogger<VanSalesEndOfDayPostingService>.Instance);

        var poster = new VanSaleFiscalFirstPoster(
            _context,
            reservations,
            new DesktopSaleFiscaliser(fiscalisation, notifications, tax, NoFiscalPrintFormChoices.Instance, NullLogger<DesktopSaleFiscaliser>.Instance),
            DesktopCreditPosters.Idle(_context),
            tax,
            NullLogger<VanSaleFiscalFirstPoster>.Instance);

        return new PostDesktopSaleToSapHandler(
            _context,
            till,
            van,
            poster,
            StubProxy.For<IAuditService>((_, _) => Task.CompletedTask),
            NullLogger<PostDesktopSaleToSapHandler>.Instance);
    }
}
