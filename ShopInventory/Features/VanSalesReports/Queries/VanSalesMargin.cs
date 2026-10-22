using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Common.Sales;
using ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesAnalysis;
using ShopInventory.Features.DesktopIntegration.Queries.GetManagementSalesReport;

namespace ShopInventory.Features.VanSalesReports.Queries;

/// <summary>
/// SAP's booked gross profit on van sales, for every van report: one read of the API's copy of the
/// invoices' cost, and a way to find a line's invoice through its sale.
/// </summary>
/// <remarks>
/// A van sale is one invoice on both routes — the reservation's online, the desktop sale's offline — so
/// every row a van report draws can be costed by its sales' invoices. A line carries no invoice of its
/// own; <see cref="Invoices"/> finds it through the sale it belongs to.
/// </remarks>
internal sealed class VanSalesMargin
{
    private readonly IReadOnlyDictionary<(VanSaleSource Source, string Reference), int> _invoiceOfSale;

    private VanSalesMargin(
        ManagementSalesRollup.MarginBook book,
        ManagementMarginStatus status,
        IReadOnlyDictionary<(VanSaleSource Source, string Reference), int> invoiceOfSale)
    {
        Book = book;
        Status = status;
        _invoiceOfSale = invoiceOfSale;
    }

    /// <summary>Costs nothing: what a report holds when it was not asked for margin.</summary>
    public static readonly VanSalesMargin None = new(
        ManagementSalesRollup.MarginBook.Empty,
        new ManagementMarginStatus(false, "Gross profit was not read."),
        new Dictionary<(VanSaleSource, string), int>());

    public ManagementSalesRollup.MarginBook Book { get; }

    /// <summary>Whether margin could be read, and what it means; for the report to show beside it.</summary>
    public ManagementMarginStatus Status { get; }

    /// <summary>The invoice a line's sale was posted as, or null when the sale is not in SAP yet.</summary>
    public int? Invoice(VanSaleLineFact line) =>
        _invoiceOfSale.TryGetValue((line.Source, line.ExternalReferenceId), out var docEntry) ? docEntry : null;

    /// <summary>The (invoice, item) pairs of some lines, for <see cref="ManagementSalesRollup.MarginBook.ForInvoiceItems"/>.</summary>
    public IEnumerable<(int? DocEntry, string ItemCode)> Invoices(IEnumerable<VanSaleLineFact> lines) =>
        lines.Select(line => (Invoice(line), line.ItemCode));

    /// <summary>
    /// The margin on <paramref name="sales"/>, read from <paramref name="costReader"/> under the
    /// analysis's deadline. Never an error: a report whose margin fails says so and shows the rest.
    /// </summary>
    public static async Task<VanSalesMargin> LoadAsync(
        IReadOnlyCollection<VanSaleFact> sales,
        ISaleInvoiceCostReader? costReader,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        if (costReader is null)
        {
            return new VanSalesMargin(
                ManagementSalesRollup.MarginBook.Empty,
                new ManagementMarginStatus(false, "Gross profit is unavailable: this server has no SAP cost reader."),
                new Dictionary<(VanSaleSource, string), int>());
        }

        // Filed as the van source on both routes, so every item lands under the vans' business.
        var posted = sales
            .Where(sale => sale.SapDocEntry is not null)
            .Select(sale => new ManagementSalesRollup.PostedSale(
                sale.SapDocEntry!.Value,
                ManagementSalesRollup.CurrencyKey(sale.Currency),
                sale.WarehouseCode?.Trim() ?? string.Empty,
                sale.VanAccountCode,
                SaleSourceSystems.VanSales,
                sale.UserId.ToString(),
                string.Empty,
                sale.RouteCustomerId))
            .ToList();

        var (book, status) = await ManagementSalesRollup.MarginOfPostedAsync(
            posted,
            [],
            null,
            costReader,
            logger ?? NullLogger.Instance,
            GetDesktopSalesAnalysisHandler.MarginDeadline,
            cancellationToken);

        var invoiceOfSale = sales
            .Where(sale => sale.SapDocEntry is not null)
            .GroupBy(sale => (sale.Source, sale.ExternalReferenceId))
            .ToDictionary(group => group.Key, group => group.First().SapDocEntry!.Value);

        return new VanSalesMargin(book, status, invoiceOfSale);
    }
}
