using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.Reports.Queries.GetAccountSalesPaymentReport;

public sealed class GetAccountSalesPaymentReportHandler(
    ApplicationDbContext context,
    ISAPServiceLayerClient sapClient,
    ILogger<GetAccountSalesPaymentReportHandler> logger
) : IRequestHandler<GetAccountSalesPaymentReportQuery, ErrorOr<GetAccountSalesPaymentReportResult>>
{
    private static readonly TimeSpan ReportTimeout = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions QueuePayloadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly Regex AccountRangeRegex = new(
        @"^(?<prefix>[A-Za-z]+)(?<start>\d+)-(?<end>\d+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<ErrorOr<GetAccountSalesPaymentReportResult>> Handle(
        GetAccountSalesPaymentReportQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var fromDateUtc = NormalizeDate(request.FromDate ?? DateTime.UtcNow.AddDays(-30));
            var toDateUtc = NormalizeDate(request.ToDate ?? DateTime.UtcNow);
            var accountCodes = ExpandAccountCodes(request.AccountCodes);
            var accountCodeSet = accountCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);

            logger.LogInformation(
                "Generating account sales and incoming payment report for {AccountCount} account(s) from {FromDate} to {ToDate} grouped by {Grouping}",
                accountCodes.Count,
                fromDateUtc,
                toDateUtc,
                request.Grouping);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ReportTimeout);

            var invoicesTask = GetSapInvoicesForAccountsAsync(accountCodes, fromDateUtc, toDateUtc, timeoutCts.Token);
            var paymentsTask = GetSapPaymentsForAccountsAsync(accountCodes, fromDateUtc, toDateUtc, timeoutCts.Token);

            await Task.WhenAll(invoicesTask, paymentsTask);

            var sapInvoices = await invoicesTask;
            var sapPayments = await paymentsTask;
            var sapInvoiceDocEntries = sapInvoices
                .Select(invoice => invoice.DocEntry)
                .ToHashSet();
            var sapInvoiceDocNums = sapInvoices
                .Select(invoice => invoice.DocNum)
                .ToHashSet();
            var sapPaymentDocEntries = sapPayments
                .Select(payment => payment.DocEntry)
                .ToHashSet();
            var sapPaymentDocNums = sapPayments
                .Select(payment => payment.DocNum)
                .ToHashSet();

            var invoiceSnapshots = BuildInvoiceSnapshots(sapInvoices, accountCodeSet);
            var paymentSnapshots = BuildPaymentSnapshots(sapPayments, accountCodeSet);
            var localInvoiceSnapshots = await BuildLocalInvoiceSnapshotsAsync(
                fromDateUtc,
                toDateUtc,
                accountCodeSet,
                sapInvoiceDocEntries,
                sapInvoiceDocNums,
                timeoutCts.Token);
            var localPaymentSnapshots = await BuildLocalPaymentSnapshotsAsync(
                fromDateUtc,
                toDateUtc,
                accountCodeSet,
                sapPaymentDocEntries,
                sapPaymentDocNums,
                timeoutCts.Token);

            invoiceSnapshots.AddRange(localInvoiceSnapshots);
            paymentSnapshots.AddRange(localPaymentSnapshots);

            if (localInvoiceSnapshots.Count > 0 || localPaymentSnapshots.Count > 0)
            {
                logger.LogInformation(
                    "Merged {LocalInvoiceCount} local invoice(s) and {LocalPaymentCount} local payment(s) into account sales/payment report",
                    localInvoiceSnapshots.Count,
                    localPaymentSnapshots.Count);
            }

            var sources = new List<string> { "SAP" };
            if (localInvoiceSnapshots.Count > 0 || localPaymentSnapshots.Count > 0)
            {
                sources.Add("API");
            }

            var accountNames = BuildAccountNameLookup(accountCodes, invoiceSnapshots, paymentSnapshots);
            var accountTotals = BuildAccountTotals(accountCodes, accountNames, invoiceSnapshots, paymentSnapshots);
            var periods = BuildPeriodResults(fromDateUtc, toDateUtc, request.Grouping, accountNames, invoiceSnapshots, paymentSnapshots);
            var invoiceDetails = BuildInvoiceDetailResults(request.Grouping, invoiceSnapshots);
            var paymentDetails = BuildPaymentDetailResults(request.Grouping, paymentSnapshots);
            var paymentApplications = BuildPaymentApplicationResults(request.Grouping, paymentSnapshots);

            return new GetAccountSalesPaymentReportResult
            {
                GeneratedAtUtc = DateTime.UtcNow,
                FromDateUtc = fromDateUtc,
                ToDateUtc = toDateUtc,
                Grouping = request.Grouping,
                RequestedAccountCodes = accountCodes,
                Sources = sources,
                Summary = new AccountSalesPaymentSummaryResult
                {
                    RequestedAccountCount = accountCodes.Count,
                    ActiveAccountCount = accountTotals.Count(account =>
                        account.InvoiceCount > 0 || account.PaymentCount > 0 || account.TotalQuantitySold > 0),
                    TotalPeriods = periods.Count,
                    TotalInvoices = accountTotals.Sum(account => account.InvoiceCount),
                    TotalPayments = accountTotals.Sum(account => account.PaymentCount),
                    TotalQuantitySold = accountTotals.Sum(account => account.TotalQuantitySold),
                    TotalSalesUsd = accountTotals.Sum(account => account.TotalSalesUsd),
                    TotalSalesZig = accountTotals.Sum(account => account.TotalSalesZig),
                    TotalIncomingPaymentsUsd = accountTotals.Sum(account => account.IncomingPaymentsUsd),
                    TotalIncomingPaymentsZig = accountTotals.Sum(account => account.IncomingPaymentsZig),
                    CollectionRatePercentUsd = CalculatePercentage(
                        accountTotals.Sum(account => account.IncomingPaymentsUsd),
                        accountTotals.Sum(account => account.TotalSalesUsd)),
                    CollectionRatePercentZig = CalculatePercentage(
                        accountTotals.Sum(account => account.IncomingPaymentsZig),
                        accountTotals.Sum(account => account.TotalSalesZig))
                },
                AccountTotals = accountTotals,
                Periods = periods,
                InvoiceDetails = invoiceDetails,
                PaymentDetails = paymentDetails,
                PaymentApplications = paymentApplications
            };
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Account sales and incoming payment report timed out");
            return Errors.Report.Timeout;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating account sales and incoming payment report");
            return Errors.Report.GenerationFailed(ex.Message);
        }
    }

    private async Task<List<Invoice>> GetSapInvoicesForAccountsAsync(
        IReadOnlyCollection<string> accountCodes,
        DateTime fromDateUtc,
        DateTime toDateUtc,
        CancellationToken cancellationToken)
    {
        var invoices = new List<Invoice>();

        foreach (var accountCode in accountCodes)
        {
            var accountInvoices = await sapClient.GetInvoicesByCustomerAsync(
                accountCode,
                fromDateUtc,
                toDateUtc,
                includeDocumentLines: true,
                cancellationToken: cancellationToken);
            invoices.AddRange(accountInvoices);
        }

        var dedupedInvoices = invoices
            .GroupBy(invoice => GetSapDocumentKey(invoice.DocEntry, invoice.DocNum))
            .Select(group => group.First())
            .ToList();

        logger.LogInformation(
            "Retrieved {InvoiceCount} invoice(s) from SAP for {AccountCount} requested account(s) between {FromDate} and {ToDate}",
            dedupedInvoices.Count,
            accountCodes.Count,
            fromDateUtc,
            toDateUtc);

        return dedupedInvoices;
    }

    private async Task<List<IncomingPayment>> GetSapPaymentsForAccountsAsync(
        IReadOnlyCollection<string> accountCodes,
        DateTime fromDateUtc,
        DateTime toDateUtc,
        CancellationToken cancellationToken)
    {
        var payments = new List<IncomingPayment>();

        foreach (var accountCode in accountCodes)
        {
            var accountPayments = await sapClient.GetIncomingPaymentsByCustomerAsync(accountCode, fromDateUtc, toDateUtc, cancellationToken);
            payments.AddRange(accountPayments);
        }

        var dedupedPayments = payments
            .GroupBy(payment => GetSapDocumentKey(payment.DocEntry, payment.DocNum))
            .Select(group => group.First())
            .ToList();

        logger.LogInformation(
            "Retrieved {PaymentCount} incoming payment(s) from SAP for {AccountCount} requested account(s) between {FromDate} and {ToDate}",
            dedupedPayments.Count,
            accountCodes.Count,
            fromDateUtc,
            toDateUtc);

        return dedupedPayments;
    }

    private static string GetSapDocumentKey(int docEntry, int docNum) =>
        docEntry > 0
            ? $"DE:{docEntry}"
            : $"DN:{docNum}";

    private async Task<List<AccountSalesPaymentInvoiceSnapshot>> BuildLocalInvoiceSnapshotsAsync(
        DateTime fromDateUtc,
        DateTime toDateUtc,
        IReadOnlySet<string> accountCodes,
        IReadOnlySet<int> sapInvoiceDocEntries,
        IReadOnlySet<int> sapInvoiceDocNums,
        CancellationToken cancellationToken)
    {
        var toDateExclusive = toDateUtc.AddDays(1);

        var localInvoices = await context.Invoices
            .AsNoTracking()
            .Include(invoice => invoice.DocumentLines)
            .Where(invoice => invoice.DocDate >= fromDateUtc && invoice.DocDate < toDateExclusive)
            .Where(invoice => !invoice.SyncedToSAP)
            .ToListAsync(cancellationToken);

        return localInvoices
            .Where(invoice => accountCodes.Contains(NormalizeCode(invoice.CardCode)))
            .Where(invoice => !IsLocalCancelled(invoice.Status))
            .Where(invoice => !HasMatchingSapInvoice(invoice, sapInvoiceDocEntries, sapInvoiceDocNums))
            .Select(invoice => new AccountSalesPaymentInvoiceSnapshot(
                -invoice.Id,
                "API",
                invoice.SAPDocNum?.ToString(CultureInfo.InvariantCulture) ?? $"LOCAL-{invoice.Id}",
                invoice.SAPDocEntry?.ToString(CultureInfo.InvariantCulture) ?? invoice.Id.ToString(CultureInfo.InvariantCulture),
                NormalizeCode(invoice.CardCode),
                NormalizeName(invoice.CardName, invoice.CardCode),
                NormalizeDate(invoice.DocDate),
                NormalizeCurrency(invoice.DocCurrency),
                NormalizeStatus(invoice.Status, "Draft"),
                invoice.DocTotal,
                IsUsdCurrency(invoice.DocCurrency) ? invoice.DocTotal : 0m,
                IsZigCurrency(invoice.DocCurrency) ? invoice.DocTotal : 0m,
                invoice.DocumentLines
                    .Where(line => !string.IsNullOrWhiteSpace(line.ItemCode))
                    .Select(line => new AccountSalesPaymentItemSnapshot(
                        line.LineNum,
                        NormalizeCode(line.ItemCode),
                        NormalizeName(line.ItemDescription, line.ItemCode),
                        line.Quantity,
                        line.LineTotal,
                        IsUsdCurrency(invoice.DocCurrency) ? line.LineTotal : 0m,
                        IsZigCurrency(invoice.DocCurrency) ? line.LineTotal : 0m))
                    .ToList()))
            .Where(invoice => invoice.DocumentDateUtc != DateTime.MinValue)
            .ToList();
    }

    private async Task<List<AccountSalesPaymentPaymentSnapshot>> BuildLocalPaymentSnapshotsAsync(
        DateTime fromDateUtc,
        DateTime toDateUtc,
        IReadOnlySet<string> accountCodes,
        IReadOnlySet<int> sapPaymentDocEntries,
        IReadOnlySet<int> sapPaymentDocNums,
        CancellationToken cancellationToken)
    {
        var toDateExclusive = toDateUtc.AddDays(1);
        var paymentSnapshots = new List<AccountSalesPaymentPaymentSnapshot>();

        var localPayments = await context.IncomingPayments
            .AsNoTracking()
            .Include(payment => payment.PaymentInvoices)
            .ThenInclude(paymentInvoice => paymentInvoice.Invoice)
            .Where(payment => payment.DocDate >= fromDateUtc && payment.DocDate < toDateExclusive)
            .Where(payment => !payment.SyncedToSAP)
            .ToListAsync(cancellationToken);

        paymentSnapshots.AddRange(localPayments
            .Where(payment => accountCodes.Contains(NormalizeCode(payment.CardCode)))
            .Where(payment => !IsLocalCancelled(payment.Status))
            .Where(payment => !HasMatchingSapPayment(payment, sapPaymentDocEntries, sapPaymentDocNums))
            .Select(payment => new AccountSalesPaymentPaymentSnapshot(
                "API",
                payment.SAPDocNum?.ToString(CultureInfo.InvariantCulture) ?? $"LOCALPAY-{payment.Id}",
                payment.SAPDocEntry?.ToString(CultureInfo.InvariantCulture) ?? payment.Id.ToString(CultureInfo.InvariantCulture),
                NormalizeStatus(payment.Status, "Draft"),
                NormalizeCurrency(payment.DocCurrency),
                FirstNonEmpty(payment.TransferReference, payment.Remarks, payment.JournalRemarks),
                NormalizeCode(payment.CardCode),
                NormalizeName(payment.CardName, payment.CardCode),
                NormalizeDate(payment.DocDate),
                GetPaymentTotal(payment),
                IsUsdCurrency(payment.DocCurrency) ? GetPaymentTotal(payment) : 0m,
                IsZigCurrency(payment.DocCurrency) ? GetPaymentTotal(payment) : 0m,
                BuildPaymentApplicationSnapshots(payment.PaymentInvoices)))
            .Where(payment => payment.PaymentDateUtc != DateTime.MinValue));

        var queuedPayments = await context.IncomingPaymentQueue
            .AsNoTracking()
            .Where(payment => payment.SourceSystem == "API")
            .Where(payment => payment.Status == IncomingPaymentQueueStatus.Pending ||
                              payment.Status == IncomingPaymentQueueStatus.Processing ||
                              payment.Status == IncomingPaymentQueueStatus.Completed)
            .ToListAsync(cancellationToken);

        paymentSnapshots.AddRange(queuedPayments
            .Where(payment => accountCodes.Contains(NormalizeCode(payment.CustomerCode)))
            .Where(payment => !HasMatchingSapPayment(payment, sapPaymentDocEntries, sapPaymentDocNums))
            .Select(BuildQueuedPaymentSnapshot)
            .Where(payment => payment.PaymentDateUtc >= fromDateUtc && payment.PaymentDateUtc <= toDateUtc));

        return paymentSnapshots;
    }

    private static List<AccountSalesPaymentInvoiceSnapshot> BuildInvoiceSnapshots(
        IEnumerable<Invoice> invoices,
        IReadOnlySet<string> accountCodes)
    {
        return invoices
            .Where(invoice => accountCodes.Contains(NormalizeCode(invoice.CardCode)) && !IsCancelled(invoice.Cancelled))
            .Select(invoice => new AccountSalesPaymentInvoiceSnapshot(
                invoice.DocEntry,
                "SAP",
                invoice.DocNum.ToString(CultureInfo.InvariantCulture),
                invoice.DocEntry.ToString(CultureInfo.InvariantCulture),
                NormalizeCode(invoice.CardCode),
                NormalizeName(invoice.CardName, invoice.CardCode),
                ParseSapDate(invoice.DocDate),
                NormalizeCurrency(invoice.DocCurrency),
                BuildDocumentStatus(FirstNonEmpty(invoice.DocStatus, invoice.DocumentStatus), IsCancelled(invoice.Cancelled), "Posted"),
                invoice.DocTotal,
                IsUsdCurrency(invoice.DocCurrency) ? invoice.DocTotal : 0m,
                IsZigCurrency(invoice.DocCurrency) ? invoice.DocTotal : 0m,
                (invoice.DocumentLines ?? new List<InvoiceLine>())
                    .Where(line => !string.IsNullOrWhiteSpace(line.ItemCode))
                    .Select(line => new AccountSalesPaymentItemSnapshot(
                        line.LineNum,
                        NormalizeCode(line.ItemCode),
                        NormalizeName(line.ItemDescription, line.ItemCode),
                        line.Quantity,
                        line.LineTotal,
                        IsUsdCurrency(invoice.DocCurrency) ? line.LineTotal : 0m,
                        IsZigCurrency(invoice.DocCurrency) ? line.LineTotal : 0m))
                    .ToList()))
            .Where(invoice => invoice.DocumentDateUtc != DateTime.MinValue)
            .ToList();
    }

    private static List<AccountSalesPaymentPaymentSnapshot> BuildPaymentSnapshots(
        IEnumerable<IncomingPayment> payments,
        IReadOnlySet<string> accountCodes)
    {
        return payments
            .Where(payment => accountCodes.Contains(NormalizeCode(payment.CardCode)) && !IsCancelled(payment.Cancelled))
            .Select(payment =>
            {
                var paymentTotal = GetPaymentTotal(payment);
                return new AccountSalesPaymentPaymentSnapshot(
                    "SAP",
                    payment.DocNum.ToString(CultureInfo.InvariantCulture),
                    payment.DocEntry.ToString(CultureInfo.InvariantCulture),
                    BuildDocumentStatus(null, IsCancelled(payment.Cancelled), "Posted"),
                    NormalizeCurrency(payment.DocCurrency),
                    FirstNonEmpty(payment.TransferReference, payment.Remarks, payment.JournalRemarks),
                    NormalizeCode(payment.CardCode),
                    NormalizeName(payment.CardName, payment.CardCode),
                    ParseSapDate(payment.DocDate),
                    paymentTotal,
                    IsUsdCurrency(payment.DocCurrency) ? paymentTotal : 0m,
                    IsZigCurrency(payment.DocCurrency) ? paymentTotal : 0m,
                    BuildPaymentApplicationSnapshots(payment.PaymentInvoices));
            })
            .Where(payment => payment.PaymentDateUtc != DateTime.MinValue)
            .ToList();
    }

    private static Dictionary<string, string> BuildAccountNameLookup(
        IReadOnlyList<string> accountCodes,
        IEnumerable<AccountSalesPaymentInvoiceSnapshot> invoices,
        IEnumerable<AccountSalesPaymentPaymentSnapshot> payments)
    {
        var names = accountCodes.ToDictionary(code => code, code => code, StringComparer.OrdinalIgnoreCase);

        foreach (var invoice in invoices)
        {
            if (!string.IsNullOrWhiteSpace(invoice.CardName) && invoice.CardName != invoice.CardCode)
            {
                names[invoice.CardCode] = invoice.CardName;
            }
        }

        foreach (var payment in payments)
        {
            if (!string.IsNullOrWhiteSpace(payment.CardName) && payment.CardName != payment.CardCode)
            {
                names[payment.CardCode] = payment.CardName;
            }
        }

        return names;
    }

    private static List<AccountSalesPaymentAccountResult> BuildAccountTotals(
        IReadOnlyList<string> accountCodes,
        IReadOnlyDictionary<string, string> accountNames,
        IReadOnlyList<AccountSalesPaymentInvoiceSnapshot> invoices,
        IReadOnlyList<AccountSalesPaymentPaymentSnapshot> payments)
    {
        return accountCodes
            .Select(code => BuildAccountResult(
                code,
                accountNames.TryGetValue(code, out var accountName) ? accountName : code,
                invoices.Where(invoice => string.Equals(invoice.CardCode, code, StringComparison.OrdinalIgnoreCase)).ToList(),
                payments.Where(payment => string.Equals(payment.CardCode, code, StringComparison.OrdinalIgnoreCase)).ToList()))
            .ToList();
    }

    private static List<AccountSalesPaymentPeriodResult> BuildPeriodResults(
        DateTime fromDateUtc,
        DateTime toDateUtc,
        AccountSalesPaymentGrouping grouping,
        IReadOnlyDictionary<string, string> accountNames,
        IReadOnlyList<AccountSalesPaymentInvoiceSnapshot> invoices,
        IReadOnlyList<AccountSalesPaymentPaymentSnapshot> payments)
    {
        return BuildPeriods(fromDateUtc, toDateUtc, grouping)
            .Select(period =>
            {
                var periodInvoices = invoices
                    .Where(invoice => invoice.DocumentDateUtc >= period.Start && invoice.DocumentDateUtc <= period.End)
                    .ToList();

                var periodPayments = payments
                    .Where(payment => payment.PaymentDateUtc >= period.Start && payment.PaymentDateUtc <= period.End)
                    .ToList();

                var accountCodes = periodInvoices
                    .Select(invoice => invoice.CardCode)
                    .Concat(periodPayments.Select(payment => payment.CardCode))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var accounts = accountCodes
                    .Select(code => BuildAccountResult(
                        code,
                        accountNames.TryGetValue(code, out var accountName) ? accountName : code,
                        periodInvoices.Where(invoice => string.Equals(invoice.CardCode, code, StringComparison.OrdinalIgnoreCase)).ToList(),
                        periodPayments.Where(payment => string.Equals(payment.CardCode, code, StringComparison.OrdinalIgnoreCase)).ToList()))
                    .ToList();

                return new AccountSalesPaymentPeriodResult
                {
                    Label = period.Label,
                    PeriodStartUtc = period.Start,
                    PeriodEndUtc = period.End,
                    InvoiceCount = accounts.Sum(account => account.InvoiceCount),
                    PaymentCount = accounts.Sum(account => account.PaymentCount),
                    TotalQuantitySold = accounts.Sum(account => account.TotalQuantitySold),
                    TotalSalesUsd = accounts.Sum(account => account.TotalSalesUsd),
                    TotalSalesZig = accounts.Sum(account => account.TotalSalesZig),
                    IncomingPaymentsUsd = accounts.Sum(account => account.IncomingPaymentsUsd),
                    IncomingPaymentsZig = accounts.Sum(account => account.IncomingPaymentsZig),
                    Accounts = accounts
                };
            })
            .ToList();
    }

    private static AccountSalesPaymentAccountResult BuildAccountResult(
        string accountCode,
        string accountName,
        IReadOnlyList<AccountSalesPaymentInvoiceSnapshot> invoices,
        IReadOnlyList<AccountSalesPaymentPaymentSnapshot> payments)
    {
        var items = invoices
            .SelectMany(invoice => invoice.Items.Select(item => new
            {
                invoice.DocumentKey,
                item.ItemCode,
                item.ItemName,
                item.QuantitySold,
                item.SalesUsd,
                item.SalesZig
            }))
            .GroupBy(item => item.ItemCode, StringComparer.OrdinalIgnoreCase)
            .Select(group => new AccountSalesPaymentItemResult
            {
                ItemCode = group.Key,
                ItemName = group.Select(item => item.ItemName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? group.Key,
                InvoiceCount = group.Select(item => item.DocumentKey).Distinct().Count(),
                TotalQuantitySold = group.Sum(item => item.QuantitySold),
                TotalSalesUsd = group.Sum(item => item.SalesUsd),
                TotalSalesZig = group.Sum(item => item.SalesZig)
            })
            .OrderByDescending(item => item.TotalSalesUsd)
            .ThenByDescending(item => item.TotalSalesZig)
            .ThenBy(item => item.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var totalSalesUsd = invoices.Sum(invoice => invoice.TotalSalesUsd);
        var totalSalesZig = invoices.Sum(invoice => invoice.TotalSalesZig);
        var totalPaymentsUsd = payments.Sum(payment => payment.IncomingPaymentsUsd);
        var totalPaymentsZig = payments.Sum(payment => payment.IncomingPaymentsZig);

        return new AccountSalesPaymentAccountResult
        {
            CardCode = accountCode,
            CardName = accountName,
            InvoiceCount = invoices.Count,
            PaymentCount = payments.Count,
            TotalQuantitySold = items.Sum(item => item.TotalQuantitySold),
            TotalSalesUsd = totalSalesUsd,
            TotalSalesZig = totalSalesZig,
            IncomingPaymentsUsd = totalPaymentsUsd,
            IncomingPaymentsZig = totalPaymentsZig,
            CollectionRatePercentUsd = CalculatePercentage(totalPaymentsUsd, totalSalesUsd),
            CollectionRatePercentZig = CalculatePercentage(totalPaymentsZig, totalSalesZig),
            Items = items
        };
    }

    private static List<AccountSalesPaymentInvoiceDetailResult> BuildInvoiceDetailResults(
        AccountSalesPaymentGrouping grouping,
        IReadOnlyList<AccountSalesPaymentInvoiceSnapshot> invoices)
    {
        return invoices
            .SelectMany(invoice => invoice.Items.Select(item => new AccountSalesPaymentInvoiceDetailResult
            {
                PeriodLabel = BuildDetailPeriodLabel(invoice.DocumentDateUtc, grouping),
                Source = invoice.Source,
                DocumentDateUtc = invoice.DocumentDateUtc,
                CardCode = invoice.CardCode,
                CardName = invoice.CardName,
                DocumentNumber = invoice.DocumentNumber,
                DocumentEntry = invoice.DocumentEntry,
                Status = invoice.Status,
                Currency = invoice.Currency,
                DocumentTotal = invoice.DocumentTotal,
                LineNumber = item.LineNumber,
                ItemCode = item.ItemCode,
                ItemName = item.ItemName,
                QuantitySold = item.QuantitySold,
                LineAmount = item.LineAmount,
                SalesUsd = item.SalesUsd,
                SalesZig = item.SalesZig
            }))
            .OrderBy(detail => detail.DocumentDateUtc)
            .ThenBy(detail => detail.CardCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(detail => detail.DocumentNumber, StringComparer.OrdinalIgnoreCase)
            .ThenBy(detail => detail.LineNumber)
            .ToList();
    }

    private static List<AccountSalesPaymentPaymentDetailResult> BuildPaymentDetailResults(
        AccountSalesPaymentGrouping grouping,
        IReadOnlyList<AccountSalesPaymentPaymentSnapshot> payments)
    {
        return payments
            .Select(payment => new AccountSalesPaymentPaymentDetailResult
            {
                PeriodLabel = BuildDetailPeriodLabel(payment.PaymentDateUtc, grouping),
                Source = payment.Source,
                PaymentDateUtc = payment.PaymentDateUtc,
                CardCode = payment.CardCode,
                CardName = payment.CardName,
                PaymentNumber = payment.PaymentNumber,
                PaymentEntry = payment.PaymentEntry,
                Status = payment.Status,
                Currency = payment.Currency,
                TotalAmount = payment.TotalAmount,
                IncomingPaymentsUsd = payment.IncomingPaymentsUsd,
                IncomingPaymentsZig = payment.IncomingPaymentsZig,
                Reference = payment.Reference,
                AppliedInvoiceCount = payment.Applications.Count
            })
            .OrderBy(detail => detail.PaymentDateUtc)
            .ThenBy(detail => detail.CardCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(detail => detail.PaymentNumber, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<AccountSalesPaymentPaymentApplicationResult> BuildPaymentApplicationResults(
        AccountSalesPaymentGrouping grouping,
        IReadOnlyList<AccountSalesPaymentPaymentSnapshot> payments)
    {
        return payments
            .SelectMany(payment => payment.Applications.Select(application => new AccountSalesPaymentPaymentApplicationResult
            {
                PeriodLabel = BuildDetailPeriodLabel(payment.PaymentDateUtc, grouping),
                Source = payment.Source,
                PaymentDateUtc = payment.PaymentDateUtc,
                CardCode = payment.CardCode,
                CardName = payment.CardName,
                PaymentNumber = payment.PaymentNumber,
                PaymentEntry = payment.PaymentEntry,
                Status = payment.Status,
                AppliedInvoiceReference = application.InvoiceReference,
                InvoiceType = application.InvoiceType,
                Currency = payment.Currency,
                AppliedAmount = application.AppliedAmount
            }))
            .OrderBy(detail => detail.PaymentDateUtc)
            .ThenBy(detail => detail.CardCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(detail => detail.PaymentNumber, StringComparer.OrdinalIgnoreCase)
            .ThenBy(detail => detail.AppliedInvoiceReference, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<(DateTime Start, DateTime End, string Label)> BuildPeriods(
        DateTime fromDateUtc,
        DateTime toDateUtc,
        AccountSalesPaymentGrouping grouping)
    {
        var periods = new List<(DateTime Start, DateTime End, string Label)>();
        var cursor = grouping switch
        {
            AccountSalesPaymentGrouping.Daily => fromDateUtc.Date,
            AccountSalesPaymentGrouping.Weekly => GetStartOfWeek(fromDateUtc.Date),
            AccountSalesPaymentGrouping.Monthly => new DateTime(fromDateUtc.Year, fromDateUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc),
            _ => fromDateUtc.Date
        };

        while (cursor <= toDateUtc.Date)
        {
            var periodEnd = grouping switch
            {
                AccountSalesPaymentGrouping.Daily => cursor,
                AccountSalesPaymentGrouping.Weekly => cursor.AddDays(6),
                AccountSalesPaymentGrouping.Monthly => new DateTime(cursor.Year, cursor.Month, 1, 0, 0, 0, DateTimeKind.Utc)
                    .AddMonths(1)
                    .AddDays(-1),
                _ => cursor
            };

            periods.Add((cursor, periodEnd, BuildPeriodLabel(cursor, periodEnd, grouping)));

            cursor = grouping switch
            {
                AccountSalesPaymentGrouping.Daily => cursor.AddDays(1),
                AccountSalesPaymentGrouping.Weekly => cursor.AddDays(7),
                AccountSalesPaymentGrouping.Monthly => new DateTime(cursor.Year, cursor.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1),
                _ => cursor.AddDays(1)
            };
        }

        return periods;
    }

    private static string BuildPeriodLabel(DateTime start, DateTime end, AccountSalesPaymentGrouping grouping) => grouping switch
    {
        AccountSalesPaymentGrouping.Daily => start.ToString("dd MMM yyyy", CultureInfo.InvariantCulture),
        AccountSalesPaymentGrouping.Weekly => $"{start:dd MMM yyyy} - {end:dd MMM yyyy}",
        AccountSalesPaymentGrouping.Monthly => start.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
        _ => start.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)
    };

    private static string BuildDetailPeriodLabel(DateTime dateUtc, AccountSalesPaymentGrouping grouping)
    {
        var normalizedDate = NormalizeDate(dateUtc);
        var periodStart = grouping switch
        {
            AccountSalesPaymentGrouping.Daily => normalizedDate,
            AccountSalesPaymentGrouping.Weekly => GetStartOfWeek(normalizedDate),
            AccountSalesPaymentGrouping.Monthly => new DateTime(normalizedDate.Year, normalizedDate.Month, 1, 0, 0, 0, DateTimeKind.Utc),
            _ => normalizedDate
        };

        var periodEnd = grouping switch
        {
            AccountSalesPaymentGrouping.Daily => periodStart,
            AccountSalesPaymentGrouping.Weekly => periodStart.AddDays(6),
            AccountSalesPaymentGrouping.Monthly => periodStart.AddMonths(1).AddDays(-1),
            _ => periodStart
        };

        return BuildPeriodLabel(periodStart, periodEnd, grouping);
    }

    private static DateTime GetStartOfWeek(DateTime dateUtc)
    {
        var offset = ((int)dateUtc.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return dateUtc.AddDays(-offset);
    }

    private static DateTime NormalizeDate(DateTime value) =>
        (value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime()).Date;

    private static DateTime ParseSapDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DateTime.MinValue;
        }

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return parsed.Date;
        }

        return DateTime.MinValue;
    }

    private static bool IsUsdCurrency(string? currency) =>
        currency is "USD" or "$" || string.IsNullOrWhiteSpace(currency);

    private static bool IsZigCurrency(string? currency) =>
        string.Equals(currency, "ZIG", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(currency, "ZiG", StringComparison.OrdinalIgnoreCase);

    private static bool IsCancelled(string? value) =>
        string.Equals(value, "tYES", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "Y", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "Yes", StringComparison.OrdinalIgnoreCase);

    private static bool IsLocalCancelled(string? value) =>
        IsCancelled(value) ||
        string.Equals(value, "Cancelled", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "Canceled", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "Void", StringComparison.OrdinalIgnoreCase);

    private static decimal GetPaymentTotal(IncomingPayment payment)
    {
        var methodTotal = payment.CashSum + payment.CheckSum + payment.TransferSum + payment.CreditSum;
        if (methodTotal != 0m)
        {
            return methodTotal;
        }

        return payment.DocTotal != 0m ? payment.DocTotal : payment.DocTotalFc;
    }

    private static decimal GetPaymentTotal(IncomingPaymentEntity payment)
    {
        var methodTotal = payment.CashSum + payment.CheckSum + payment.TransferSum + payment.CreditSum;
        if (methodTotal != 0m)
        {
            return methodTotal;
        }

        return payment.DocTotal != 0m ? payment.DocTotal : payment.DocTotalFc;
    }

    private static bool HasMatchingSapInvoice(
        InvoiceEntity invoice,
        IReadOnlySet<int> sapInvoiceDocEntries,
        IReadOnlySet<int> sapInvoiceDocNums) =>
        invoice.SAPDocEntry.HasValue && sapInvoiceDocEntries.Contains(invoice.SAPDocEntry.Value) ||
        invoice.SAPDocNum.HasValue && sapInvoiceDocNums.Contains(invoice.SAPDocNum.Value);

    private static bool HasMatchingSapPayment(
        IncomingPaymentEntity payment,
        IReadOnlySet<int> sapPaymentDocEntries,
        IReadOnlySet<int> sapPaymentDocNums) =>
        payment.SAPDocEntry.HasValue && sapPaymentDocEntries.Contains(payment.SAPDocEntry.Value) ||
        payment.SAPDocNum.HasValue && sapPaymentDocNums.Contains(payment.SAPDocNum.Value);

    private static bool HasMatchingSapPayment(
        IncomingPaymentQueueEntity payment,
        IReadOnlySet<int> sapPaymentDocEntries,
        IReadOnlySet<int> sapPaymentDocNums)
    {
        var matchesSapDocEntry = int.TryParse(payment.SapDocEntry, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sapDocEntry)
            && sapPaymentDocEntries.Contains(sapDocEntry);

        return matchesSapDocEntry || payment.SapDocNum.HasValue && sapPaymentDocNums.Contains(payment.SapDocNum.Value);
    }

    private static AccountSalesPaymentPaymentSnapshot BuildQueuedPaymentSnapshot(IncomingPaymentQueueEntity payment)
    {
        var request = DeserializeQueuedPaymentRequest(payment.PaymentPayload);
        var paymentDateUtc = ParseSapDate(request?.DocDate);
        if (paymentDateUtc == DateTime.MinValue)
        {
            paymentDateUtc = NormalizeDate(payment.CreatedAt);
        }

        var cardCode = NormalizeCode(request?.CardCode ?? payment.CustomerCode);

        return new AccountSalesPaymentPaymentSnapshot(
            "API",
            payment.SapDocNum?.ToString(CultureInfo.InvariantCulture) ?? payment.ExternalReference,
            !string.IsNullOrWhiteSpace(payment.SapDocEntry) ? payment.SapDocEntry : payment.Id.ToString(CultureInfo.InvariantCulture),
            payment.Status.ToString(),
            NormalizeCurrency(payment.Currency),
            FirstNonEmpty(payment.ExternalReference, request?.TransferReference, request?.Remarks, payment.Remarks),
            cardCode,
            NormalizeName(null, cardCode),
            paymentDateUtc,
            payment.TotalAmount,
            IsUsdCurrency(payment.Currency) ? payment.TotalAmount : 0m,
            IsZigCurrency(payment.Currency) ? payment.TotalAmount : 0m,
            BuildPaymentApplicationSnapshots(request));
    }

    private static IReadOnlyList<AccountSalesPaymentPaymentApplicationSnapshot> BuildPaymentApplicationSnapshots(
        IEnumerable<PaymentInvoice>? paymentInvoices)
    {
        if (paymentInvoices is null)
        {
            return [];
        }

        return paymentInvoices
            .Select(paymentInvoice => new AccountSalesPaymentPaymentApplicationSnapshot(
                paymentInvoice.LineNum,
                paymentInvoice.DocEntry.ToString(CultureInfo.InvariantCulture),
                paymentInvoice.InvoiceType ?? string.Empty,
                paymentInvoice.SumApplied != 0 ? paymentInvoice.SumApplied : paymentInvoice.SumAppliedFC))
            .ToList();
    }

    private static IReadOnlyList<AccountSalesPaymentPaymentApplicationSnapshot> BuildPaymentApplicationSnapshots(
        IEnumerable<IncomingPaymentInvoiceEntity> paymentInvoices)
    {
        return paymentInvoices
            .Select(paymentInvoice => new AccountSalesPaymentPaymentApplicationSnapshot(
                paymentInvoice.LineNum,
                paymentInvoice.SAPDocEntry?.ToString(CultureInfo.InvariantCulture)
                    ?? paymentInvoice.Invoice?.SAPDocNum?.ToString(CultureInfo.InvariantCulture)
                    ?? paymentInvoice.InvoiceId?.ToString(CultureInfo.InvariantCulture)
                    ?? string.Empty,
                paymentInvoice.InvoiceType ?? string.Empty,
                paymentInvoice.SumApplied != 0 ? paymentInvoice.SumApplied : paymentInvoice.SumAppliedFC))
            .ToList();
    }

    private static IReadOnlyList<AccountSalesPaymentPaymentApplicationSnapshot> BuildPaymentApplicationSnapshots(
        CreateIncomingPaymentRequest? request)
    {
        if (request?.PaymentInvoices is null || request.PaymentInvoices.Count == 0)
        {
            return [];
        }

        return request.PaymentInvoices
            .Select((paymentInvoice, index) => new AccountSalesPaymentPaymentApplicationSnapshot(
                index + 1,
                paymentInvoice.DocEntry.ToString(CultureInfo.InvariantCulture),
                paymentInvoice.InvoiceType ?? string.Empty,
                paymentInvoice.SumApplied))
            .ToList();
    }

    private static string BuildDocumentStatus(string? status, bool isCancelled, string fallback) =>
        isCancelled ? "Cancelled" : NormalizeStatus(status, fallback);

    private static CreateIncomingPaymentRequest? DeserializeQueuedPaymentRequest(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<CreateIncomingPaymentRequest>(payload, QueuePayloadJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static decimal CalculatePercentage(decimal numerator, decimal denominator) =>
        denominator <= 0 ? 0 : Math.Round((numerator / denominator) * 100m, 2);

    private static string NormalizeCode(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToUpperInvariant();

    private static string NormalizeName(string? value, string? fallback) =>
        string.IsNullOrWhiteSpace(value)
            ? NormalizeCode(fallback)
            : value.Trim();

    private static string NormalizeCurrency(string? currency) =>
        IsUsdCurrency(currency)
            ? "USD"
            : IsZigCurrency(currency)
                ? "ZiG"
                : string.IsNullOrWhiteSpace(currency)
                    ? "USD"
                    : currency.Trim();

    private static string NormalizeStatus(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static List<string> ExpandAccountCodes(IEnumerable<string> rawAccountCodes)
    {
        var expandedCodes = new List<string>();

        foreach (var rawValue in rawAccountCodes)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                continue;
            }

            var tokens = rawValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var rawToken in tokens)
            {
                var token = NormalizeCode(rawToken);
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
        }

        return expandedCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
