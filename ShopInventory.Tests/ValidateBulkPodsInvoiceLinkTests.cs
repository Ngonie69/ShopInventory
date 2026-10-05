using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.Invoices.Queries.ValidateBulkPods;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins the invoice a sales-order POD check hands back, which Mobile Orders and Van Sales Orders link to.
/// </summary>
/// <remarks>
/// Every van account is a POD-excluded business partner, and the excluded answer used to drop the
/// invoice, so no van sales order ever showed the invoice it had been converted to.
/// </remarks>
public sealed class ValidateBulkPodsInvoiceLinkTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public ValidateBulkPodsInvoiceLinkTests()
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

    [Fact]
    public async Task An_excluded_van_account_order_still_names_its_latest_invoice()
    {
        var handler = CreateHandler(() =>
        [
            Link(83425, "VAN013", invoiceDocNum: 120001, invoiceDocEntry: 9001, "2026-10-05"),
            Link(83425, "VAN013", invoiceDocNum: 120007, invoiceDocEntry: 9007, "2026-10-06")
        ]);

        var result = await handler.Handle(new ValidateBulkPodsQuery([], [83425]), CancellationToken.None);

        var row = Assert.Single(result.Value.Results);
        Assert.False(row.Found);
        Assert.False(row.LookupFailed);
        Assert.StartsWith("Excluded BP", row.ErrorMessage);
        Assert.Equal(120007, row.ResolvedInvoiceDocNum);
        Assert.Equal(9007, row.ResolvedInvoiceDocEntry);
        Assert.Equal(2, row.LinkedInvoiceCount);
        // Nothing is filed against an excluded order, so it carries no POD target.
        Assert.Equal(0, row.DocNum);
        Assert.Null(row.DocEntry);
    }

    [Fact]
    public async Task An_order_not_yet_invoiced_names_no_invoice()
    {
        var handler = CreateHandler(() => []);

        var result = await handler.Handle(new ValidateBulkPodsQuery([], [83425]), CancellationToken.None);

        var row = Assert.Single(result.Value.Results);
        Assert.False(row.Found);
        Assert.Null(row.ResolvedInvoiceDocNum);
    }

    [Fact]
    public async Task A_customer_order_names_its_invoice_as_the_POD_target()
    {
        var handler = CreateHandler(() => [Link(83430, "CUS001", invoiceDocNum: 120010, invoiceDocEntry: 9010, "2026-10-05")]);

        var result = await handler.Handle(new ValidateBulkPodsQuery([], [83430]), CancellationToken.None);

        var row = Assert.Single(result.Value.Results);
        Assert.True(row.Found);
        Assert.Equal(120010, row.ResolvedInvoiceDocNum);
        Assert.Equal(120010, row.DocNum);
    }

    private static Dictionary<string, object?> Link(
        int salesOrderDocNum,
        string cardCode,
        int invoiceDocNum,
        int invoiceDocEntry,
        string invoiceDocDate) => new()
        {
            ["SalesOrderDocEntry"] = salesOrderDocNum + 100000,
            ["SalesOrderDocNum"] = salesOrderDocNum,
            ["CustomerCode"] = cardCode,
            ["CustomerName"] = "Tropical",
            ["InvoiceDocEntry"] = invoiceDocEntry,
            ["InvoiceDocNum"] = invoiceDocNum,
            ["InvoiceDocDate"] = invoiceDocDate
        };

    private ValidateBulkPodsHandler CreateHandler(Func<List<Dictionary<string, object?>>> runSql)
    {
        var sap = StubProxy.For<ISAPServiceLayerClient>((method, _) =>
            method.Name == nameof(ISAPServiceLayerClient.ExecuteParameterisedSqlQueryAsync)
                ? Task.Run(runSql)
                : null);

        var documents = StubProxy.For<IDocumentService>((method, _) =>
            method.Name == nameof(IDocumentService.GetPodStatusByDocEntriesAsync)
                ? Task.FromResult(new Dictionary<int, PodStatusInfo>())
                : null);

        return new ValidateBulkPodsHandler(
            _context,
            sap,
            documents,
            Options.Create(new SAPSettings { Enabled = true }),
            NullLogger<ValidateBulkPodsHandler>.Instance);
    }
}
