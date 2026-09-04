using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.DesktopIntegration.Commands.CreateInvoiceDirect;
using ShopInventory.Features.DesktopIntegration.Queries.GenerateEndOfDayReport;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSales;
using ShopInventory.Features.ExceptionCenter;
using ShopInventory.Features.RouteCustomers.Queries.GetRouteCustomerSales;
using ShopInventory.Features.VanSalesCompatibility.Commands.ConvertVanSalesSalesOrderToInvoice;
using ShopInventory.Features.VanSalesCompatibility.Commands.CreateVanSalesDirectInvoice;
using ShopInventory.Features.VanSalesReports.Queries;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Tests;

/// <summary>
/// A van sale made with signal is stamped by the same handset, on the same ZIMRA device, as one made
/// without it. This is what the server does with the receipt that arrives on that path.
///
/// The compliance hole these tests close: the online path posted straight to SAP and left the signed
/// receipt nowhere, so those sales never reached ZIMRA and the device's fiscal day closed short of
/// receipt numbers it had already spent. FDMS reconciles a day against a contiguous chain, so a hole in
/// it does not lose one sale — it stops the day.
///
/// The hazard the fix has to avoid is the mirror image, and it is quieter. The sale is <b>already</b>
/// recorded, as the confirmed <c>StockReservation</c> the invoice posted from. Writing the receipt as an
/// ordinary van <c>DesktopSale</c> would have every report that unions those two tables count the money
/// twice, and the end-of-day posting run put a second invoice in SAP for a sale SAP already has — neither
/// of which throws, errors, or shows up as anything but a larger number. Hence
/// <see cref="SaleSourceSystems.VanSalesOnline"/>, and hence most of what is asserted below.
/// </summary>
public sealed class VanSalesOnlineSignedReceiptTests : IDisposable
{
    private static readonly Guid VanUser = Guid.Parse("55555555-5555-5555-5555-555555555555");

    /// <summary>
    /// A console account for the sales-list cases below. The van account cannot read that list at all —
    /// listing till takings is scoped to the shop consoles and to a till's own shop — and what those
    /// cases are about is which source systems appear, not who may look.
    /// </summary>
    private static readonly Guid SalesReader = Guid.Parse("66666666-6666-6666-6666-666666666666");

    /// <summary>The trading day every case here is written around.</summary>
    private static readonly DateTime Day = new(2026, 8, 10);

    private const int DeviceNumber = 35410;

    private const string VerificationCode = "A1B2C3D4E5F60718";

    /// <summary>qrUrl + deviceId:D10 + receiptDate:ddMMyyyy + globalNo:D10 + verificationCode.</summary>
    private const string QrCode =
        "https://fdms.zimra.co.zw/000003541010082026000000050" + "1" + VerificationCode;

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly RecordingMediator _mediator;
    private readonly FiscalisationSettings _fiscalisation = new()
    {
        Enabled = true,
        Preflight = new FiscalisationPreflightSettings { Mode = FiscalisationPreflightMode.Local }
    };

    public VanSalesOnlineSignedReceiptTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new ApplicationDbContext(options);
        _context.Database.EnsureCreated();
        _mediator = new RecordingMediator(_context);

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
            AssignedCustomerCodes = """["SIM001"]"""
        });
        _context.Users.Add(new User
        {
            Id = SalesReader,
            Username = "console",
            PasswordHash = "x",
            Role = ApplicationRoles.Admin,
            IsActive = true
        });
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    // --- The receipt is taken custody of ---

    /// <summary>
    /// The whole point. The sale posts to SAP as it always did, and the receipt the handset signed is now
    /// stored alongside it, queued for the platform, under a source that no posting route claims.
    /// </summary>
    [Fact]
    public async Task A_stamped_online_sale_stores_its_receipt_against_the_posted_invoice()
    {
        var response = await SellAsync(Stamped("VAN006-INV-20260810-AAA111", globalNo: 501, counter: 4));

        Assert.True(response.Success);

        var sale = await _context.DesktopSales.Include(s => s.Lines).SingleAsync();

        Assert.Equal(SaleSourceSystems.VanSalesOnline, sale.SourceSystem);
        Assert.Equal("VAN006-INV-20260810-AAA111", sale.ExternalReferenceId);

        // SAP already holds the invoice, and the row says so in the only way the posting routes read.
        Assert.Equal(DesktopSaleConsolidationStatus.Consolidated, sale.ConsolidationStatus);
        Assert.Equal(RecordingMediator.DocEntry, sale.SapDocEntry);
        Assert.Equal(RecordingMediator.DocNum, sale.SapDocNum);
        Assert.NotNull(sale.PostedAt);

        // Success, not Pending: a number came off the device's chain and the customer holds the printout.
        Assert.Equal(DesktopSaleFiscalizationStatus.Success, sale.FiscalizationStatus);
        Assert.Equal(DesktopSaleReceiptIngestStatus.Pending, sale.ReceiptIngestStatus);
        Assert.Equal(DeviceNumber, sale.FiscalDeviceId);
        Assert.Equal("19", sale.FiscalDayNo);
        Assert.Equal(501, sale.ReceiptGlobalNo);
        Assert.Equal(4, sale.ReceiptCounter);
    }

    /// <summary>
    /// Every signed value is stored exactly as it arrived. The platform re-derives the payload and refuses
    /// anything that does not hash to the signature, so a value rounded or re-derived on the way in is a
    /// receipt ZIMRA never gets — and its number is already spent.
    /// </summary>
    [Fact]
    public async Task The_signed_values_are_stored_verbatim()
    {
        await SellAsync(Stamped("VAN006-INV-20260810-AAA111", globalNo: 501, counter: 4));

        var sale = await _context.DesktopSales.Include(s => s.Lines).SingleAsync();

        Assert.Equal(new DateTime(2026, 8, 10, 11, 30, 0), sale.ReceiptDate);
        Assert.Equal(new DateTime(2026, 8, 10, 6, 15, 0), sale.FiscalDayOpenedAt);
        Assert.Equal("previous-hash-501", sale.PreviousReceiptHash);
        Assert.Equal("hash-501", sale.DeviceSignatureHash);
        Assert.Equal("signature-501", sale.DeviceSignatureValue);
        Assert.Equal(VerificationCode, sale.FiscalVerificationCode);
        Assert.Equal(QrCode, sale.FiscalQRCode);

        // The line as signed: the tax-inclusive unit price, and the tax the lease supplied at signing.
        var line = Assert.Single(sale.Lines);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal(50m, line.UnitPrice);
        Assert.Equal(100m, line.LineTotal);
        Assert.Equal("Cheese 1kg", line.ItemDescription);
        Assert.Equal(517, line.TaxId);
        Assert.Equal(15.5m, line.TaxPercent);
        Assert.Equal("15.5% Output VAT USD", line.TaxCode);
        Assert.Equal("04031000", line.HsCode);
    }

    /// <summary>
    /// A handset that owns a device must be the only writer on its chain. Letting the server fiscalise a
    /// sale the handset already numbered puts a second signature on that sequence, and FDMS then refuses
    /// the whole fiscal day at upload — not this receipt, the day.
    /// </summary>
    [Fact]
    public async Task The_server_does_not_fiscalise_a_sale_the_handset_stamped()
    {
        await SellAsync(Stamped("VAN006-INV-20260810-AAA111", globalNo: 501, counter: 4));

        Assert.False(_mediator.LastInvoiceRequest.Fiscalize);
    }

    /// <summary>
    /// The one case where the server still fiscalises. A handset too old to stamp owns no device and holds
    /// no chain, so there is nothing to fork — and switching fiscalisation off for it would leave the sale
    /// with no fiscal record at all, which is worse than one on the wrong device.
    /// </summary>
    [Fact]
    public async Task The_server_still_fiscalises_a_sale_no_handset_stamped()
    {
        await SellAsync(Unstamped("VAN006-INV-20260810-BBB222"));

        Assert.True(_mediator.LastInvoiceRequest.Fiscalize);
    }

    // --- The double count this design exists to avoid ---

    /// <summary>
    /// The quiet failure. An online van sale is already counted as its confirmed reservation, so a receipt
    /// row read as a van sale as well would report the day's takings at twice what the van took — with no
    /// error, no empty result and nothing to notice but a bigger number.
    /// </summary>
    [Fact]
    public async Task A_stamped_online_sale_is_counted_once_in_the_van_sales_reports()
    {
        await SellAsync(Stamped("VAN006-INV-20260810-AAA111", globalNo: 501, counter: 4));

        // Both records exist: the reservation the invoice posted from, and the receipt row beside it.
        Assert.Single(await _context.StockReservations.ToListAsync());
        Assert.Single(await _context.DesktopSales.ToListAsync());

        var facts = await VanSalesFactReader.LoadSalesAsync(
            _context, new VanSalesFactFilter(Day, Day), CancellationToken.None);

        var fact = Assert.Single(facts);
        Assert.Equal(VanSaleSource.OnlineInvoice, fact.Source);
        Assert.Equal(100m, fact.TotalAmount);
    }

    /// <summary>
    /// The same double count, on the report a route customer's own history is read from. It unions the two
    /// tables exactly as the fact reader does, and would show the shop every online sale twice.
    /// </summary>
    [Fact]
    public async Task A_stamped_online_sale_is_counted_once_in_the_route_customer_history()
    {
        var customer = new RouteCustomerEntity
        {
            AssignedBusinessPartnerCode = "SIM001",
            Code = "TUCK01",
            Name = "Tuck Shop"
        };

        _context.RouteCustomers.Add(customer);
        await _context.SaveChangesAsync();

        _mediator.RouteCustomer = customer;
        await SellAsync(Stamped("VAN006-INV-20260810-AAA111", globalNo: 501, counter: 4));

        // The receipt row is attributed to the same shop as the reservation, so nothing but the source
        // keeps the two apart.
        var stored = await _context.DesktopSales.SingleAsync();
        stored.RouteCustomerId = customer.Id;
        stored.RouteCustomerCode = customer.Code;
        await _context.SaveChangesAsync();

        var result = await new GetRouteCustomerSalesHandler(_context).Handle(
            new GetRouteCustomerSalesQuery(customer.Id, Day, Day), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(1, result.Value.SaleCount);
        Assert.Equal(100m, result.Value.TotalsByCurrency.Sum(total => total.Gross));
    }

    /// <summary>
    /// The second half of the same hazard, and the expensive one. The end-of-day run posts one SAP invoice
    /// per van sale it finds unposted; finding this row would put a duplicate A/R invoice against a sale
    /// SAP already has, reversible only by a manual credit note.
    /// </summary>
    [Fact]
    public async Task A_stamped_online_sale_is_not_reposted_by_the_end_of_day_run()
    {
        await SellAsync(Stamped("VAN006-INV-20260810-AAA111", globalNo: 501, counter: 4));

        // An ordinary offline van sale on the same day, so the run has real work and the assertion is
        // "it posted that one and not this one" rather than "it did nothing".
        AddOfflineVanSale("VAN006-INV-20260810-OFF001", globalNo: 502);
        await _context.SaveChangesAsync();

        var sap = new RecordingSapClient();
        var result = await new VanSalesEndOfDayPostingService(
            _context,
            sap.Client,
            new SapCircuitBreakerState(Options.Create(new SAPSettings())),
            Options.Create(new VanSalesPostingSettings()),
            NullLogger<VanSalesEndOfDayPostingService>.Instance)
            .PostPendingSalesAsync(Day);

        Assert.Equal(1, result.Posted);
        Assert.Equal(
            "VAN006-INV-20260810-OFF001",
            Assert.Single(sap.Created).U_Van_saleorder);
    }

    // --- The receipt reaches ZIMRA ---

    /// <summary>
    /// The reason the row is written at all. The drain is the only route a handset-signed receipt has to
    /// the platform, and it has to take an online sale's receipt as readily as an offline one's — they are
    /// numbers on the same device's chain, and the platform accepts N+1 only once it holds N.
    /// </summary>
    [Fact]
    public async Task The_receipt_from_an_online_sale_is_drained_to_the_platform()
    {
        await SellAsync(Stamped("VAN006-INV-20260810-AAA111", globalNo: 501, counter: 4));

        var platform = new RecordingPlatform();
        var run = await BuildDrain(platform).IngestPendingReceiptsAsync();

        Assert.Equal(1, run.Ingested);

        var request = Assert.Single(platform.Requests);
        Assert.Equal(DeviceNumber, request.DeviceId);
        Assert.Equal("VAN006-INV-20260810-AAA111", request.InvoiceNo);
        Assert.Equal(19, request.FiscalDayNo);
        Assert.Equal(501, request.ReceiptGlobalNo);
        Assert.Equal(4, request.ReceiptCounter);
        Assert.Equal("previous-hash-501", request.PreviousReceiptHash);
        Assert.Equal("hash-501", request.DeviceSignatureHash);
        Assert.Equal("signature-501", request.DeviceSignatureValue);

        var line = Assert.Single(request.Lines);
        Assert.Equal("Cheese 1kg", line.Name);
        Assert.Equal(50m, line.Price);
        Assert.Equal(517, line.TaxId);
        Assert.Equal(15.5m, line.TaxPercent);
        Assert.Equal("04031000", line.HsCode);

        var sale = await _context.DesktopSales.SingleAsync();
        Assert.Equal(DesktopSaleReceiptIngestStatus.Ingested, sale.ReceiptIngestStatus);
    }

    // --- The handsets that cannot stamp yet ---

    /// <summary>
    /// During the rollout a van on an older build still has to trade, so its sale is accepted and posted.
    /// It is flagged rather than hidden, and — the part that matters — it must not stop the drain: it
    /// consumed no receipt number, so it holds no place in any chain and nothing is waiting behind it.
    /// Treating it as a hole would stop a device that has nothing wrong with it.
    /// </summary>
    [Fact]
    public async Task An_unstamped_online_sale_is_accepted_and_flagged_without_stopping_the_device()
    {
        var response = await SellAsync(Unstamped("VAN006-INV-20260810-BBB222"));
        Assert.True(response.Success);

        var unstamped = await _context.DesktopSales.SingleAsync();
        Assert.Equal(SaleSourceSystems.VanSalesOnline, unstamped.SourceSystem);
        Assert.Equal(DesktopSaleReceiptIngestStatus.Unstamped, unstamped.ReceiptIngestStatus);
        Assert.Equal(DesktopSaleFiscalizationStatus.Failed, unstamped.FiscalizationStatus);
        Assert.NotNull(unstamped.FiscalError);
        Assert.Null(unstamped.FiscalDeviceId);

        // A real receipt on a real device, waiting behind it in the same table.
        AddOfflineVanSale("VAN006-INV-20260810-OFF001", globalNo: 502, signed: true);
        await _context.SaveChangesAsync();

        var platform = new RecordingPlatform();
        var run = await BuildDrain(platform).IngestPendingReceiptsAsync();

        Assert.Equal(1, run.Ingested);
        Assert.Equal(0, run.DevicesStopped);
        Assert.Equal("VAN006-INV-20260810-OFF001", Assert.Single(platform.Requests).InvoiceNo);

        // Never offered, and left exactly as it was — there is nothing to send.
        var after = await _context.DesktopSales.SingleAsync(s => s.ExternalReferenceId == "VAN006-INV-20260810-BBB222");
        Assert.Equal(DesktopSaleReceiptIngestStatus.Unstamped, after.ReceiptIngestStatus);
        Assert.Equal(0, after.ReceiptIngestAttempts);
    }

    /// <summary>
    /// Once the fleet is updated the switch goes on, and an unstamped sale is refused.
    ///
    /// <b>Before the SAP post, which is the whole reason the check sits where it does.</b> Refusing after
    /// would tell the handset no about a sale that already exists in SAP as a real A/R invoice: the rep
    /// keeps the sale and re-sends it, and the invoice sits there with nothing pointing at it.
    /// </summary>
    [Fact]
    public async Task With_stamped_receipts_required_an_unstamped_online_sale_is_refused_before_sap()
    {
        _fiscalisation.RequireStampedVanSales = true;

        var result = await BuildHandler().Handle(
            new CreateVanSalesDirectInvoiceCommand(Unstamped("VAN006-INV-20260810-BBB222"), VanUser),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("VanSalesCompatibility.UnstampedSale", result.FirstError.Code);

        Assert.Empty(_mediator.Sent);
        Assert.Empty(await _context.StockReservations.ToListAsync());
        Assert.Empty(await _context.DesktopSales.ToListAsync());
    }

    /// <summary>The switch is about stamping, not about trading: a stamped sale goes through as normal.</summary>
    [Fact]
    public async Task With_stamped_receipts_required_a_stamped_online_sale_still_posts()
    {
        _fiscalisation.RequireStampedVanSales = true;

        var response = await SellAsync(Stamped("VAN006-INV-20260810-AAA111", globalNo: 501, counter: 4));

        Assert.True(response.Success);
        Assert.Single(await _context.DesktopSales.ToListAsync());
    }

    /// <summary>
    /// The sales-order conversion endpoint shares this request DTO, so it can now be handed a signed
    /// receipt — and it stores none and fiscalises server-side. That combination is a second writer on the
    /// device's chain, so it is refused at the door rather than allowed to fork it.
    /// </summary>
    [Fact]
    public async Task A_stamped_sale_cannot_be_sent_to_the_sales_order_conversion_endpoint()
    {
        var request = Stamped("VAN006-INV-20260810-AAA111", globalNo: 501, counter: 4);
        request.SalesOrderId = 42;

        var result = await new ConvertVanSalesSalesOrderToInvoiceHandler(_context, _mediator).Handle(
            new ConvertVanSalesSalesOrderToInvoiceCommand(request, VanUser), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("VanSalesCompatibility.StampedSaleCannotBeConverted", result.FirstError.Code);
        Assert.Empty(_mediator.Sent);
    }

    // --- Failure isolation ---

    /// <summary>
    /// A handset that loses the reply re-sends, and the reservation answers the second attempt with the
    /// invoice the first one posted. The receipt is already stored from that first attempt, and
    /// <c>ExternalReferenceId</c> is uniquely indexed — so a second write would fail the save and, without
    /// the guard, turn a successful duplicate into a reported failure.
    /// </summary>
    [Fact]
    public async Task A_resent_sale_does_not_write_the_receipt_twice()
    {
        var first = await SellAsync(Stamped("VAN006-INV-20260810-AAA111", globalNo: 501, counter: 4));
        var second = await SellAsync(Stamped("VAN006-INV-20260810-AAA111", globalNo: 501, counter: 4));

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Single(await _context.DesktopSales.ToListAsync());
    }

    /// <summary>
    /// The likeliest way to lose a receipt, not the most exotic. These vans sell at the edge of coverage,
    /// so a handset that drops the connection while waiting for the reply is ordinary — and the window it
    /// drops in is exactly the one between SAP accepting the invoice and this server writing the receipt.
    ///
    /// <para>
    /// ASP.NET binds the handler's <c>CancellationToken</c> to <c>HttpContext.RequestAborted</c>. Passing
    /// it to the write past the commit point makes the disconnect cancel the one thing that must still
    /// happen, and the money is in SAP either way. Same rule as <c>ConsolidateDailySalesHandler</c>'s last
    /// safe abort.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_disconnect_after_the_sap_post_does_not_abort_the_receipt_write()
    {
        using var aborted = new CancellationTokenSource();

        // The handset hangs up the moment SAP has the invoice. Everything before this point ran on a live
        // token, so the test is about the window after the commit and nothing else.
        _mediator.AfterPost = aborted.Cancel;

        var result = await BuildHandler().Handle(
            new CreateVanSalesDirectInvoiceCommand(
                Stamped("VAN006-INV-20260810-AAA111", globalNo: 501, counter: 4), VanUser),
            aborted.Token);

        Assert.False(result.IsError);

        // The invoice exists in SAP. Without the receipt beside it the device's fiscal day closes short of
        // a number it already spent, and FDMS refuses the day rather than the sale.
        var sale = await _context.DesktopSales.SingleAsync();
        Assert.Equal(501, sale.ReceiptGlobalNo);
        Assert.Equal(DesktopSaleReceiptIngestStatus.Pending, sale.ReceiptIngestStatus);
        Assert.Equal(RecordingMediator.DocNum, sale.SapDocNum);

        // And nothing was raised: the write succeeded, so there is no loss to report.
        Assert.Empty(await _context.ExceptionCenterIncidents.ToListAsync());
    }

    // --- A receipt that cannot be stored is put in front of a person ---

    /// <summary>
    /// A log line is not a control. Nothing polls the logs, and this particular loss disables the one
    /// check that would otherwise catch it: <c>CountOutstandingReceiptsAsync</c> reads
    /// <c>DesktopSales</c>, so a receipt with no row there is not outstanding but absent, and the
    /// device-day is auto-closed and uploaded for FDMS to refuse.
    /// </summary>
    [Fact]
    public async Task A_receipt_row_that_fails_to_save_raises_an_incident()
    {
        var interceptor = new FailFirstSaveInterceptor();

        // Its own context so the failure can be injected, over the same connection so both see one
        // database. The handler's own SaveChanges is the first async one on it.
        using var poisoned = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .AddInterceptors(interceptor)
                .Options);

        var result = await new CreateVanSalesDirectInvoiceHandler(
            poisoned,
            new RecordingMediator(poisoned),
            Options.Create(_fiscalisation),
            NullLogger<CreateVanSalesDirectInvoiceHandler>.Instance)
            .Handle(
                new CreateVanSalesDirectInvoiceCommand(
                    Stamped("VAN006-INV-20260810-AAA111", globalNo: 501, counter: 4), VanUser),
                CancellationToken.None);

        // The sale is not failed. The money reached SAP, and a handset told the sale failed re-sends it.
        Assert.False(result.IsError);
        Assert.Empty(await _context.DesktopSales.ToListAsync());

        var incident = Assert.Single(await _context.ExceptionCenterIncidents.ToListAsync());
        Assert.Equal(ExceptionCenterSources.VanSaleReceiptStorage, incident.Source);
        Assert.Equal("VAN006-INV-20260810-AAA111", incident.Reference);
        Assert.Equal("RequiresReview", incident.Status);

        // No retry button: nothing on this server can re-obtain a receipt that exists on a handset.
        Assert.False(incident.CanRetry);

        // The text has to say what is now at stake, or it reads as a failed write of a replaceable row.
        Assert.Contains("cannot hand it to the fiscalisation platform", incident.LastError);
        Assert.Contains("501", incident.LastError);

        // Proof the incident write was a second, separate save rather than the first one succeeding.
        Assert.Equal(2, interceptor.Attempts);
    }

    /// <summary>
    /// Validated before the insert, not discovered by it. The row is subject to
    /// <c>CK_DesktopSaleLines_Quantity_Positive</c> and the non-negative money constraints, and a
    /// constraint violation names a constraint rather than the line that was wrong — a poor thing to be
    /// told about a receipt that is now unrecoverable. The offline handler validates the same shape up
    /// front; this path validated nothing.
    /// </summary>
    [Fact]
    public async Task A_line_that_would_violate_a_check_constraint_is_caught_before_the_insert()
    {
        var request = Stamped("VAN006-INV-20260810-AAA111", globalNo: 501, counter: 4);
        request.Items[0].Quantity = 0;

        var result = await BuildHandler().Handle(
            new CreateVanSalesDirectInvoiceCommand(request, VanUser), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Empty(await _context.DesktopSales.ToListAsync());

        var incident = Assert.Single(await _context.ExceptionCenterIncidents.ToListAsync());

        // Names the line and the value, which is what a person can act on. The constraint name is what
        // the database would have said instead, and it is not in here.
        Assert.Contains("line 0", incident.LastError);
        Assert.Contains("CHE011", incident.LastError);
        Assert.DoesNotContain("CK_DesktopSaleLines", incident.LastError);
    }

    /// <summary>An empty cart is refused for the same reason: the platform will not take a receipt with no lines.</summary>
    [Fact]
    public async Task A_sale_with_no_lines_is_caught_before_the_insert()
    {
        var request = Stamped("VAN006-INV-20260810-AAA111", globalNo: 501, counter: 4);
        request.Items.Clear();

        var result = await BuildHandler().Handle(
            new CreateVanSalesDirectInvoiceCommand(request, VanUser), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Empty(await _context.DesktopSales.ToListAsync());
        Assert.Contains("no line items", Assert.Single(await _context.ExceptionCenterIncidents.ToListAsync()).LastError);
    }

    // --- The cash column means money kept, on both van paths ---

    /// <summary>
    /// <c>amount_paid</c> on this DTO sits beside <c>change</c>, so it is the tender — what the customer
    /// handed over — while the offline van DTO has no <c>change</c> field and sends the settled amount.
    /// Storing the tender would make one column mean two things and inflate every sum over it by the
    /// change given, on this source alone.
    /// </summary>
    [Fact]
    public async Task The_stored_amount_paid_is_what_was_settled_not_what_was_tendered()
    {
        var request = Stamped("VAN006-INV-20260810-AAA111", globalNo: 501, counter: 4);
        request.AmountPaid = 100d;
        request.Change = 7.5d;

        await SellAsync(request);

        Assert.Equal(92.50m, (await _context.DesktopSales.SingleAsync()).AmountPaid);
    }

    /// <summary>
    /// The column is check-constrained non-negative, so a handset reporting change larger than the tender
    /// would otherwise fail the insert and lose the receipt over an arithmetic disagreement.
    /// </summary>
    [Fact]
    public async Task Change_larger_than_the_tender_floors_the_settled_amount_rather_than_losing_the_receipt()
    {
        var request = Stamped("VAN006-INV-20260810-AAA111", globalNo: 501, counter: 4);
        request.AmountPaid = 10d;
        request.Change = 40d;

        await SellAsync(request);

        var sale = await _context.DesktopSales.SingleAsync();
        Assert.Equal(0m, sale.AmountPaid);
        Assert.Equal(501, sale.ReceiptGlobalNo);
    }

    // --- Readers that count money must not pick these rows up ---

    /// <summary>
    /// The end-of-day report is a cash reconciliation, and every figure on it is a sum over
    /// <c>DesktopSales</c> — so the set of rows it reads is the report. It filtered on no source at all,
    /// which meant a source added later was absorbed silently: the online receipt carrier is a sale
    /// already counted as its confirmed reservation and already posted as its own SAP invoice.
    /// </summary>
    [Fact]
    public async Task The_end_of_day_report_leaves_the_online_receipt_carriers_out()
    {
        await SellAsync(Stamped("VAN006-INV-20260810-AAA111", globalNo: 501, counter: 4));

        // Real work on the same day, so the assertion is "it counted that one and not this one".
        AddOfflineVanSale("VAN006-INV-20260810-OFF001", globalNo: 502);
        await _context.SaveChangesAsync();

        var result = await new GenerateEndOfDayReportHandler(_context).Handle(
            new GenerateEndOfDayReportQuery(Day), CancellationToken.None);

        Assert.False(result.IsError);

        var report = result.Value;
        Assert.Equal(1, report.TotalSalesCount);
        Assert.Equal(100m, report.TotalSalesAmount);
        Assert.Equal(13.42m, report.TotalVatAmount);
        Assert.Equal(100m, report.TotalAmountPaid);

        // And it is absent from the breakdown too, not merely netted out of the headline.
        Assert.DoesNotContain(
            report.BusinessPartnerSummaries.SelectMany(bp => bp.IndividualSales),
            sale => sale.ExternalReferenceId == "VAN006-INV-20260810-AAA111");
    }

    /// <summary>
    /// The sales list defaults to the same scope, and says so by answering a caller that names the source.
    /// Excluded by default because every money column on the row describes a sale the caller is already
    /// looking at elsewhere; findable by name because an operator chasing a fiscal reference must be able
    /// to reach it.
    /// </summary>
    [Fact]
    public async Task The_sales_list_excludes_the_online_carriers_by_default_and_returns_them_by_name()
    {
        await SellAsync(Stamped("VAN006-INV-20260810-AAA111", globalNo: 501, counter: 4));
        AddOfflineVanSale("VAN006-INV-20260810-OFF001", globalNo: 502);
        await _context.SaveChangesAsync();

        var handler = new GetDesktopSalesHandler(_context);

        var byDefault = await handler.Handle(new GetDesktopSalesQuery(SalesReader), CancellationToken.None);
        Assert.False(byDefault.IsError);
        Assert.Equal(1, byDefault.Value.TotalCount);
        Assert.Equal("VAN006-INV-20260810-OFF001", Assert.Single(byDefault.Value.Sales).ExternalReferenceId);

        var named = await handler.Handle(
            new GetDesktopSalesQuery(SalesReader, SourceSystem: SaleSourceSystems.VanSalesOnline), CancellationToken.None);
        Assert.False(named.IsError);
        Assert.Equal("VAN006-INV-20260810-AAA111", Assert.Single(named.Value.Sales).ExternalReferenceId);
    }

    // --- Helpers ---

    /// <summary>
    /// Fails the first asynchronous <c>SaveChanges</c> and lets the rest through, which is the shape of a
    /// write that dies between the SAP post and the receipt row. Only the async overload, so the
    /// reservation the stub mediator writes synchronously is untouched.
    /// </summary>
    private sealed class FailFirstSaveInterceptor : SaveChangesInterceptor
    {
        public int Attempts { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Attempts++;

            return Attempts == 1
                ? throw new DbUpdateException("The connection dropped mid-insert.")
                : base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private CreateVanSalesDirectInvoiceHandler BuildHandler() =>
        new(
            _context,
            _mediator,
            Options.Create(_fiscalisation),
            NullLogger<CreateVanSalesDirectInvoiceHandler>.Instance);

    private async Task<VanSalesDirectInvoiceResponse> SellAsync(VanSalesOrderRequest request)
    {
        var result = await BuildHandler().Handle(
            new CreateVanSalesDirectInvoiceCommand(request, VanUser), CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : null);
        return result.Value;
    }

    private VanSalesSignedReceiptIngestService BuildDrain(RecordingPlatform platform) =>
        new(
            _context,
            platform.Client,
            new StubDeviceConfigCache(),
            Options.Create(_fiscalisation),
            NullLogger<VanSalesSignedReceiptIngestService>.Instance);

    /// <summary>One sale, stamped on the handset, in the shape the contract specifies for both paths.</summary>
    private static VanSalesOrderRequest Stamped(string reference, int globalNo, int counter) => new()
    {
        VanOrder = reference,
        CustomerCode = "SIM001",
        Reference = "Tuck Shop",
        Type = "INV",
        Currency = "USD",
        DueDate = "2026-08-10",
        AmountPaid = 100d,
        PaymentMethod = "Cash",
        Items = [Line()],

        FiscalDeviceId = DeviceNumber.ToString(),
        FiscalDayNo = 19,
        ReceiptGlobalNo = globalNo,
        ReceiptCounter = counter,
        VerificationCode = VerificationCode,
        QrCode = QrCode,
        ReceiptDate = new DateTime(2026, 8, 10, 11, 30, 0, DateTimeKind.Unspecified),
        FiscalDayOpenedAt = new DateTime(2026, 8, 10, 6, 15, 0, DateTimeKind.Unspecified),
        PreviousReceiptHash = $"previous-hash-{globalNo}",
        DeviceSignatureHash = $"hash-{globalNo}",
        DeviceSignatureValue = $"signature-{globalNo}"
    };

    /// <summary>The same cart off a handset built before the signing release: no fiscal fields at all.</summary>
    private static VanSalesOrderRequest Unstamped(string reference) => new()
    {
        VanOrder = reference,
        CustomerCode = "SIM001",
        Reference = "Tuck Shop",
        Type = "INV",
        Currency = "USD",
        DueDate = "2026-08-10",
        AmountPaid = 100d,
        PaymentMethod = "Cash",
        Items = [Line()]
    };

    private static VanSalesOrderItemRequest Line() => new()
    {
        Code = "CHE011",
        Description = "Cheese 1kg",
        Quantity = 2,
        // Tax-inclusive, as signed: 2 x 50.00 is the 100.00 printed on the receipt.
        Price = 50d,
        TaxCode = "15.5% Output VAT USD",
        TaxId = 517,
        TaxPercent = 15.5m,
        HsCode = "04031000"
    };

    private void AddOfflineVanSale(string reference, int globalNo, bool signed = false)
    {
        var sale = new DesktopSaleEntity
        {
            ExternalReferenceId = reference,
            SourceSystem = SaleSourceSystems.VanSales,
            CardCode = "SIM001",
            DocDate = Day,
            NumAtCard = reference,
            TotalAmount = 100m,
            VatAmount = 13.42m,
            AmountPaid = 100m,
            Currency = "USD",
            PaymentMethod = "Cash",
            WarehouseCode = "VAN006",
            CostCentreCode = "CC006",
            CreatedBy = VanUser.ToString(),
            ConsolidationStatus = DesktopSaleConsolidationStatus.Pending,
            FiscalizationStatus = DesktopSaleFiscalizationStatus.Success,
            FiscalDeviceId = DeviceNumber,
            FiscalDayNo = "19",
            ReceiptGlobalNo = globalNo,
            ReceiptCounter = globalNo - 497,
            ReceiptIngestStatus = signed
                ? DesktopSaleReceiptIngestStatus.Pending
                : DesktopSaleReceiptIngestStatus.NotApplicable,
            Lines =
            [
                new DesktopSaleLineEntity
                {
                    LineNum = 0,
                    ItemCode = "CHE011",
                    ItemDescription = "Cheese 1kg",
                    Quantity = 2m,
                    UnitPrice = 50m,
                    LineTotal = 100m,
                    WarehouseCode = "VAN006",
                    TaxId = 517,
                    TaxPercent = 15.5m,
                    HsCode = "04031000"
                }
            ]
        };

        if (signed)
        {
            sale.ReceiptDate = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Unspecified);
            sale.FiscalDayOpenedAt = new DateTime(2026, 8, 10, 6, 15, 0, DateTimeKind.Unspecified);
            sale.DeviceSignatureHash = $"hash-{globalNo}";
            sale.DeviceSignatureValue = $"signature-{globalNo}";
        }

        _context.DesktopSales.Add(sale);
    }

    /// <summary>
    /// Stands in for the direct-invoice route: records the invoice request and writes the confirmed
    /// reservation that route leaves behind, because that reservation is the sale's other record and the
    /// double count these tests are about only exists when both are present.
    /// </summary>
    private sealed class RecordingMediator(ApplicationDbContext context) : IMediator
    {
        public const int DocEntry = 4321;
        public const int DocNum = 8765;

        public List<object> Sent { get; } = [];

        /// <summary>Attributed to this shop on the reservation, when a test is about attribution.</summary>
        public RouteCustomerEntity? RouteCustomer { get; set; }

        /// <summary>
        /// Run once the invoice is in SAP and before control returns to the handler — the window a van at
        /// the edge of coverage actually disconnects in.
        /// </summary>
        public Action? AfterPost { get; set; }

        public CreateDesktopInvoiceRequest LastInvoiceRequest =>
            Sent.OfType<CreateInvoiceDirectCommand>().Last().Request;

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Sent.Add(request);

            var command = (CreateInvoiceDirectCommand)(object)request;
            var response = Confirm(command.Request);

            AfterPost?.Invoke();

            var errorOr = typeof(TResponse)
                .GetMethod("op_Implicit", [typeof(ConfirmReservationResponseDto)])!
                .Invoke(null, [response])!;

            return Task.FromResult((TResponse)errorOr);
        }

        private ConfirmReservationResponseDto Confirm(CreateDesktopInvoiceRequest request)
        {
            var existing = context.StockReservations
                .FirstOrDefault(reservation => reservation.ExternalReferenceId == request.ExternalReferenceId);

            if (existing is null)
            {
                context.StockReservations.Add(new StockReservationEntity
                {
                    ReservationId = Guid.NewGuid().ToString(),
                    ExternalReferenceId = request.ExternalReferenceId!,
                    SourceSystem = request.SourceSystem!,
                    DocumentType = ReservationDocumentType.Invoice,
                    CardCode = request.CardCode,
                    CardName = request.CardName,
                    RouteCustomerId = RouteCustomer?.Id,
                    RouteCustomerCode = RouteCustomer?.Code,
                    RouteCustomerName = RouteCustomer?.Name,
                    Currency = request.DocCurrency,
                    PaymentMethod = request.PaymentMethod,
                    TotalValue = request.Lines.Sum(line => (line.UnitPrice ?? 0m) * line.Quantity),
                    Status = ReservationStatus.Confirmed,
                    // 09:00 UTC is 11:00 CAT on the trading day, comfortably inside it either way.
                    CreatedAt = new DateTime(2026, 8, 10, 9, 0, 0, DateTimeKind.Utc),
                    ConfirmedAt = new DateTime(2026, 8, 10, 9, 0, 0, DateTimeKind.Utc),
                    ExpiresAt = new DateTime(2026, 8, 10, 10, 0, 0, DateTimeKind.Utc),
                    CreatedBy = VanUser.ToString(),
                    SAPDocEntry = DocEntry,
                    SAPDocNum = DocNum,
                    Lines = request.Lines.Select(line => new StockReservationLineEntity
                    {
                        LineNum = line.LineNum,
                        ItemCode = line.ItemCode,
                        ItemDescription = line.ItemDescription,
                        OriginalQuantity = line.Quantity,
                        ReservedQuantity = line.Quantity,
                        WarehouseCode = line.WarehouseCode,
                        UnitPrice = line.UnitPrice ?? 0m,
                        LineTotal = (line.UnitPrice ?? 0m) * line.Quantity
                    }).ToList()
                });

                context.SaveChanges();
            }

            return new ConfirmReservationResponseDto
            {
                Success = true,
                Message = "Reservation confirmed successfully",
                ReservationId = Guid.NewGuid().ToString(),
                SAPDocEntry = DocEntry,
                SAPDocNum = DocNum
            };
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest
            => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification
            => throw new NotSupportedException();
    }

    /// <summary>
    /// The fiscalisation platform, recording what it was asked to archive. Only the ingest and preflight
    /// calls are answered — reaching a fiscalise call from a handset-signed receipt would itself be the
    /// bug, so it throws rather than quietly succeeding.
    /// </summary>
    private sealed class RecordingPlatform
    {
        public List<IngestSignedReceiptApiRequest> Requests { get; } = [];

        public IFiscalisationApiClient Client => StubProxy.For<IFiscalisationApiClient>((method, args) =>
            method.Name switch
            {
                nameof(IFiscalisationApiClient.IngestSignedReceiptAsync) =>
                    Ingest((IngestSignedReceiptApiRequest)args![0]!),

                nameof(IFiscalisationApiClient.PreflightSignedReceiptAsync) =>
                    Task.FromResult(new PreflightReceiptApiResponse { Valid = true }),

                _ => throw new InvalidOperationException(
                    $"A receipt signed on a handset must never reach {method.Name}.")
            });

        private object Ingest(IngestSignedReceiptApiRequest request)
        {
            Requests.Add(request);

            return Task.FromResult(new SubmitReceiptApiResponse
            {
                Success = true,
                DeviceId = request.DeviceId,
                FiscalDayNo = request.FiscalDayNo,
                InvoiceNo = request.InvoiceNo ?? string.Empty,
                ReceiptCounter = request.ReceiptCounter,
                ReceiptGlobalNo = request.ReceiptGlobalNo,
                ReceiptId = 9001
            });
        }
    }

    /// <summary>
    /// No device configuration, which is what an unreachable platform looks like. Deliberately the
    /// degraded case: the tax and HS-code rules are pinned in <c>ReceiptPreflightTests</c>, and handing
    /// this one a configuration would make every test above also a test of those.
    /// </summary>
    private sealed class StubDeviceConfigCache : IFiscalDeviceConfigCache
    {
        public Task<FiscalConfigApiResponse?> TryGetAsync(int deviceId, CancellationToken cancellationToken = default)
            => Task.FromResult<FiscalConfigApiResponse?>(null);
    }

    /// <summary>Records what would have gone to SAP, and fails the test on any other SAP call.</summary>
    private sealed class RecordingSapClient
    {
        public List<CreateInvoiceRequest> Created { get; } = [];

        private int _nextDocNum = 1000;

        public ISAPServiceLayerClient Client => StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetInvoiceByVanSaleOrderAsync) => (object)Task.FromResult<Invoice?>(null),
            nameof(ISAPServiceLayerClient.CreateInvoiceAsync) => CreateInvoice((CreateInvoiceRequest)args![0]!),
            _ => throw new InvalidOperationException($"Unexpected SAP call: {method.Name}")
        });

        private Task<Invoice> CreateInvoice(CreateInvoiceRequest request)
        {
            Created.Add(request);
            var docNum = _nextDocNum++;
            return Task.FromResult(new Invoice { DocEntry = docNum, DocNum = docNum });
        }
    }
}
