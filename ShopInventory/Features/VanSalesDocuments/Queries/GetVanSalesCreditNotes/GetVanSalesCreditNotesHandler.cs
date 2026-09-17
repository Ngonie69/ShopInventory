using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.VanSalesDocuments.Queries.GetVanSalesInvoices;
using ShopInventory.Features.VanSalesReports.Queries;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesDocuments.Queries.GetVanSalesCreditNotes;

public sealed class GetVanSalesCreditNotesHandler(
    ApplicationDbContext db,
    ICreditNoteProjectionSyncService creditNoteProjection
) : IRequestHandler<GetVanSalesCreditNotesQuery, ErrorOr<VanSalesCreditNotesResult>>
{
    /// <summary>SAP's object type for an A/R invoice, as a credit memo line names its base document.</summary>
    private const int ArInvoiceObjectType = 13;

    public async Task<ErrorOr<VanSalesCreditNotesResult>> Handle(
        GetVanSalesCreditNotesQuery request,
        CancellationToken cancellationToken)
    {
        var from = request.FromDate.Date;
        var to = request.ToDate.Date;

        if (to < from)
        {
            return Error.Validation("VanSalesDocuments.InvalidRange", "The end date is before the start date.");
        }

        if ((to - from).TotalDays >= GetVanSalesInvoicesHandler.MaxDays)
        {
            return Error.Validation(
                "VanSalesDocuments.RangeTooLong",
                $"Choose a period of at most {GetVanSalesInvoicesHandler.MaxDays} days.");
        }

        if (request.State is not null && !VanSalesDocumentStates.IsKnown(request.State))
        {
            return Error.Validation("VanSalesDocuments.UnknownState", $"'{request.State}' is not a state.");
        }

        var origin = request.Origin?.Trim().ToLowerInvariant() switch
        {
            null or "" => null,
            "sap" => "SAP",
            "till" => "Till",
            _ => "?"
        };

        if (origin == "?")
        {
            return Error.Validation("VanSalesDocuments.UnknownOrigin", $"'{request.Origin}' is not an origin.");
        }

        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, GetVanSalesInvoicesHandler.MaxPageSize);
        var (windowStartUtc, windowEndUtc) = VanSalesFacts.ToUtcWindow(from, to);

        var memos = await db.SapCreditNoteSnapshots
            .AsNoTracking()
            .Where(c => c.DocDate >= from && c.DocDate < to.AddDays(1))
            .Select(c => new
            {
                c.SapDocEntry,
                c.SapDocNum,
                c.DocDate,
                c.CardCode,
                c.CardName,
                c.DocCurrency,
                c.Comments,
                c.DocTotal,
                c.VatSum,
                c.IsCancelled
            })
            .ToListAsync(cancellationToken);

        // The lines on their own, flat. Projecting them as a collection inside the header query needs a lateral
        // join, which SQLite cannot run and PostgreSQL runs once per memo.
        var memoEntries = memos.Select(m => m.SapDocEntry).ToList();

        var memoLines = memoEntries.Count == 0
            ? []
            : await db.SapCreditNoteSnapshots
                .AsNoTracking()
                .Where(c => memoEntries.Contains(c.SapDocEntry))
                .SelectMany(c => c.Lines)
                .Select(l => new { l.CreditNoteDocEntry, l.BaseType, l.BaseEntry, l.CreditReason })
                .ToListAsync(cancellationToken);

        var basesByMemo = memoLines
            .Where(l => l.BaseType == ArInvoiceObjectType && l.BaseEntry != null)
            .GroupBy(l => l.CreditNoteDocEntry)
            .ToDictionary(g => g.Key, g => g.Select(l => l.BaseEntry!.Value).Distinct().ToList());

        var reasonByMemo = memoLines
            .Where(l => !string.IsNullOrWhiteSpace(l.CreditReason))
            .GroupBy(l => l.CreditNoteDocEntry)
            .ToDictionary(g => g.Key, g => g.First().CreditReason);

        List<int> BasesOf(int docEntry) => basesByMemo.TryGetValue(docEntry, out var bases) ? bases : [];

        var baseEntries = basesByMemo.Values.SelectMany(b => b).Distinct().ToList();
        var vanInvoices = await LoadVanInvoicesAsync(baseEntries, cancellationToken);

        var tillCredits = await db.DesktopCreditNotes
            .AsNoTracking()
            .Where(c => c.Sale.SourceSystem == SaleSourceSystems.VanSales
                        && c.CreatedAtUtc >= windowStartUtc
                        && c.CreatedAtUtc < windowEndUtc
                        && c.Status != DesktopCreditStatuses.Rejected)
            .Select(c => new
            {
                c.Id,
                c.Number,
                c.Amount,
                c.Currency,
                c.Reason,
                c.Status,
                c.SapStatus,
                c.SapDocEntry,
                c.SapDocNum,
                c.SapError,
                c.Message,
                c.CreatedAtUtc,
                SaleReference = c.Sale.ExternalReferenceId,
                SaleDocNum = c.Sale.SapDocNum,
                CardCode = c.Sale.RouteCustomerCode ?? c.Sale.CardCode,
                CustomerName = c.Sale.RouteCustomerName ?? c.Sale.CardName,
                SaleCreatedBy = c.Sale.CreatedBy,
                SaleAmount = c.Sale.TotalAmount,
                SaleCurrency = c.Sale.Currency,
                SaleDocDate = c.Sale.DocDate,
                SaleSource = c.Sale.SourceSystem,
                SaleWarehouse = c.Sale.WarehouseCode
            })
            .ToListAsync(cancellationToken);

        var tillCreditByDocEntry = tillCredits
            .Where(c => c.SapDocEntry is not null)
            .GroupBy(c => c.SapDocEntry!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        var repNames = await LoadRepNamesAsync(
            vanInvoices.Values.Select(v => v.CreatedBy).Concat(tillCredits.Select(c => c.SaleCreatedBy)),
            cancellationToken);

        // SAP memos that credit at least one van invoice.
        var vanMemos = memos.Where(m => BasesOf(m.SapDocEntry).Any(vanInvoices.ContainsKey)).ToList();

        var fiscalProbes = vanMemos
            .Select(m => new CreditNoteDto { SAPDocNum = m.SapDocNum })
            .ToList();
        await FiscalDocumentStatusProjector.EnrichCreditNotesAsync(db, fiscalProbes, cancellationToken);

        var rows = new List<VanSalesCreditNoteRow>();

        for (var i = 0; i < vanMemos.Count; i++)
        {
            var memo = vanMemos[i];
            var probe = fiscalProbes[i];
            tillCreditByDocEntry.TryGetValue(memo.SapDocEntry, out var tillCredit);

            // Filed at the till before SAP saw it: the memo's own DocNum finds nothing in the fiscal log, but
            // the credit that became it carries the receipt.
            var fiscalised = probe.IsFiscalized == true || tillCredit?.Status == DesktopCreditStatuses.Fiscalised;

            var credited = BasesOf(memo.SapDocEntry)
                .Where(vanInvoices.ContainsKey)
                .Select(entry => Describe(vanInvoices[entry], repNames))
                .ToList();

            // The memo's own card is the van's posting account, which every shop on the round shares. The shop
            // is on the invoice it reverses.
            var shop = vanInvoices[BasesOf(memo.SapDocEntry).First(vanInvoices.ContainsKey)];

            rows.Add(new VanSalesCreditNoteRow(
                Key: $"sap-{memo.SapDocEntry}",
                Origin: "SAP",
                Date: memo.DocDate.Date,
                Number: memo.SapDocNum.ToString(),
                SapDocEntry: memo.SapDocEntry,
                SapDocNum: memo.SapDocNum,
                CustomerCode: shop.CustomerCode ?? memo.CardCode,
                CustomerName: shop.CustomerName ?? memo.CardName,
                Amount: memo.DocTotal,
                VatAmount: memo.VatSum,
                Currency: string.IsNullOrWhiteSpace(memo.DocCurrency) ? "USD" : memo.DocCurrency,
                Reason: reasonByMemo.GetValueOrDefault(memo.SapDocEntry) ?? tillCredit?.Reason ?? memo.Comments,
                IsCancelled: memo.IsCancelled,
                CreditedInvoices: credited,
                FiscalReceiptNumber: probe.FiscalReceiptGlobalNo?.ToString() ?? tillCredit?.Number,
                State: VanSalesDocumentStates.Decide(fiscalised, inSap: true, hasFailure: false),
                Problem: null));
        }

        // Till credits SAP has not taken — or took, but outside the period's memo dates.
        var listedMemoEntries = vanMemos.Select(m => m.SapDocEntry).ToHashSet();

        foreach (var credit in tillCredits.Where(c => c.SapDocEntry is null || !listedMemoEntries.Contains(c.SapDocEntry.Value)))
        {
            var fiscalised = credit.Status == DesktopCreditStatuses.Fiscalised;
            var inSap = credit.SapStatus is DesktopCreditSapStatuses.Posted or DesktopCreditSapStatuses.NotRequired;

            var failure = credit.Status == DesktopCreditStatuses.ReconciliationRequired
                ? credit.Message ?? "The fiscal device could not confirm whether this credit was filed."
                : credit.SapStatus == DesktopCreditSapStatuses.Failed
                    ? credit.SapError ?? "SAP refused the credit memo."
                    : null;

            rows.Add(new VanSalesCreditNoteRow(
                Key: $"till-{credit.Id:N}",
                Origin: "Till",
                Date: VanSalesFacts.TradingDayOf(credit.CreatedAtUtc),
                Number: credit.Number,
                SapDocEntry: credit.SapDocEntry,
                SapDocNum: credit.SapDocNum,
                CustomerCode: credit.CardCode,
                CustomerName: credit.CustomerName,
                Amount: credit.Amount,
                VatAmount: null,
                Currency: string.IsNullOrWhiteSpace(credit.Currency) ? "USD" : credit.Currency,
                Reason: credit.Reason,
                IsCancelled: false,
                CreditedInvoices:
                [
                    new VanSalesCreditedInvoice(
                        credit.SaleReference,
                        credit.SaleDocNum,
                        credit.CustomerName,
                        RepName(credit.SaleCreatedBy, repNames),
                        credit.SaleAmount,
                        AmountIncludesVat: true,
                        string.IsNullOrWhiteSpace(credit.SaleCurrency) ? "USD" : credit.SaleCurrency,
                        credit.SaleDocDate.Date,
                        ChannelOf(credit.SaleSource),
                        credit.SaleWarehouse)
                ],
                FiscalReceiptNumber: fiscalised ? credit.Number : null,
                State: VanSalesDocumentStates.Decide(fiscalised, inSap, failure is not null),
                Problem: failure));
        }

        var searched = rows.Where(row => Matches(row, request.Search)).ToList();
        var ofOrigin = searched.Where(row => origin is null || row.Origin == origin).ToList();
        var summary = Summarise(searched, ofOrigin);

        searched = ofOrigin.Where(row => request.IncludeCancelled || !row.IsCancelled).ToList();

        var counts = new VanSalesCreditNoteCounts(
            searched.Count,
            searched.Count(r => r.State == VanSalesDocumentStates.Complete),
            searched.Count(r => r.State == VanSalesDocumentStates.AwaitingSap),
            searched.Count(r => r.State == VanSalesDocumentStates.NotFiscalised),
            searched.Count(r => r.State == VanSalesDocumentStates.InProgress),
            searched.Count(r => r.State == VanSalesDocumentStates.NeedsAttention));

        var filtered = searched
            .Where(row => request.State is null || row.State == request.State)
            .OrderByDescending(row => row.Date)
            .ThenByDescending(row => row.SapDocNum)
            .ToList();

        return new VanSalesCreditNotesResult(
            from,
            to,
            page,
            pageSize,
            filtered.Count,
            await creditNoteProjection.IsReadyForReadsAsync(cancellationToken),
            counts,
            filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
            summary);
    }

    /// <param name="beforeOrigin">The rows the origin counts are taken over.</param>
    /// <param name="rows">The rows everything else is taken over, cancelled notes still in.</param>
    internal static VanSalesCreditNoteSummary Summarise(
        IReadOnlyCollection<VanSalesCreditNoteRow> beforeOrigin,
        IReadOnlyCollection<VanSalesCreditNoteRow> rows)
    {
        var standing = rows.Where(row => !row.IsCancelled).ToList();

        var credited = standing
            .GroupBy(row => row.Currency)
            .Select(g => new VanSalesMoneyTotal(
                g.Key,
                g.Sum(row => row.Amount),
                g.Sum(row => row.VatAmount ?? 0m),
                g.Count(),
                NetOnlyCount: 0))
            .OrderByDescending(total => total.Count)
            .ThenBy(total => total.Currency, StringComparer.Ordinal)
            .ToList();

        var topCustomers = standing
            .GroupBy(row => (Name: row.CustomerName ?? row.CustomerCode ?? "Unknown customer", row.Currency))
            .Select(g => new VanSalesCustomerCredit(
                g.Select(row => row.CustomerCode).FirstOrDefault(code => code is not null),
                g.Key.Name,
                g.Key.Currency,
                g.Sum(row => row.Amount),
                g.Count()))
            .OrderByDescending(customer => customer.Amount)
            .ThenBy(customer => customer.CustomerName, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();

        return new VanSalesCreditNoteSummary(
            beforeOrigin.Count(row => row.Origin == "SAP"),
            beforeOrigin.Count(row => row.Origin == "Till"),
            rows.Count(row => row.IsCancelled),
            standing.SelectMany(row => row.CreditedInvoices).Select(invoice => invoice.Reference).Distinct().Count(),
            credited,
            topCustomers);
    }

    private static string ChannelOf(string? source) =>
        source == SaleSourceSystems.VanSales ? "Offline" : "Online";

    private sealed record VanInvoice(
        string Reference,
        int? SapDocNum,
        string? CustomerCode,
        string? CustomerName,
        string? CreatedBy,
        decimal Amount,
        bool AmountIncludesVat,
        string Currency,
        DateTime SoldOn,
        string Channel,
        string? WarehouseCode);

    /// <summary>The van invoices among these SAP documents, keyed by DocEntry.</summary>
    private async Task<Dictionary<int, VanInvoice>> LoadVanInvoicesAsync(
        List<int> docEntries,
        CancellationToken cancellationToken)
    {
        var invoices = new Dictionary<int, VanInvoice>();

        if (docEntries.Count == 0)
        {
            return invoices;
        }

        var online = await db.StockReservations
            .AsNoTracking()
            .Where(r => r.SourceSystem == SaleSourceSystems.VanSales
                        && r.SAPDocEntry != null
                        && docEntries.Contains(r.SAPDocEntry.Value))
            .Select(r => new
            {
                DocEntry = r.SAPDocEntry!.Value,
                r.ExternalReferenceId,
                r.SAPDocNum,
                CustomerCode = r.RouteCustomerCode ?? r.CardCode,
                CustomerName = r.RouteCustomerName ?? r.CardName,
                r.CreatedBy,
                r.TotalValue,
                r.Currency,
                r.CreatedAt,
                WarehouseCode = r.Lines.Select(l => l.WarehouseCode).FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        foreach (var invoice in online)
        {
            invoices.TryAdd(
                invoice.DocEntry,
                new VanInvoice(
                    invoice.ExternalReferenceId,
                    invoice.SAPDocNum,
                    invoice.CustomerCode,
                    invoice.CustomerName,
                    invoice.CreatedBy,
                    invoice.TotalValue,
                    AmountIncludesVat: false,
                    string.IsNullOrWhiteSpace(invoice.Currency) ? "USD" : invoice.Currency,
                    VanSalesFacts.TradingDayOf(invoice.CreatedAt),
                    "Online",
                    invoice.WarehouseCode));
        }

        // Offline sales, and the receipt rows of online ones whose reservation no longer carries the entry.
        var sales = await db.DesktopSales
            .AsNoTracking()
            .Where(s => SaleSourceSystems.VanSaleSources.Contains(s.SourceSystem!)
                        && s.SapDocEntry != null
                        && docEntries.Contains(s.SapDocEntry.Value))
            .Select(s => new
            {
                DocEntry = s.SapDocEntry!.Value,
                s.ExternalReferenceId,
                s.SapDocNum,
                CustomerCode = s.RouteCustomerCode ?? s.CardCode,
                CustomerName = s.RouteCustomerName ?? s.CardName,
                s.CreatedBy,
                s.TotalAmount,
                s.Currency,
                s.DocDate,
                s.SourceSystem,
                s.WarehouseCode
            })
            .ToListAsync(cancellationToken);

        foreach (var sale in sales)
        {
            // A receipt row is the signed, tax-inclusive figure. Where the reservation was read first it held
            // only the net total, so the receipt's total replaces it: a credit is gross, and so must be the
            // invoice it is set against.
            if (invoices.TryGetValue(sale.DocEntry, out var known))
            {
                invoices[sale.DocEntry] = known with { Amount = sale.TotalAmount, AmountIncludesVat = true };
                continue;
            }

            invoices.Add(
                sale.DocEntry,
                new VanInvoice(
                    sale.ExternalReferenceId,
                    sale.SapDocNum,
                    sale.CustomerCode,
                    sale.CustomerName,
                    sale.CreatedBy,
                    sale.TotalAmount,
                    AmountIncludesVat: true,
                    string.IsNullOrWhiteSpace(sale.Currency) ? "USD" : sale.Currency,
                    sale.DocDate.Date,
                    ChannelOf(sale.SourceSystem),
                    sale.WarehouseCode));
        }

        return invoices;
    }

    private async Task<Dictionary<Guid, string>> LoadRepNamesAsync(
        IEnumerable<string?> createdBy,
        CancellationToken cancellationToken)
    {
        var ids = createdBy
            .Select(value => VanSalesFacts.TryResolveRep(value, out var id) ? id : (Guid?)null)
            .OfType<Guid>()
            .Distinct()
            .ToList();

        if (ids.Count == 0)
        {
            return [];
        }

        var users = await db.Users
            .AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.FirstName, u.LastName, u.Username })
            .ToListAsync(cancellationToken);

        return users.ToDictionary(
            u => u.Id,
            u => string.Join(" ", new[] { u.FirstName, u.LastName }.Where(p => !string.IsNullOrWhiteSpace(p)))
                 is { Length: > 0 } full
                ? full
                : u.Username);
    }

    private static VanSalesCreditedInvoice Describe(VanInvoice invoice, Dictionary<Guid, string> repNames) =>
        new(
            invoice.Reference,
            invoice.SapDocNum,
            invoice.CustomerName,
            RepName(invoice.CreatedBy, repNames),
            invoice.Amount,
            invoice.AmountIncludesVat,
            invoice.Currency,
            invoice.SoldOn,
            invoice.Channel,
            invoice.WarehouseCode);

    private static string? RepName(string? createdBy, Dictionary<Guid, string> repNames) =>
        VanSalesFacts.TryResolveRep(createdBy, out var id) && repNames.TryGetValue(id, out var name) ? name : null;

    internal static bool Matches(VanSalesCreditNoteRow row, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        var term = search.Trim();

        return row.Number.Contains(term, StringComparison.OrdinalIgnoreCase)
               || row.CustomerName?.Contains(term, StringComparison.OrdinalIgnoreCase) == true
               || row.CustomerCode?.Contains(term, StringComparison.OrdinalIgnoreCase) == true
               || row.CreditedInvoices.Any(invoice =>
                   invoice.Reference.Contains(term, StringComparison.OrdinalIgnoreCase)
                   || invoice.SapDocNum?.ToString().Contains(term, StringComparison.Ordinal) == true
                   || invoice.RepName?.Contains(term, StringComparison.OrdinalIgnoreCase) == true);
    }
}
