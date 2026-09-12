using Microsoft.Data.Sqlite;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.DesktopCreditNotes;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Models.Revmax;
using ShopInventory.Services;
using Xunit;

namespace ShopInventory.Tests;

public sealed class DesktopCreditNoteTests : IDisposable
{
    private readonly SqliteConnection connection = new("DataSource=:memory:");
    private readonly ApplicationDbContext db;
    private readonly Gateway gateway = new();
    private readonly Guid caller = Guid.NewGuid();
    private readonly DesktopCreditNoteService service;
    public DesktopCreditNoteTests()
    {
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        db.Users.Add(new User { Id = caller, Username = "credit-admin", PasswordHash = "x", Role = "Admin", IsActive = true });
        db.DesktopSales.Add(new DesktopSaleEntity
        {
            ExternalReferenceId = "TILL-123", CardCode = "C1", WarehouseCode = "W1", Currency = "USD",
            FiscalizationStatus = DesktopSaleFiscalizationStatus.Success, TotalAmount = 100m,
            FiscalDayNo = "525", FiscalReceiptNumber = "456", SourceSystem = "KefalosShopTill"
        });
        db.SaveChanges();
        service = new DesktopCreditNoteService(db, gateway, DesktopCreditPosters.Idle(db),
            StubProxy.For<IAuditService>((m, _) => m.Name == "LogAsync" ? Task.CompletedTask : throw new NotSupportedException()),
            NullLogger<DesktopCreditNoteService>.Instance);
    }

    internal static DesktopCreditSource Source() => new("TILL-123", "USD", 100m, 22862, 525, 456, null,
        [new DesktopCreditLine(1, "Original product", 10m, 10m, 7, 15.5m, "O01", "12345678")]);
    private static CreateDesktopCreditRequest Request(decimal quantity = 2m, string? key = null) =>
        new(key ?? Guid.NewGuid().ToString("N"), "Customer return", [new(1, quantity)]);

    [Fact]
    public async Task A_fiscal_credit_can_be_created_without_any_SAP_document()
    {
        Assert.Null(db.DesktopSales.Single().SapDocEntry);
        var result = await service.CreateAsync(caller, "TILL-123", Request(), default);
        Assert.Equal("Fiscalised", result.Status);
        Assert.Equal(20m, result.Amount);
        Assert.Equal("TILL-123", result.OriginalFiscalNumber);
        Assert.Null(result.SapDocNum);
        Assert.Equal(1, gateway.Submissions);
        Assert.Single(db.DesktopCreditNotes);
    }

