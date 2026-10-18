using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Caching;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// A sale an Admin posted to SAP before its receipt, once it is signed: its SAP invoice gets the fiscal
/// code and QR URL, and its remarks stop saying the receipt is pending. Until this, nothing went back to
/// the invoice, so it printed with no QR code and still said "fiscal receipt pending".
/// </summary>
public sealed class DesktopSaleSapReceiptWriterTests : IDisposable
{
    private const string Reference = "KEF-FAC-20261017-5773CB1C50FB";
    private const int DocEntry = 51234;
    private const int DocNum = 790100;
    private const string Code = "ABCD1234EFGH5678";
    private const string Url = "https://fdms.zimra.co.zw/12345/1017/0001190/ABCD1234EFGH5678";

    private static readonly string PendingComments =
        $"Shop till, KEFSHOP | Ref {Reference} | {DesktopSaleInvoiceRemarks.PendingReceiptRemark} | Paid Cash";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly FakeSap _sap = new();

    public DesktopSaleSapReceiptWriterTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_signed_sale_gives_its_invoice_the_code_the_url_and_the_receipt_remark()
    {
        Seed(DesktopSaleFiscalizationStatus.Success);
        _sap.Invoice = Invoice(PendingComments);

        var result = await Writer().WriteOwedReceiptsAsync();

        Assert.Equal(1, result.Written);
        var patch = Assert.Single(_sap.Patches);
        Assert.Equal(DocEntry, patch.DocEntry);
        Assert.Equal("ABCD-1234-EFGH-5678", patch.Fields["U_Fiscal_Code"]);
        Assert.Equal(Url, patch.Fields["U_Fiscal_Url"]);

        // Swapped in place: everything else in the remark survives.
        var comments = patch.Fields["Comments"];
        Assert.DoesNotContain("FISCAL RECEIPT PENDING", comments);
        Assert.Contains("receipt 1190", comments);
        Assert.StartsWith("Shop till, KEFSHOP | Ref ", comments);
        Assert.EndsWith(" | Paid Cash", comments);

        var sale = Sale();
        Assert.NotNull(sale.SapReceiptWrittenAt);
        Assert.Null(sale.SapReceiptWriteError);
    }

    [Fact]
    public async Task A_written_invoice_is_not_asked_about_again()
    {
        Seed(DesktopSaleFiscalizationStatus.Success);
        _sap.Invoice = Invoice(PendingComments);

        await Writer().WriteOwedReceiptsAsync();
        _sap.Reads = 0;
        var second = await Writer().WriteOwedReceiptsAsync();

        Assert.Equal(0, second.Written);
        Assert.Equal(0, _sap.Reads);
        Assert.Single(_sap.Patches);
    }

    [Theory]
    [InlineData(DesktopSaleFiscalizationStatus.Failed, true)]
    [InlineData(DesktopSaleFiscalizationStatus.Pending, true)]
    [InlineData(DesktopSaleFiscalizationStatus.Success, false)]
    public async Task Only_a_signed_sale_that_went_out_before_its_receipt_is_taken(
        DesktopSaleFiscalizationStatus status, bool postedBeforeReceipt)
    {
        // Unsigned: nothing to write yet. Signed but posted after its receipt: the POST carried it.
        Seed(status, postedBeforeReceipt);
        _sap.Invoice = Invoice(PendingComments);

        await Writer().WriteOwedReceiptsAsync();

        Assert.Equal(0, _sap.Reads);
        Assert.Empty(_sap.Patches);
    }

    [Fact]
    public async Task A_SAP_refusal_is_recorded_and_the_sale_stays_fiscalised()
    {
        Seed(DesktopSaleFiscalizationStatus.Success);
        _sap.Invoice = Invoice(PendingComments);
        _sap.PatchFails = true;

        var result = await Writer().WriteOwedReceiptsAsync();

        Assert.Equal(1, result.Failed);
        var sale = Sale();
        Assert.Equal(DesktopSaleFiscalizationStatus.Success, sale.FiscalizationStatus);
        Assert.Equal("1190", sale.FiscalReceiptNumber);
        Assert.Null(sale.SapReceiptWrittenAt);
        Assert.Equal(1, sale.SapReceiptWriteAttempts);
        Assert.Contains("SAP said no", sale.SapReceiptWriteError);
    }

    [Fact]
    public async Task It_stops_asking_once_the_attempts_run_out()
    {
        Seed(DesktopSaleFiscalizationStatus.Success);
        _sap.Invoice = Invoice(PendingComments);
        _sap.PatchFails = true;
        var settings = new DesktopSalePostingSettings { MaxPostingAttempts = 2 };

        await Writer(settings).WriteOwedReceiptsAsync();
        await Writer(settings).WriteOwedReceiptsAsync();
        _sap.Reads = 0;
        await Writer(settings).WriteOwedReceiptsAsync();

        Assert.Equal(0, _sap.Reads);
        Assert.Equal(2, Sale().SapReceiptWriteAttempts);
    }

