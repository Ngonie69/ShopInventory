using System.Globalization;
using System.Text.RegularExpressions;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Reports.Queries.GetItemVolumeSalesReport;

/// <summary>
/// Nets credit notes off invoices per item, converts the result to volume, and totals it by item,
/// by account, and by period.
/// </summary>
/// <remarks>
/// Both sides are selected on their own document date. A credit note raised in the window reduces
/// the window, even when the invoice it corrects was raised before it — that is what makes the
/// figure a statement about the period rather than about a set of invoices.
/// </remarks>
public sealed class GetItemVolumeSalesReportHandler(
    ApplicationDbContext context,
    ISAPServiceLayerClient sapClient,
    ILogger<GetItemVolumeSalesReportHandler> logger
) : IRequestHandler<GetItemVolumeSalesReportQuery, ErrorOr<GetItemVolumeSalesReportResult>>
{
    private static readonly TimeSpan ReportTimeout = TimeSpan.FromMinutes(5);

    private static readonly Regex AccountRangeRegex = new(
        @"^(?<prefix>[A-Za-z]+)(?<start>\d+)-(?<end>\d+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<ErrorOr<GetItemVolumeSalesReportResult>> Handle(
        GetItemVolumeSalesReportQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var fromDateUtc = NormalizeDate(request.FromDate ?? DateTime.UtcNow.AddDays(-30));
            var toDateUtc = NormalizeDate(request.ToDate ?? DateTime.UtcNow);
            var accountCodes = ExpandAccountCodes(request.AccountCodes);
            var accountCodeSet = accountCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var itemCodes = SplitCodes(request.ItemCodes);
            var itemCodeSet = itemCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);

            logger.LogInformation(
                "Generating item volume and revenue report for {AccountCount} account(s) and {ItemCount} item(s) from {FromDate} to {ToDate} grouped by {Grouping}",
                accountCodes.Count,
                itemCodes.Count,
                fromDateUtc,
                toDateUtc,
                request.Grouping);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ReportTimeout);

            var invoicesTask = sapClient.GetInvoicesByCustomersAsync(
                accountCodes,
                fromDateUtc,
                toDateUtc,
                includeDocumentLines: true,
                cancellationToken: timeoutCts.Token);
            var creditNotesTask = sapClient.GetCreditNotesByCustomersAsync(
                accountCodes,
                fromDateUtc,
                toDateUtc,
                timeoutCts.Token);

            await Task.WhenAll(invoicesTask, creditNotesTask);

            var invoices = Deduplicate(await invoicesTask, invoice => invoice.DocEntry, invoice => invoice.DocNum);
            var creditNotes = Deduplicate(await creditNotesTask, note => note.DocEntry, note => note.DocNum);

            logger.LogInformation(
                "Retrieved {InvoiceCount} invoice(s) and {CreditNoteCount} credit note(s) from SAP for the item volume report",
                invoices.Count,
                creditNotes.Count);

            var lines = BuildInvoiceLines(invoices, accountCodeSet, itemCodeSet)
                .Concat(BuildCreditNoteLines(creditNotes, accountCodeSet, itemCodeSet))
                .ToList();

            var factors = await LoadVolumeFactorsAsync(timeoutCts.Token);

            var itemNames = BuildItemNameLookup(lines);
            var accountNames = BuildAccountNameLookup(accountCodes, lines);

            var itemTotals = BuildItemResults(lines, factors, itemNames);
            var accountTotals = BuildAccountResults(accountCodes, accountNames, lines, factors, itemNames);
            var periods = BuildPeriodResults(fromDateUtc, toDateUtc, request.Grouping, accountNames, lines, factors, itemNames);
            var documentLines = BuildDocumentLineResults(request.Grouping, fromDateUtc, toDateUtc, lines, factors);

            var itemCodesWithoutFactor = itemTotals
                .Where(item => !item.HasVolumeFactor)
                .Select(item => item.ItemCode)
                .OrderBy(itemCode => itemCode, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (itemCodesWithoutFactor.Count > 0)
            {
                logger.LogInformation(
                    "{MissingFactorCount} item(s) in the item volume report have no conversion factor: {ItemCodes}",
                    itemCodesWithoutFactor.Count,
                    string.Join(", ", itemCodesWithoutFactor.Take(20)));
            }

            return new GetItemVolumeSalesReportResult
            {
                GeneratedAtUtc = DateTime.UtcNow,
                FromDateUtc = fromDateUtc,
                ToDateUtc = toDateUtc,
                Grouping = request.Grouping,
                RequestedAccountCodes = accountCodes,
                RequestedItemCodes = itemCodes,
                ItemCodesWithoutFactor = itemCodesWithoutFactor,
                Summary = BuildSummary(
                    accountCodes.Count,
                    itemCodes.Count,
                    periods.Count,
                    itemTotals,
                    accountTotals),
                ItemTotals = itemTotals,
                AccountTotals = accountTotals,
                Periods = periods,
                DocumentLines = documentLines
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Item volume and revenue report timed out");
            return Errors.Report.Timeout;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating item volume and revenue report");
            return Errors.Report.GenerationFailed(ex.Message);
        }
    }

    /// <summary>
    /// Every active conversion factor, keyed ordinal-ignore-case.
    /// </summary>
    /// <remarks>
    /// The whole table is read rather than the codes that moved. It is a few hundred rows, and
    /// filtering server-side would make the match case-sensitive in Postgres against item codes that
    /// arrive from SAP in whatever case SAP holds them.
    /// </remarks>
    private async Task<Dictionary<string, decimal>> LoadVolumeFactorsAsync(CancellationToken cancellationToken)
    {
        var conversions = await context.ItemVolumeConversions
            .AsNoTracking()
            .Where(conversion => conversion.IsActive)
            .Select(conversion => new { conversion.ItemCode, conversion.VolumeFactor })
            .ToListAsync(cancellationToken);

        return conversions
            .GroupBy(conversion => NormalizeCode(conversion.ItemCode), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().VolumeFactor,
                StringComparer.OrdinalIgnoreCase);
    }

    private static List<ItemVolumeSalesLineSnapshot> BuildInvoiceLines(
        IEnumerable<Invoice> invoices,
        IReadOnlySet<string> accountCodes,
        IReadOnlySet<string> itemCodes)
    {
        return invoices
            .Where(invoice => accountCodes.Contains(NormalizeCode(invoice.CardCode)) && !IsCancelled(invoice.Cancelled))
            .SelectMany(invoice => (invoice.DocumentLines ?? new List<InvoiceLine>())
                .Where(line => !string.IsNullOrWhiteSpace(line.ItemCode))
                .Where(line => itemCodes.Count == 0 || itemCodes.Contains(NormalizeCode(line.ItemCode)))
                .Select(line => new ItemVolumeSalesLineSnapshot(
                    $"INV:{invoice.DocEntry}",
                    IsCreditNote: false,
                    invoice.DocNum.ToString(CultureInfo.InvariantCulture),
                    invoice.DocEntry.ToString(CultureInfo.InvariantCulture),
                    ParseSapDate(invoice.DocDate),
                    NormalizeCode(invoice.CardCode),
                    NormalizeName(invoice.CardName, invoice.CardCode),
                    NormalizeCurrency(invoice.DocCurrency),
                    line.LineNum,
                    NormalizeCode(line.ItemCode),
                    NormalizeName(line.ItemDescription, line.ItemCode),
                    line.Quantity,
                    line.LineTotal,
                    IsUsdCurrency(invoice.DocCurrency) ? line.LineTotal : 0m,
                    IsZigCurrency(invoice.DocCurrency) ? line.LineTotal : 0m)))
            .Where(line => line.DocumentDateUtc != DateTime.MinValue)
            .ToList();
    }

    /// <summary>
    /// Credit-note lines, negated. SAP states a credit memo in positive quantities and amounts;
    /// flipping the sign here is what lets every downstream total be a plain sum.
    /// </summary>
    private static List<ItemVolumeSalesLineSnapshot> BuildCreditNoteLines(
        IEnumerable<SAPCreditNote> creditNotes,
        IReadOnlySet<string> accountCodes,
        IReadOnlySet<string> itemCodes)
    {
        return creditNotes
            .Where(note => accountCodes.Contains(NormalizeCode(note.CardCode)) && !IsCancelled(note.Cancelled))
            .SelectMany(note => (note.DocumentLines ?? new List<SAPCreditNoteLine>())
                .Where(line => !string.IsNullOrWhiteSpace(line.ItemCode))
                .Where(line => itemCodes.Count == 0 || itemCodes.Contains(NormalizeCode(line.ItemCode)))
                .Select(line => new ItemVolumeSalesLineSnapshot(
                    $"CRN:{note.DocEntry}",
                    IsCreditNote: true,
                    note.DocNum.ToString(CultureInfo.InvariantCulture),
                    note.DocEntry.ToString(CultureInfo.InvariantCulture),
                    ParseSapDate(note.DocDate),
                    NormalizeCode(note.CardCode),
                    NormalizeName(note.CardName, note.CardCode),
                    NormalizeCurrency(note.DocCurrency),
                    line.LineNum,
                    NormalizeCode(line.ItemCode),
                    NormalizeName(line.ItemDescription, line.ItemCode),
                    -Math.Abs(line.Quantity),
                    -Math.Abs(line.LineTotal),
                    IsUsdCurrency(note.DocCurrency) ? -Math.Abs(line.LineTotal) : 0m,
                    IsZigCurrency(note.DocCurrency) ? -Math.Abs(line.LineTotal) : 0m)))
            .Where(line => line.DocumentDateUtc != DateTime.MinValue)
            .ToList();
    }

    private static List<ItemVolumeSalesItemResult> BuildItemResults(
        IReadOnlyList<ItemVolumeSalesLineSnapshot> lines,
        IReadOnlyDictionary<string, decimal> factors,
        IReadOnlyDictionary<string, string> itemNames)
    {
        return lines
            .GroupBy(line => line.ItemCode, StringComparer.OrdinalIgnoreCase)
            .Select(group => BuildItemResult(group.Key, group.ToList(), factors, itemNames))
            .OrderByDescending(item => item.NetVolume)
            .ThenByDescending(item => item.NetQuantity)
            .ThenBy(item => item.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ItemVolumeSalesItemResult BuildItemResult(
        string itemCode,
        IReadOnlyList<ItemVolumeSalesLineSnapshot> lines,
        IReadOnlyDictionary<string, decimal> factors,
        IReadOnlyDictionary<string, string> itemNames)
    {
        var invoiceLines = lines.Where(line => !line.IsCreditNote).ToList();
        var creditLines = lines.Where(line => line.IsCreditNote).ToList();

        var invoicedQuantity = invoiceLines.Sum(line => line.Quantity);
        // Credit quantities are held negative; the report reads better with a positive "credited".
        var creditedQuantity = -creditLines.Sum(line => line.Quantity);
        var netQuantity = invoicedQuantity - creditedQuantity;

        var hasFactor = factors.TryGetValue(itemCode, out var factor);

        return new ItemVolumeSalesItemResult
        {
            ItemCode = itemCode,
            ItemName = itemNames.TryGetValue(itemCode, out var itemName) ? itemName : itemCode,
            InvoiceCount = invoiceLines.Select(line => line.DocumentKey).Distinct(StringComparer.Ordinal).Count(),
            CreditNoteCount = creditLines.Select(line => line.DocumentKey).Distinct(StringComparer.Ordinal).Count(),
            InvoicedQuantity = invoicedQuantity,
            CreditedQuantity = creditedQuantity,
            NetQuantity = netQuantity,
            VolumeFactor = hasFactor ? factor : null,
            InvoicedVolume = hasFactor ? RoundVolume(invoicedQuantity * factor) : 0m,
            CreditedVolume = hasFactor ? RoundVolume(creditedQuantity * factor) : 0m,
            NetVolume = hasFactor ? RoundVolume(netQuantity * factor) : 0m,
            InvoicedSalesUsd = invoiceLines.Sum(line => line.AmountUsd),
            InvoicedSalesZig = invoiceLines.Sum(line => line.AmountZig),
            CreditedSalesUsd = -creditLines.Sum(line => line.AmountUsd),
            CreditedSalesZig = -creditLines.Sum(line => line.AmountZig),
            NetRevenueUsd = lines.Sum(line => line.AmountUsd),
            NetRevenueZig = lines.Sum(line => line.AmountZig)
        };
    }

    private static List<ItemVolumeSalesAccountResult> BuildAccountResults(
        IReadOnlyList<string> accountCodes,
        IReadOnlyDictionary<string, string> accountNames,
        IReadOnlyList<ItemVolumeSalesLineSnapshot> lines,
        IReadOnlyDictionary<string, decimal> factors,
        IReadOnlyDictionary<string, string> itemNames)
    {
        return accountCodes
            .Select(accountCode => BuildAccountResult(
                accountCode,
                accountNames.TryGetValue(accountCode, out var accountName) ? accountName : accountCode,
                lines.Where(line => string.Equals(line.CardCode, accountCode, StringComparison.OrdinalIgnoreCase)).ToList(),
                factors,
                itemNames))
            .ToList();
    }

    private static ItemVolumeSalesAccountResult BuildAccountResult(
        string accountCode,
        string accountName,
        IReadOnlyList<ItemVolumeSalesLineSnapshot> lines,
        IReadOnlyDictionary<string, decimal> factors,
        IReadOnlyDictionary<string, string> itemNames)
    {
        var items = BuildItemResults(lines, factors, itemNames);

        return new ItemVolumeSalesAccountResult
        {
            CardCode = accountCode,
            CardName = accountName,
            InvoiceCount = lines
                .Where(line => !line.IsCreditNote)
                .Select(line => line.DocumentKey)
                .Distinct(StringComparer.Ordinal)
                .Count(),
            CreditNoteCount = lines
                .Where(line => line.IsCreditNote)
                .Select(line => line.DocumentKey)
                .Distinct(StringComparer.Ordinal)
                .Count(),
            InvoicedQuantity = items.Sum(item => item.InvoicedQuantity),
            CreditedQuantity = items.Sum(item => item.CreditedQuantity),
            NetQuantity = items.Sum(item => item.NetQuantity),
            InvoicedVolume = items.Sum(item => item.InvoicedVolume),
            CreditedVolume = items.Sum(item => item.CreditedVolume),
            NetVolume = items.Sum(item => item.NetVolume),
            ItemsWithoutFactorCount = items.Count(item => !item.HasVolumeFactor),
            InvoicedSalesUsd = items.Sum(item => item.InvoicedSalesUsd),
            InvoicedSalesZig = items.Sum(item => item.InvoicedSalesZig),
            CreditedSalesUsd = items.Sum(item => item.CreditedSalesUsd),
            CreditedSalesZig = items.Sum(item => item.CreditedSalesZig),
            NetRevenueUsd = items.Sum(item => item.NetRevenueUsd),
            NetRevenueZig = items.Sum(item => item.NetRevenueZig),
            Items = items
        };
    }

    private static List<ItemVolumeSalesPeriodResult> BuildPeriodResults(
        DateTime fromDateUtc,
        DateTime toDateUtc,
        ItemVolumeSalesGrouping grouping,
        IReadOnlyDictionary<string, string> accountNames,
        IReadOnlyList<ItemVolumeSalesLineSnapshot> lines,
        IReadOnlyDictionary<string, decimal> factors,
        IReadOnlyDictionary<string, string> itemNames)
    {
        return BuildPeriods(fromDateUtc, toDateUtc, grouping)
            .Select(period =>
            {
                var periodLines = lines
                    .Where(line => line.DocumentDateUtc >= period.Start && line.DocumentDateUtc <= period.End)
                    .ToList();

                var accounts = periodLines
                    .Select(line => line.CardCode)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(cardCode => cardCode, StringComparer.OrdinalIgnoreCase)
                    .Select(cardCode => BuildAccountResult(
                        cardCode,
                        accountNames.TryGetValue(cardCode, out var accountName) ? accountName : cardCode,
                        periodLines.Where(line => string.Equals(line.CardCode, cardCode, StringComparison.OrdinalIgnoreCase)).ToList(),
                        factors,
                        itemNames))
                    .ToList();

                return new ItemVolumeSalesPeriodResult
                {
                    Label = period.Label,
                    PeriodStartUtc = period.Start,
                    PeriodEndUtc = period.End,
                    InvoiceCount = accounts.Sum(account => account.InvoiceCount),
                    CreditNoteCount = accounts.Sum(account => account.CreditNoteCount),
                    NetQuantity = accounts.Sum(account => account.NetQuantity),
                    NetVolume = accounts.Sum(account => account.NetVolume),
                    NetRevenueUsd = accounts.Sum(account => account.NetRevenueUsd),
                    NetRevenueZig = accounts.Sum(account => account.NetRevenueZig),
                    Accounts = accounts
                };
            })
            .ToList();
    }

    private static List<ItemVolumeSalesDocumentLineResult> BuildDocumentLineResults(
        ItemVolumeSalesGrouping grouping,
        DateTime fromDateUtc,
        DateTime toDateUtc,
        IReadOnlyList<ItemVolumeSalesLineSnapshot> lines,
        IReadOnlyDictionary<string, decimal> factors)
    {
        return lines
            .Select(line =>
            {
                var hasFactor = factors.TryGetValue(line.ItemCode, out var factor);

                return new ItemVolumeSalesDocumentLineResult
                {
                    PeriodLabel = BuildDetailPeriodLabel(line.DocumentDateUtc, fromDateUtc, toDateUtc, grouping),
                    DocumentType = line.IsCreditNote ? "Credit Note" : "Invoice",
                    DocumentDateUtc = line.DocumentDateUtc,
                    DocumentNumber = line.DocumentNumber,
                    DocumentEntry = line.DocumentEntry,
                    CardCode = line.CardCode,
                    CardName = line.CardName,
                    Currency = line.Currency,
                    LineNumber = line.LineNumber,
                    ItemCode = line.ItemCode,
                    ItemName = line.ItemName,
                    Quantity = line.Quantity,
                    VolumeFactor = hasFactor ? factor : null,
                    Volume = hasFactor ? RoundVolume(line.Quantity * factor) : 0m,
                    LineAmount = line.LineAmount,
                    AmountUsd = line.AmountUsd,
                    AmountZig = line.AmountZig
                };
            })
            .OrderBy(line => line.DocumentDateUtc)
            .ThenBy(line => line.CardCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(line => line.DocumentType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(line => line.DocumentNumber, StringComparer.OrdinalIgnoreCase)
            .ThenBy(line => line.LineNumber)
            .ToList();
    }

    private static ItemVolumeSalesSummaryResult BuildSummary(
        int requestedAccountCount,
        int requestedItemCount,
        int periodCount,
        IReadOnlyList<ItemVolumeSalesItemResult> itemTotals,
        IReadOnlyList<ItemVolumeSalesAccountResult> accountTotals)
    {
        return new ItemVolumeSalesSummaryResult
        {
            RequestedAccountCount = requestedAccountCount,
            ActiveAccountCount = accountTotals.Count(account =>
                account.InvoiceCount > 0 || account.CreditNoteCount > 0),
            RequestedItemCount = requestedItemCount,
            ItemCount = itemTotals.Count,
            TotalPeriods = periodCount,
            InvoiceCount = accountTotals.Sum(account => account.InvoiceCount),
            CreditNoteCount = accountTotals.Sum(account => account.CreditNoteCount),
            InvoicedQuantity = itemTotals.Sum(item => item.InvoicedQuantity),
            CreditedQuantity = itemTotals.Sum(item => item.CreditedQuantity),
            NetQuantity = itemTotals.Sum(item => item.NetQuantity),
            InvoicedVolume = itemTotals.Sum(item => item.InvoicedVolume),
            CreditedVolume = itemTotals.Sum(item => item.CreditedVolume),
            NetVolume = itemTotals.Sum(item => item.NetVolume),
            ItemsWithoutFactorCount = itemTotals.Count(item => !item.HasVolumeFactor),
            QuantityWithoutFactor = itemTotals.Where(item => !item.HasVolumeFactor).Sum(item => item.NetQuantity),
            InvoicedSalesUsd = itemTotals.Sum(item => item.InvoicedSalesUsd),
            InvoicedSalesZig = itemTotals.Sum(item => item.InvoicedSalesZig),
            CreditedSalesUsd = itemTotals.Sum(item => item.CreditedSalesUsd),
            CreditedSalesZig = itemTotals.Sum(item => item.CreditedSalesZig),
            NetRevenueUsd = itemTotals.Sum(item => item.NetRevenueUsd),
            NetRevenueZig = itemTotals.Sum(item => item.NetRevenueZig)
        };
    }

    private static Dictionary<string, string> BuildItemNameLookup(IEnumerable<ItemVolumeSalesLineSnapshot> lines)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            if (!string.IsNullOrWhiteSpace(line.ItemName) && line.ItemName != line.ItemCode)
            {
                names[line.ItemCode] = line.ItemName;
            }
        }

        return names;
    }

    private static Dictionary<string, string> BuildAccountNameLookup(
        IReadOnlyList<string> accountCodes,
        IEnumerable<ItemVolumeSalesLineSnapshot> lines)
    {
        var names = accountCodes.ToDictionary(code => code, code => code, StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            if (!string.IsNullOrWhiteSpace(line.CardName) && line.CardName != line.CardCode)
            {
                names[line.CardCode] = line.CardName;
            }
        }

        return names;
    }

    private static List<TDocument> Deduplicate<TDocument>(
        IEnumerable<TDocument> documents,
        Func<TDocument, int> docEntrySelector,
        Func<TDocument, int> docNumSelector)
    {
        return documents
            .GroupBy(document => docEntrySelector(document) > 0
                ? $"DE:{docEntrySelector(document)}"
                : $"DN:{docNumSelector(document)}")
            .Select(group => group.First())
            .ToList();
    }

    private static List<(DateTime Start, DateTime End, string Label)> BuildPeriods(
        DateTime fromDateUtc,
        DateTime toDateUtc,
        ItemVolumeSalesGrouping grouping)
    {
        // Total is the window itself, so it is not walked: its one period starts where the
        // caller asked rather than at the head of a calendar month or quarter.
        if (grouping == ItemVolumeSalesGrouping.Total)
        {
            return [(fromDateUtc.Date, toDateUtc.Date, BuildWindowLabel(fromDateUtc, toDateUtc))];
        }

        var periods = new List<(DateTime Start, DateTime End, string Label)>();
        var cursor = GetPeriodStart(fromDateUtc, grouping);

        while (cursor <= toDateUtc.Date)
        {
            var periodEnd = GetPeriodEnd(cursor, grouping);
            periods.Add((cursor, periodEnd, BuildPeriodLabel(cursor, periodEnd, grouping)));

            cursor = grouping switch
            {
                ItemVolumeSalesGrouping.Weekly => cursor.AddDays(7),
                ItemVolumeSalesGrouping.Monthly => cursor.AddMonths(1),
                ItemVolumeSalesGrouping.Quarterly => cursor.AddMonths(3),
                _ => cursor.AddDays(1)
            };
        }

        return periods;
    }

    private static DateTime GetPeriodStart(DateTime dateUtc, ItemVolumeSalesGrouping grouping) => grouping switch
    {
        ItemVolumeSalesGrouping.Weekly => GetStartOfWeek(dateUtc.Date),
        ItemVolumeSalesGrouping.Monthly => new DateTime(dateUtc.Year, dateUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc),
        ItemVolumeSalesGrouping.Quarterly => new DateTime(
            dateUtc.Year,
            (((dateUtc.Month - 1) / 3) * 3) + 1,
            1,
            0,
            0,
            0,
            DateTimeKind.Utc),
        _ => dateUtc.Date
    };

    private static DateTime GetPeriodEnd(DateTime periodStart, ItemVolumeSalesGrouping grouping) => grouping switch
    {
        ItemVolumeSalesGrouping.Weekly => periodStart.AddDays(6),
        ItemVolumeSalesGrouping.Monthly => periodStart.AddMonths(1).AddDays(-1),
        ItemVolumeSalesGrouping.Quarterly => periodStart.AddMonths(3).AddDays(-1),
        _ => periodStart
    };

    private static string BuildPeriodLabel(DateTime start, DateTime end, ItemVolumeSalesGrouping grouping) => grouping switch
    {
        ItemVolumeSalesGrouping.Weekly => $"{start:dd MMM yyyy} - {end:dd MMM yyyy}",
        ItemVolumeSalesGrouping.Monthly => start.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
        ItemVolumeSalesGrouping.Quarterly => string.Create(
            CultureInfo.InvariantCulture,
            $"Q{((start.Month - 1) / 3) + 1} {start.Year}"),
        _ => start.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)
    };

    private static string BuildWindowLabel(DateTime fromDateUtc, DateTime toDateUtc) => string.Create(
        CultureInfo.InvariantCulture,
        $"{fromDateUtc:dd MMM yyyy} - {toDateUtc:dd MMM yyyy}");

    private static string BuildDetailPeriodLabel(
        DateTime dateUtc,
        DateTime fromDateUtc,
        DateTime toDateUtc,
        ItemVolumeSalesGrouping grouping)
    {
        // Every line of a Total run belongs to the one period, so the label is the window
        // rather than the line's own date.
        if (grouping == ItemVolumeSalesGrouping.Total)
        {
            return BuildWindowLabel(fromDateUtc, toDateUtc);
        }

        var periodStart = GetPeriodStart(NormalizeDate(dateUtc), grouping);
        return BuildPeriodLabel(periodStart, GetPeriodEnd(periodStart, grouping), grouping);
    }

    private static DateTime GetStartOfWeek(DateTime dateUtc)
    {
        var offset = ((int)dateUtc.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return dateUtc.AddDays(-offset);
    }

    private static decimal RoundVolume(decimal value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);

    private static DateTime NormalizeDate(DateTime value) =>
        (value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime()).Date;

    private static DateTime ParseSapDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DateTime.MinValue;
        }

        return DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed.Date
            : DateTime.MinValue;
    }

    private static bool IsUsdCurrency(string? currency) =>
        currency is "USD" or "$" || string.IsNullOrWhiteSpace(currency);

    private static bool IsZigCurrency(string? currency) =>
        string.Equals(currency, "ZIG", StringComparison.OrdinalIgnoreCase);

    private static bool IsCancelled(string? value) =>
        string.Equals(value, "tYES", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "Y", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "Yes", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeCode(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();

    private static string NormalizeName(string? value, string? fallback) =>
        string.IsNullOrWhiteSpace(value) ? NormalizeCode(fallback) : value.Trim();

    private static string NormalizeCurrency(string? currency) =>
        IsUsdCurrency(currency)
            ? "USD"
            : IsZigCurrency(currency)
                ? "ZiG"
                : currency!.Trim();

    private static List<string> SplitCodes(IEnumerable<string> rawCodes) =>
        rawCodes
            .Where(rawValue => !string.IsNullOrWhiteSpace(rawValue))
            .SelectMany(rawValue => rawValue.Split(
                [',', ';'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(NormalizeCode)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Splits the requested codes and expands any <c>PREFIX001-019</c> range into its members, so a
    /// van fleet can be asked for the way people write it down.
    /// </summary>
    private static List<string> ExpandAccountCodes(IEnumerable<string> rawAccountCodes)
    {
        var expandedCodes = new List<string>();

        foreach (var token in SplitCodes(rawAccountCodes))
        {
            var match = AccountRangeRegex.Match(token);
            if (!match.Success)
            {
                expandedCodes.Add(token);
                continue;
            }

            var prefix = match.Groups["prefix"].Value.ToUpperInvariant();
            var startText = match.Groups["start"].Value;
            var endText = match.Groups["end"].Value;

            if (!int.TryParse(startText, NumberStyles.None, CultureInfo.InvariantCulture, out var startNumber) ||
                !int.TryParse(endText, NumberStyles.None, CultureInfo.InvariantCulture, out var endNumber) ||
                endNumber < startNumber)
            {
                expandedCodes.Add(token);
                continue;
            }

            var width = Math.Max(startText.Length, endText.Length);
            for (var number = startNumber; number <= endNumber; number++)
            {
                expandedCodes.Add($"{prefix}{number.ToString(new string('0', width), CultureInfo.InvariantCulture)}");
            }
        }

        return expandedCodes
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