    [Fact]
    public async Task A_permanent_request_key_replays_the_same_credit_without_resubmitting()
    {
        var request = Request();
        var first = await service.CreateAsync(caller, "TILL-123", request, default);
        var second = await service.CreateAsync(caller, "TILL-123", request, default);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.Number, second.Number);
        Assert.Equal(1, gateway.Submissions);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(caller, "TILL-123",
            Request(3m, request.RequestKey), default));
    }

    [Fact]
    public async Task An_uncertain_submission_reserves_the_credit_and_is_only_looked_up()
    {
        gateway.LoseReply = true;
        var result = await service.CreateAsync(caller, "TILL-123", Request(8m), default);
        Assert.Equal("ReconciliationRequired", result.Status);
        Assert.Equal(1, gateway.Submissions);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(caller, "TILL-123", Request(3m), default));
        var missing = await service.ReconcileAsync(caller, "TILL-123", result.Id, default);
        Assert.Equal("ReconciliationRequired", missing.Status);
        Assert.Equal(1, gateway.Submissions);
        gateway.Existing = new FiscalizationResult { Success = true, ReceiptGlobalNo = "789", QRCode = "https://example.invalid/receipt" };
        var found = await service.ReconcileAsync(caller, "TILL-123", result.Id, default);
        Assert.Equal("Fiscalised", found.Status);
        Assert.Equal("789", found.ReceiptGlobalNo);
        Assert.Equal(1, gateway.Submissions);
    }

    [Fact]
    public async Task A_saved_plan_exists_before_the_device_is_called()
    {
        gateway.BeforeSubmit = () => Assert.Equal("Submitting", db.DesktopCreditNotes.AsNoTracking().Single().Status);
        await service.CreateAsync(caller, "TILL-123", Request(), default);
    }

    [Fact]
    public async Task A_preflight_refusal_does_not_submit_or_reserve_the_remaining_quantity()
    {
        gateway.Refusal = "Original fiscal day is missing";
        var rejected = await service.CreateAsync(caller, "TILL-123", Request(10m), default);
        Assert.Equal("Rejected", rejected.Status);
        Assert.Equal(0, gateway.Submissions);
        gateway.Refusal = null;
        Assert.Equal("Fiscalised", (await service.CreateAsync(caller, "TILL-123", Request(10m), default)).Status);
    }

    [Fact]
    public async Task Another_warehouse_cannot_read_or_credit_the_sale()
    {
        var shop = new ShopEntity { Code = "OTHER", Name = "Other", WarehouseCode = "W2", BusinessPartnerCode = "C2" };
        db.Shops.Add(shop);
        await db.SaveChangesAsync();
        var cashier = new User { Id = Guid.NewGuid(), Username = "other-cashier", PasswordHash = "x", Role = "Cashier", IsActive = true, ShopId = shop.Id };
        db.Users.Add(cashier);
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PrepareAsync(cashier.Id, "TILL-123", default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CreateAsync(cashier.Id, "TILL-123", Request(), default));
        Assert.Equal(0, gateway.Submissions);
    }

    [Fact]
    public void The_REVMax_payload_uses_original_fiscal_references_and_historical_taxes()
    {
        var plan = DesktopCreditPlanner.Build(Source(), Request(), new Dictionary<int, decimal>(), 0m, DateTime.UtcNow);
        var payload = RevmaxDesktopCreditGateway.BuildRequest(plan, new RevmaxSettings { DefaultRefDeviceId = 22862 });
        Assert.StartsWith("DCN-", payload.InvoiceNumber);
        Assert.Equal("TILL-123", payload.OriginalInvoiceNumber);
        Assert.Equal("02", payload.Istatus);
        Assert.Equal(22862, payload.refDeviceId);
        Assert.Equal(525, payload.refFiscalDayNo);
        Assert.Equal(456, payload.refReceiptGlobalNo);
        Assert.Equal(20m, payload.InvoiceAmount);
        var line = Assert.Single(Assert.IsType<List<RevmaxRequestItem>>(payload.ItemsXml));
        Assert.Equal("7", line.Tax);
        Assert.Equal("15.5", line.TaxR);
        Assert.Equal("10", line.Price);
        Assert.Equal("2", line.Qty);
    }

    [Fact]
    public void Repeated_line_names_do_not_replace_the_original_line_tax_ids()
    {
        var source = Source() with { OriginalTotal = 200m, Lines = [
            new(1, "Same name", 10m, 10m, 7, 15.5m, "O01", null),
            new(2, "Same name", 10m, 10m, 2, 0m, "O0", null)] };
        var request = Request() with { Lines = [new(1, 1), new(2, 1)] };
        var plan = DesktopCreditPlanner.Build(source, request, new Dictionary<int, decimal>(), 0m, DateTime.UtcNow);
        var lines = Assert.IsType<List<RevmaxRequestItem>>(RevmaxDesktopCreditGateway.BuildRequest(plan, new()).ItemsXml);
        Assert.Equal(new[] { "7", "2" }, lines.Select(l => l.Tax));
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(11)]
    public void Invalid_quantities_are_rejected(decimal quantity) => Assert.Throws<InvalidOperationException>(() =>
        DesktopCreditPlanner.Build(Source(), Request(quantity), new Dictionary<int, decimal>(), 0m, DateTime.UtcNow));

    [Fact]
    public void Duplicate_original_lines_are_rejected() => Assert.Throws<InvalidOperationException>(() =>
        DesktopCreditPlanner.Build(Source(), Request() with { Lines = [new(1, 1), new(1, 1)] },
            new Dictionary<int, decimal>(), 0m, DateTime.UtcNow));

    [Fact]
    public void Price_precision_must_not_change_the_credited_amount_on_the_wire()
    {
        var source = Source() with { OriginalTotal = 100.45m,
            Lines = [new(1, "Fractional price", 999999999999m, 100.45m / 999999999999m, 7, 15.5m, "O01", null)] };
        var plan = DesktopCreditPlanner.Build(source, Request(999999999999m), new Dictionary<int, decimal>(), 0, DateTime.UtcNow);
        Assert.Throws<InvalidOperationException>(() => RevmaxDesktopCreditGateway.BuildRequest(plan, new RevmaxSettings()));
    }

    [Theory]
    [InlineData(null)] [InlineData("0")] [InlineData("unknown")]
    public async Task An_unverified_original_fiscal_day_is_not_guessed_from_the_REVMax_envelope(string? day)
    {
        var sale = db.DesktopSales.Single();
        sale.FiscalDayNo = day;
        var device = RealGateway(OriginalReceipt());
        await Assert.ThrowsAsync<InvalidOperationException>(() => device.ReadOriginalAsync(sale, default));
    }

    [Fact]
    public async Task The_original_day_and_discounted_line_values_come_from_the_filed_sale_and_receipt()
    {
        var original = OriginalReceipt();
        original.FiscalDay = "524"; // REVMax's envelope is deliberately not the true recorded day.
        original.Data!.ReceiptLines![0].ReceiptLinePrice = 99m; // The line total is after discount.
        var source = await RealGateway(original).ReadOriginalAsync(db.DesktopSales.Single(), default);
        Assert.Equal(525, source.FiscalDayNo);
        Assert.Equal(10m, Assert.Single(source.Lines).UnitPrice);
        Assert.Equal("TILL-123", source.OriginalFiscalNumber);
    }

    [Fact]
    public async Task A_receipt_from_a_different_device_is_never_used_as_the_credit_base()
    {
        var original = OriginalReceipt();
        original.DeviceID = "999";
        await Assert.ThrowsAsync<InvalidOperationException>(() => RealGateway(original).ReadOriginalAsync(db.DesktopSales.Single(), default));
    }

    [Fact]
    public async Task A_legacy_missing_global_number_is_recovered_from_the_exact_REVMax_receipt()
    {
        var sale = db.DesktopSales.Single();
        sale.FiscalReceiptNumber = null;
        var source = await RealGateway(OriginalReceipt()).ReadOriginalAsync(sale, default);
        Assert.Equal(456, source.ReceiptGlobalNo);
        Assert.Equal(525, source.FiscalDayNo);
    }

    [Fact]
    public async Task An_unknown_REVMax_lookup_cannot_authorise_a_new_submission()
    {
        var device = RealGateway(new InvoiceResponse { Code = "0", Message = "Device unavailable" });
        var plan = DesktopCreditPlanner.Build(Source(), Request(), new Dictionary<int, decimal>(), 0, DateTime.UtcNow);
        await Assert.ThrowsAsync<InvalidOperationException>(() => device.FindAsync(plan, default));
    }

    private static InvoiceResponse OriginalReceipt() => new()
    {
        Code = "1", DeviceID = "22862", FiscalDay = "524", Data = new InvoiceData
        {
            InvoiceNo = "TILL-123", ReceiptType = "FiscalInvoice", ReceiptGlobalNo = 456,
            ReceiptCurrency = "USD", ReceiptTotal = 100m, ReceiptLinesTaxInclusive = true,
            ReceiptLines = [new ReceiptLine { ReceiptLineNo = 1, ReceiptLineType = "Sale", ReceiptLineName = "Original product",
                ReceiptLineQuantity = 10, ReceiptLinePrice = 10, ReceiptLineTotal = 100, TaxID = 7, TaxPercent = 15.5m }]
        }
    };

    private static RevmaxDesktopCreditGateway RealGateway(InvoiceResponse original)
    {
        var client = StubProxy.For<IRevmaxClient>((m, _) => m.Name == "GetInvoiceAsync"
            ? Task.FromResult<InvoiceResponse?>(original) : throw new InvalidOperationException("Unexpected device write"));
        var settings = Options.Create(new RevmaxSettings { Enabled = true, DefaultRefDeviceId = 22862 });
        var selection = Options.Create(new FiscalisationSettings { Provider = FiscalisationProvider.Revmax });
        var device = new RevmaxFiscalizationService(client, settings, Options.Create(new TaxSettings()), selection,
            NullLogger<RevmaxFiscalizationService>.Instance);
        return new RevmaxDesktopCreditGateway(client, device, settings, selection);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task The_actual_TransactM_request_preserves_fiscal_references_and_is_sent_once(HttpStatusCode status)
    {
        var handler = new CaptureHandler(status);
        using var http = new HttpClient(handler);
        var settings = new RevmaxSettings { BaseUrl = "https://revmax.invalid", DefaultRefDeviceId = 22862 };
        var client = new RevmaxClient(http, Options.Create(settings), NullLogger<RevmaxClient>.Instance);
        var plan = DesktopCreditPlanner.Build(Source(), Request(), new Dictionary<int, decimal>(), 0, DateTime.UtcNow);
        var payload = RevmaxDesktopCreditGateway.BuildRequest(plan, settings);
        if (status == HttpStatusCode.OK) await client.TransactMAsync(payload);
        else await Assert.ThrowsAsync<HttpRequestException>(() => client.TransactMAsync(payload));
        Assert.Equal(1, handler.Count);
        Assert.EndsWith("/TransactM", handler.Path);
        using var document = JsonDocument.Parse(handler.Body!);
        Assert.Equal("22862", document.RootElement.GetProperty("refDeviceId").GetString());
        Assert.Equal("525", document.RootElement.GetProperty("refFiscalDayNo").GetString());
        Assert.Equal("456", document.RootElement.GetProperty("refReceiptGlobalNo").GetString());
    }

    private sealed class CaptureHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public int Count;
        public string? Body;
        public string? Path;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Count++;
            Path = request.RequestUri!.AbsolutePath;
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent("{\"Code\":\"1\"}", Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Gateway : IDesktopCreditFiscalGateway
    {
        public int Submissions;
        public bool LoseReply;
        public string? Refusal;
        public Action? BeforeSubmit;
        public FiscalizationResult? Existing;
        public Task<DesktopCreditSource> ReadOriginalAsync(DesktopSaleEntity sale, CancellationToken ct) => Task.FromResult(Source());
        public Task<FiscalizationResult?> FindAsync(DesktopCreditPlan plan, CancellationToken ct) => Task.FromResult(Existing);
        public Task<string?> PreflightAsync(DesktopCreditPlan plan, CancellationToken ct) => Task.FromResult(Refusal);
        public Task<FiscalizationResult> SubmitAsync(DesktopCreditPlan plan, CancellationToken ct)
        {
            BeforeSubmit?.Invoke();
            Submissions++;
            if (LoseReply) throw new HttpRequestException("Reply lost after submission");
            return Task.FromResult(new FiscalizationResult { Success = true, ReceiptGlobalNo = "789" });
        }
    }
    public void Dispose() { db.Dispose(); connection.Dispose(); }
}