    [Fact]
    public async Task An_invoice_whose_DocNum_is_not_the_sale_s_is_left_alone()
    {
        Seed(DesktopSaleFiscalizationStatus.Success);
        _sap.Invoice = Invoice(PendingComments, docNum: "790999");

        await Writer().WriteOwedReceiptsAsync();

        Assert.Empty(_sap.Patches);
        Assert.Contains("790999", Sale().SapReceiptWriteError);
    }

    [Fact]
    public async Task Another_fiscal_code_already_on_the_invoice_is_never_overwritten()
    {
        // Somebody fiscalised the invoice in SAP too: two receipts, which a person has to look at.
        Seed(DesktopSaleFiscalizationStatus.Success);
        _sap.Invoice = Invoice(PendingComments, code: "ZZZZ-ZZZZ-ZZZZ-ZZZZ");

        await Writer().WriteOwedReceiptsAsync();

        Assert.Empty(_sap.Patches);
        Assert.Null(Sale().SapReceiptWrittenAt);
        Assert.Contains("ZZZZ-ZZZZ-ZZZZ-ZZZZ", Sale().SapReceiptWriteError);
    }

    [Fact]
    public async Task This_receipt_s_own_code_already_there_is_not_a_conflict()
    {
        Seed(DesktopSaleFiscalizationStatus.Success);
        _sap.Invoice = Invoice(PendingComments, code: "ABCD-1234-EFGH-5678");

        await Writer().WriteOwedReceiptsAsync();

        var patch = Assert.Single(_sap.Patches);
        Assert.False(patch.Fields.ContainsKey("U_Fiscal_Code"));
        Assert.True(patch.Fields.ContainsKey("U_Fiscal_Url"));
        Assert.NotNull(Sale().SapReceiptWrittenAt);
    }

    [Fact]
    public async Task Remarks_a_person_rewrote_are_left_as_they_are()
    {
        Seed(DesktopSaleFiscalizationStatus.Success);
        _sap.Invoice = Invoice("Checked by finance");

        await Writer().WriteOwedReceiptsAsync();

        var patch = Assert.Single(_sap.Patches);
        Assert.False(patch.Fields.ContainsKey("Comments"));
    }

    [Fact]
    public async Task Without_the_UDFs_configured_only_the_remark_is_written()
    {
        // As in Development: the test company does not define the fields, and naming them would fail the PATCH.
        Seed(DesktopSaleFiscalizationStatus.Success);
        _sap.Invoice = Invoice(PendingComments);

        await Writer(udf: new FiscalisationUdfSettings()).WriteOwedReceiptsAsync();

        var patch = Assert.Single(_sap.Patches);
        Assert.Equal(["Comments"], patch.Fields.Keys);
        Assert.DoesNotContain("U_Fiscal_Code", _sap.LastRead!);
    }

    [Fact]
    public async Task The_client_reads_only_the_named_fields_and_blank_reads_as_null()
    {
        var sap = new ServiceLayer(HttpStatusCode.OK,
            """{"DocNum":790100,"Comments":"x","U_Fiscal_Code":"","U_Fiscal_Url":null}""");

        var fields = await RealClient(sap).GetInvoiceFieldsAsync(DocEntry, ["DocNum", "Comments", "U_Fiscal_Code", "U_Fiscal_Url"]);

        Assert.Equal("GET /b1s/v1/Invoices(51234)?$select=DocNum,Comments,U_Fiscal_Code,U_Fiscal_Url", sap.Requests.Single());
        Assert.NotNull(fields);
        Assert.Equal("790100", fields["DocNum"]);
        Assert.Equal("x", fields["Comments"]);
        Assert.Null(fields["U_Fiscal_Code"]);
        Assert.Null(fields["U_Fiscal_Url"]);
    }

    [Fact]
    public async Task The_client_patches_only_the_fields_it_is_given()
    {
        var sap = new ServiceLayer(HttpStatusCode.NoContent, "");

        await RealClient(sap).UpdateInvoiceFieldsAsync(DocEntry, new Dictionary<string, string>
        {
            ["U_Fiscal_Code"] = "ABCD-1234-EFGH-5678",
            ["Comments"] = "Fiscal receipt 1190"
        });

        Assert.Equal("PATCH /b1s/v1/Invoices(51234)", sap.Requests.Single());
        using var body = JsonDocument.Parse(sap.LastBody!);
        Assert.Equal(["U_Fiscal_Code", "Comments"], body.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("ABCD-1234-EFGH-5678", body.RootElement.GetProperty("U_Fiscal_Code").GetString());
    }

    private static SAPServiceLayerClient RealClient(ServiceLayer sap)
    {
        var httpClient = new HttpClient(sap) { BaseAddress = new Uri("https://sap.invalid/b1s/v1/") };
        var services = new ServiceCollection().BuildServiceProvider();

        return new SAPServiceLayerClient(
            httpClient,
            new SingleClientFactory(httpClient),
            Options.Create(new SAPSettings { ServiceLayerUrl = "https://sap.invalid/b1s/v1/" }),
            new StubHostEnvironment(),
            NullLogger<SAPServiceLayerClient>.Instance,
            new MemoryCache(new MemoryCacheOptions()),
            new CacheSyncStateRecorder(
                services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<CacheSyncStateRecorder>.Instance),
            StubProxy.Unused<ISapItemUomMappingStore>());
    }

    /// <summary>Answers the login, then records every other request and answers it with one reply.</summary>
    private sealed class ServiceLayer(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var target = Uri.UnescapeDataString(request.RequestUri!.PathAndQuery);

            if (target.EndsWith("/Login", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"SessionId\":\"test-session\"}", Encoding.UTF8, "application/json")
                };
            }

            Requests.Add($"{request.Method} {target}");
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "ShopInventory.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private DesktopSaleSapReceiptWriter Writer(
        DesktopSalePostingSettings? settings = null, FiscalisationUdfSettings? udf = null) =>
        new(
            _context,
            _sap.AsClient(),
            new SapCircuitBreakerState(Options.Create(new SAPSettings())),
            Options.Create(new FiscalisationSettings
            {
                Udf = udf ?? new FiscalisationUdfSettings { FiscalCodeField = "U_Fiscal_Code", FiscalUrlField = "U_Fiscal_Url" }
            }),
            Options.Create(settings ?? new DesktopSalePostingSettings()),
            NullLogger<DesktopSaleSapReceiptWriter>.Instance);

    private static Dictionary<string, string?> Invoice(string comments, string docNum = "790100", string? code = null) => new()
    {
        ["DocNum"] = docNum,
        ["Comments"] = comments,
        ["U_Fiscal_Code"] = code,
        ["U_Fiscal_Url"] = null
    };

    private void Seed(DesktopSaleFiscalizationStatus fiscal, bool postedBeforeReceipt = true)
    {
        var signed = fiscal == DesktopSaleFiscalizationStatus.Success;

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
            FiscalReceiptNumber = signed ? "1190" : null,
            FiscalVerificationCode = signed ? Code : null,
            FiscalQRCode = signed ? Url : null,
            ConsolidationStatus = DesktopSaleConsolidationStatus.Consolidated,
            SapDocEntry = DocEntry,
            SapDocNum = DocNum,
            PostedBeforeReceiptAt = postedBeforeReceipt ? DateTime.UtcNow.AddHours(-2) : null,
            PaymentMethod = "Cash"
        });
        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    private DesktopSaleEntity Sale() =>
        _context.DesktopSales.AsNoTracking().Single(s => s.ExternalReferenceId == Reference);

    private sealed class FakeSap
    {
        public Dictionary<string, string?>? Invoice { get; set; }
        public bool PatchFails { get; set; }
        public int Reads { get; set; }
        public IReadOnlyCollection<string>? LastRead { get; private set; }
        public List<(int DocEntry, IReadOnlyDictionary<string, string> Fields)> Patches { get; } = [];

        public ISAPServiceLayerClient AsClient() =>
            StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
            {
                nameof(ISAPServiceLayerClient.GetInvoiceFieldsAsync) => Read((int)args![0]!, (IReadOnlyCollection<string>)args[1]!),
                nameof(ISAPServiceLayerClient.UpdateInvoiceFieldsAsync) => Patch((int)args![0]!, (IReadOnlyDictionary<string, string>)args[1]!),
                _ => throw new InvalidOperationException($"ISAPServiceLayerClient.{method.Name} was not expected.")
            });

        private Task<IReadOnlyDictionary<string, string?>?> Read(int docEntry, IReadOnlyCollection<string> fields)
        {
            Reads++;
            LastRead = fields.ToList();
            IReadOnlyDictionary<string, string?>? answer = docEntry == DocEntry && Invoice is not null
                ? fields.ToDictionary(f => f, f => Invoice.GetValueOrDefault(f))
                : null;
            return Task.FromResult(answer);
        }

        private Task Patch(int docEntry, IReadOnlyDictionary<string, string> fields)
        {
            if (PatchFails)
            {
                throw new Exception("Failed to update invoice: 400 - SAP said no");
            }

            Patches.Add((docEntry, new Dictionary<string, string>(fields)));
            return Task.CompletedTask;
        }
    }
}
