using System.Diagnostics;
using System.Globalization;
using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Statements.Queries.GetCustomerStatement;

public sealed class GetCustomerStatementHandler(
    IBusinessPartnerService businessPartnerService,
    ISAPServiceLayerClient sapClient,
    IStatementBuildCache statementCache,
    ILogger<GetCustomerStatementHandler> logger
) : IRequestHandler<GetCustomerStatementQuery, ErrorOr<CustomerStatementResponseDto>>
{
    // Fixed codes, not content-addressed ones. Both statements below are constant — the card code
    // and the dates arrive as bound parameters — so two SAP query objects serve every statement
    // ever viewed. Interpolating those values instead gave every request its own SQL text and so
    // its own permanent OUQR row: the date range moves daily and each customer has a different
    // code, so nothing was ever reused. That is the leak commit 8235dcb removed elsewhere, and it
    // compounds, because a large OUQR is what makes creating the next query slow.
    internal const string OpeningBalanceQueryCode = "STMT_OPENING_BALANCE";
    internal const string LedgerQueryCode = "STMT_LEDGER_ROWS";
    internal const string OpenItemsQueryCode = "STMT_OPEN_ITEMS";

    internal const string OpeningBalanceSql = """
SELECT
    SUM(T1."Debit") AS "TotalDebit",
    SUM(T1."Credit") AS "TotalCredit"
FROM OJDT T0
INNER JOIN JDT1 T1
    ON T0."TransId" = T1."TransId"
WHERE T1."ShortName" = :cardCode
  AND T0."RefDate" < :fromDate
""";

    // One card code per execution: SAP binds a parameter as a single literal, so `IN (:codes)` with
    // a comma-separated value matches nothing and reports it as zero rows rather than an error.
    internal const string LedgerSql = """
SELECT
    T0."RefDate" AS "PostingDate",
    T0."Number" AS "TransactionNumber",
    T0."TransType" AS "TransType",
    T0."BaseRef" AS "OriginNumber",
    T0."Memo" AS "JournalMemo",
    T0."CreatedBy" AS "CreatedBy",
    T1."Line_ID" AS "LineId",
    T1."ContraAct" AS "OffsetAccount",
    T1."LineMemo" AS "Details",
    T1."Debit" AS "Debit",
    T1."Credit" AS "Credit",
    T1."FCDebit" AS "DebitFC",
    T1."FCCredit" AS "CreditFC",
    T1."FCCurrency" AS "Currency"
FROM OJDT T0
INNER JOIN JDT1 T1
    ON T0."TransId" = T1."TransId"
WHERE T1."ShortName" = :cardCode
  AND T0."RefDate" >= :fromDate
  AND T0."RefDate" <= :toDate
ORDER BY T0."RefDate", T0."Number", T1."Line_ID"
""";

    // Aging reads unreconciled journal lines, not open invoices. BalDueDeb/BalDueCred are what SAP's
    // own Account Balance window prints as "Balance Due", so an unapplied receipt, a credit note or a
    // set-off journal reduces the aging exactly as it reduces the balance. Summing invoices instead
    // could only ever climb: on ABS006's July statement it reported 41,275.73 due against a closing
    // balance of 24,875.40, because 10,135.00 of receipts sitting unapplied and two invoices already
    // closed by credit notes had nothing to subtract them.
    //
    // Two shapes here are dictated by the SQLQueries validator rather than by preference. The columns
    // come back separately and are subtracted in C# because arithmetic between two columns is
    // rejected outright; and "not fully reconciled" is written as two `>` comparisons rather than
    // `<>` because column-against-column `>` is the form known to be accepted. Both failures would
    // land when the query object is created, taking the whole statement down with them.
    internal const string OpenItemsSql = """
SELECT
    T0."RefDate" AS "PostingDate",
    T1."DueDate" AS "DueDate",
    T1."BalDueDeb" AS "BalanceDueDebit",
    T1."BalDueCred" AS "BalanceDueCredit"
FROM OJDT T0
INNER JOIN JDT1 T1
    ON T0."TransId" = T1."TransId"
WHERE T1."ShortName" = :cardCode
  AND T0."RefDate" <= :toDate
  AND (T1."BalDueDeb" > T1."BalDueCred" OR T1."BalDueCred" > T1."BalDueDeb")
""";

    public async Task<ErrorOr<CustomerStatementResponseDto>> Handle(
        GetCustomerStatementQuery request,
        CancellationToken cancellationToken)
    {
        var fromDate = (request.FromDate ?? DateTime.UtcNow.AddMonths(-3)).Date;
        var toDate = (request.ToDate ?? DateTime.UtcNow).Date;
        if (fromDate > toDate)
        {
            return Errors.Statement.RetrievalFailed("The statement start date cannot be after the end date.");
        }

        var statementCardCodes = BuildStatementCardCodes(request.CardCode, request.CardCodes);

        try
        {
            // The build runs behind the cache rather than inline, so it survives this request. See
            // StatementBuildCache for why a statement slower than the portal's HTTP timeout was
            // otherwise impossible to produce at all.
            return await statementCache.GetOrBuildAsync(
                BuildCacheKey(request.CardCode, statementCardCodes, fromDate, toDate),
                token => BuildStatementAsync(request.CardCode, statementCardCodes, fromDate, toDate, token),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The client gave up. The build is still running and will still be cached, so there is
            // nothing to report as a failure and nothing here worth logging as one.
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning(
                "Customer statement for {CardCode} exceeded its {BuildMinutes}-minute budget",
                request.CardCode,
                StatementBuildCache.BuildTimeout.TotalMinutes);
            return Errors.Statement.Timeout;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving customer statement for {CardCode}", request.CardCode);
            return Errors.Statement.RetrievalFailed(ex.Message);
        }
    }

    /// <summary>
    /// Identifies a statement by everything that changes its contents, so two customers or two date
    /// ranges can never share a cached result.
    /// </summary>
    private static string BuildCacheKey(
        string cardCode,
        IReadOnlyList<string> statementCardCodes,
        DateTime fromDate,
        DateTime toDate) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"statement:{cardCode}:{string.Join(',', statementCardCodes.OrderBy(code => code, StringComparer.OrdinalIgnoreCase))}:{fromDate:yyyy-MM-dd}:{toDate:yyyy-MM-dd}");

    private async Task<ErrorOr<CustomerStatementResponseDto>> BuildStatementAsync(
        string requestedCardCode,
        IReadOnlyList<string> statementCardCodes,
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken)
    {
        var buildStarted = Stopwatch.GetTimestamp();

        var customer = await TimeLegAsync(
            "business partner",
            requestedCardCode,
            () => businessPartnerService.GetBusinessPartnerByCodeAsync(requestedCardCode, cancellationToken));

        if (customer is null)
        {
            return Errors.Statement.CustomerNotFound(requestedCardCode);
        }

        var paymentTermsTask = customer.PayTermGrpCode.HasValue
            ? TimeLegAsync(
                "payment terms",
                requestedCardCode,
                () => sapClient.GetPaymentTermsByCodeAsync(customer.PayTermGrpCode.Value, cancellationToken))
            : Task.FromResult<PaymentTermsDto?>(null);
        var openingBalanceTask = TimeLegAsync(
            "opening balance",
            requestedCardCode,
            () => GetOpeningBalanceAsync(statementCardCodes, fromDate, cancellationToken));
        var ledgerRowsTask = TimeLegAsync(
            "ledger rows",
            requestedCardCode,
            () => GetLedgerRowsAsync(statementCardCodes, fromDate, toDate, cancellationToken));

        // Aging needs the payment terms only to label its buckets, so the open-item read it is built
        // from belongs in this batch rather than after it. It used to run once everything else had
        // finished, which put a whole paged open-invoice walk on the end of every statement, in
        // series with the part the customer actually came for.
        var openItemsTask = TimeLegAsync(
            "open items",
            requestedCardCode,
            () => GetOpenItemsAsync(statementCardCodes, toDate, cancellationToken));

        await Task.WhenAll(paymentTermsTask, openingBalanceTask, ledgerRowsTask, openItemsTask);

        var paymentTerms = paymentTermsTask.Result;
        var openingBalance = openingBalanceTask.Result;
        var ledgerRows = ledgerRowsTask.Result;

        var response = new CustomerStatementResponseDto
        {
            Customer = new StatementCustomerDto
            {
                CardCode = customer.CardCode ?? requestedCardCode,
                CardName = customer.CardName ?? string.Empty,
                Email = customer.Email,
                Phone = customer.Phone1,
                Currency = customer.Currency,
                AccountStructure = statementCardCodes.Count > 1 ? "Multi" : "Single",
                PaymentTermsName = paymentTerms?.PaymentTermsGroupName,
                PaymentTermsDays = ToPaymentTermsDays(paymentTerms)
            },
            FromDate = fromDate,
            ToDate = toDate,
            GeneratedAt = DateTime.UtcNow,
            OpeningBalance = openingBalance
        };

        decimal runningBalance = openingBalance;
        foreach (var ledgerRow in ledgerRows)
        {
            var line = MapLedgerLine(ledgerRow);
            runningBalance += line.Debit - line.Credit;
            line.Balance = runningBalance;
            response.Lines.Add(line);
        }

        response.TotalDebits = response.Lines.Sum(line => line.Debit);
        response.TotalCredits = response.Lines.Sum(line => line.Credit);
        response.TotalInvoices = response.Lines
            .Where(line => string.Equals(line.OriginCode, "IN", StringComparison.OrdinalIgnoreCase))
            .Sum(line => line.Debit);
        response.TotalPayments = response.Lines
            .Where(line => string.Equals(line.OriginCode, "RC", StringComparison.OrdinalIgnoreCase))
            .Sum(line => line.Credit);
        response.TotalCreditNotes = response.Lines
            .Where(line => string.Equals(line.OriginCode, "CN", StringComparison.OrdinalIgnoreCase))
            .Sum(line => line.Credit);
        response.ClosingBalance = runningBalance;
        response.Customer.Balance = runningBalance;
        // Aged as at the statement's own end date, not today. A July statement pulled in August was
        // otherwise bucketed against the August date, which both aged every July document a few days
        // further than the document it sat next to and let documents dated after the period end
        // count towards a period they are not in.
        response.Aging = BuildAgingSummary(openItemsTask.Result, paymentTerms, toDate);

        logger.LogInformation(
            "Customer statement for {CardCode} built in {ElapsedMs:F0}ms across {AccountCount} account(s) with {LineCount} line(s)",
            requestedCardCode,
            Stopwatch.GetElapsedTime(buildStarted).TotalMilliseconds,
            statementCardCodes.Count,
            response.Lines.Count);

        return response;
    }

    /// <summary>
    /// Times one leg of the build so a statement that runs long says which SAP read it spent the
    /// time in, rather than only that it did.
    /// </summary>
    private async Task<T> TimeLegAsync<T>(string leg, string cardCode, Func<Task<T>> work)
    {
        var started = Stopwatch.GetTimestamp();

        try
        {
            return await work();
        }
        finally
        {
            logger.LogInformation(
                "Statement leg {StatementLeg} for {CardCode} finished in {ElapsedMs:F0}ms",
                leg,
                cardCode,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    /// <remarks>
    /// One execution per card code, because a bound parameter cannot carry an <c>IN</c> list — but
    /// they do not have to wait for each other. In series a customer with five linked accounts paid
    /// five SAP latencies for what is ultimately one addition.
    /// </remarks>
    private async Task<decimal> GetOpeningBalanceAsync(
        IReadOnlyList<string> cardCodes,
        DateTime fromDate,
        CancellationToken cancellationToken)
    {
        var balances = await Task.WhenAll(
            cardCodes.Select(cardCode => GetOpeningBalanceForCardCodeAsync(cardCode, fromDate, cancellationToken)));

        return balances.Sum();
    }

    private async Task<decimal> GetOpeningBalanceForCardCodeAsync(
        string cardCode,
        DateTime fromDate,
        CancellationToken cancellationToken)
    {
        var rows = await sapClient.ExecuteParameterisedSqlQueryAsync(
            OpeningBalanceQueryCode,
            "Statement Opening Balance",
            OpeningBalanceSql,
            new Dictionary<string, string>
            {
                ["cardCode"] = cardCode,
                ["fromDate"] = FormatSqlDate(fromDate)
            },
            cancellationToken);

        return rows.Count == 0
            ? 0m
            : GetDecimal(rows[0], "TotalDebit") - GetDecimal(rows[0], "TotalCredit");
    }

    /// <remarks>
    /// Concurrent per card code for the same reason as the opening balance. Task.WhenAll keeps the
    /// results in the order the codes were given, so the concatenation below stays deterministic
    /// even though the reads no longer are.
    /// </remarks>
    private async Task<List<StatementLedgerRow>> GetLedgerRowsAsync(
        IReadOnlyList<string> cardCodes,
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken)
    {
        var rowsPerCardCode = await Task.WhenAll(cardCodes.Select(cardCode =>
            sapClient.ExecuteParameterisedSqlQueryAsync(
                LedgerQueryCode,
                "Statement Ledger Rows",
                LedgerSql,
                new Dictionary<string, string>
                {
                    ["cardCode"] = cardCode,
                    ["fromDate"] = FormatSqlDate(fromDate),
                    ["toDate"] = FormatSqlDate(toDate)
                },
                cancellationToken)));

        var rows = rowsPerCardCode.SelectMany(cardCodeRows => cardCodeRows);

        return rows.Select(row => new StatementLedgerRow(
                PostingDate: GetDateTime(row, "PostingDate"),
                TransactionNumber: GetInt32(row, "TransactionNumber"),
                TransType: GetInt32(row, "TransType"),
                OriginNumber: GetString(row, "OriginNumber"),
                OffsetAccount: GetString(row, "OffsetAccount"),
                Details: GetString(row, "Details"),
                JournalMemo: GetString(row, "JournalMemo"),
                Debit: GetDecimal(row, "Debit"),
                Credit: GetDecimal(row, "Credit"),
                Currency: GetString(row, "Currency"),
                CreatedBy: GetString(row, "CreatedBy"),
                LineId: GetInt32(row, "LineId")))
            .OrderBy(row => row.PostingDate)
            .ThenBy(row => row.TransactionNumber)
            .ThenBy(row => row.LineId)
            .ToList();
    }

    /// <remarks>
    /// Fanned out per card code like the other two reads, and for the same reason: a bound parameter
    /// cannot carry an <c>IN</c> list.
    ///
    /// A failure here costs the aging table and nothing else. Aging is a supporting block on a
    /// document whose point is the balance, and this is the leg most likely to fail for a reason
    /// unrelated to the customer — the Service Layer validates SqlText when the query object is
    /// created, so an expression it dislikes surfaces as an error for the entire request. That is
    /// how five reports once went blank over a single unsupported function, and a statement is worth
    /// showing with an empty aging table but not worth withholding over one.
    /// </remarks>
    private async Task<List<StatementOpenItemRow>> GetOpenItemsAsync(
        IReadOnlyList<string> cardCodes,
        DateTime toDate,
        CancellationToken cancellationToken)
    {
        try
        {
            var rowsPerCardCode = await Task.WhenAll(cardCodes.Select(cardCode =>
                sapClient.ExecuteParameterisedSqlQueryAsync(
                    OpenItemsQueryCode,
                    "Statement Open Items",
                    OpenItemsSql,
                    new Dictionary<string, string>
                    {
                        ["cardCode"] = cardCode,
                        ["toDate"] = FormatSqlDate(toDate)
                    },
                    cancellationToken)));

            return rowsPerCardCode
                .SelectMany(rows => rows)
                .Select(row => new StatementOpenItemRow(
                    PostingDate: GetDateTime(row, "PostingDate"),
                    DueDate: ToNullableDate(GetDateTime(row, "DueDate")),
                    // Never both non-zero on one line, so this is the signed open amount: positive
                    // for what the customer owes, negative for credit they are holding.
                    Balance: GetDecimal(row, "BalanceDueDebit") - GetDecimal(row, "BalanceDueCredit")))
                .ToList();
        }
        catch (OperationCanceledException)
        {
            // The statement's own timeout, not an aging problem. Let it travel.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Aging could not be built for {CardCodes}; the statement will show an empty aging table",
                string.Join(",", cardCodes));
            return [];
        }
    }

    /// <param name="openItems">
    /// Unreconciled journal lines for the whole account set, already read alongside the ledger.
    /// Signed, so credits subtract.
    /// </param>
    /// <param name="paymentTerms">Sizes the buckets; 30-day buckets when the customer has none.</param>
    /// <param name="asAtDate">The statement's end date — the day the aging speaks for.</param>
    /// <remarks>
    /// Every open item is aged by its own date, credits included, so the buckets net and the total
    /// lands on the closing balance. That holds while reconciliation stays inside the period; a July
    /// invoice settled in August leaves the total below the July closing balance, which is the same
    /// answer SAP's Account Balance window gives and the honest one for a back-dated statement.
    /// </remarks>
    private static StatementAgingSummaryDto BuildAgingSummary(
        IReadOnlyList<StatementOpenItemRow> openItems,
        PaymentTermsDto? paymentTerms,
        DateTime asAtDate)
    {
        var paymentTermsDays = ToPaymentTermsDays(paymentTerms);
        var bucketSize = paymentTermsDays > 0 ? paymentTermsDays : 30;

        var aging = new StatementAgingSummaryDto
        {
            Bucket1Label = $"1-{bucketSize} Days",
            Bucket2Label = $"{bucketSize + 1}-{bucketSize * 2} Days",
            Bucket3Label = $"{bucketSize * 2 + 1}-{bucketSize * 3} Days",
            Bucket4Label = $"Over {bucketSize * 3} Days"
        };

        foreach (var openItem in openItems)
        {
            var daysOverdue = paymentTermsDays > 0
                ? CalculateDaysOverdueFromTerms(openItem.PostingDate, paymentTermsDays, asAtDate)
                : CalculateDaysOverdue(openItem.DueDate, asAtDate);

            if (daysOverdue <= 0)
            {
                aging.Current += openItem.Balance;
            }
            else if (daysOverdue <= bucketSize)
            {
                aging.Days1To30 += openItem.Balance;
            }
            else if (daysOverdue <= bucketSize * 2)
            {
                aging.Days31To60 += openItem.Balance;
            }
            else if (daysOverdue <= bucketSize * 3)
            {
                aging.Days61To90 += openItem.Balance;
            }
            else
            {
                aging.Over90Days += openItem.Balance;
            }
        }

        aging.Total = aging.Current + aging.Days1To30 + aging.Days31To60 + aging.Days61To90 + aging.Over90Days;
        return aging;
    }

    /// <summary>
    /// Payment terms in days, or 0 when SAP has none to give. A customer with no terms group, or one
    /// pointing at a group the Service Layer will not return, is ordinary rather than exceptional —
    /// leads in particular — so this reports "no terms" rather than "unknown".
    /// </summary>
    private static int ToPaymentTermsDays(PaymentTermsDto? paymentTerms) =>
        paymentTerms is null
            ? 0
            : (paymentTerms.NumberOfAdditionalMonths * 30) + paymentTerms.NumberOfAdditionalDays;

    private static List<string> BuildStatementCardCodes(string primaryCardCode, IReadOnlyList<string>? requestedCardCodes)
    {
        var cardCodes = (requestedCardCodes ?? Array.Empty<string>())
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Append(primaryCardCode)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return cardCodes.Count == 0 ? new List<string> { primaryCardCode } : cardCodes;
    }

    private static StatementLineDto MapLedgerLine(StatementLedgerRow row)
    {
        var (originCode, documentType) = MapOrigin(row.TransType);
        var documentNumber = string.IsNullOrWhiteSpace(row.OriginNumber)
            ? row.TransactionNumber.ToString(CultureInfo.InvariantCulture)
            : row.OriginNumber;
        var description = !string.IsNullOrWhiteSpace(row.Details)
            ? row.Details
            : !string.IsNullOrWhiteSpace(row.JournalMemo)
                ? row.JournalMemo
                : documentType;

        return new StatementLineDto
        {
            Date = row.PostingDate,
            TransactionNumber = row.TransactionNumber,
            OriginCode = originCode,
            OriginNumber = row.OriginNumber,
            DocumentType = documentType,
            DocumentNumber = documentNumber,
            Reference = row.JournalMemo,
            OffsetAccount = row.OffsetAccount,
            Description = description,
            Debit = row.Debit,
            Credit = row.Credit,
            BalanceDue = Math.Max(row.Debit - row.Credit, 0m),
            Currency = row.Currency,
            CreatedBy = row.CreatedBy
        };
    }

    private static (string OriginCode, string DocumentType) MapOrigin(int transType)
    {
        return transType switch
        {
            -2 => ("OB", "Opening Balance"),
            13 => ("IN", "A/R Invoice"),
            14 => ("CN", "A/R Credit Memo"),
            24 => ("RC", "Incoming Payment"),
            30 => ("JE", "Journal Entry"),
            // "PS", not "PY" — this column exists to be read against SAP's own Account Balance
            // window, and that is the abbreviation it prints for an outgoing payment. ABS006's June
            // 2026 statement carries three, two of them a petty-cash payment and its reversal.
            46 => ("PS", "Outgoing Payment"),
            _ => (transType.ToString(CultureInfo.InvariantCulture), $"Transaction {transType}")
        };
    }

    /// <summary>
    /// SAP accepts a bound date as <c>yyyy-MM-dd</c>. Its own <c>TO_DATE</c> is rejected by the
    /// SQLQueries validator, so the column is compared against the bare parameter instead.
    /// </summary>
    private static string FormatSqlDate(DateTime date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string? GetString(IReadOnlyDictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static int GetInt32(IReadOnlyDictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value is null)
        {
            return 0;
        }

        return value switch
        {
            int intValue => intValue,
            long longValue => (int)longValue,
            decimal decimalValue => decimal.ToInt32(decimalValue),
            _ when int.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0
        };
    }

    private static decimal GetDecimal(IReadOnlyDictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value is null)
        {
            return 0m;
        }

        return value switch
        {
            decimal decimalValue => decimalValue,
            int intValue => intValue,
            long longValue => longValue,
            double doubleValue => Convert.ToDecimal(doubleValue, CultureInfo.InvariantCulture),
            _ when decimal.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ when decimal.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.CurrentCulture, out var fallback) => fallback,
            _ => 0m
        };
    }

    private static DateTime GetDateTime(IReadOnlyDictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value is null)
        {
            return DateTime.MinValue;
        }

        if (value is DateTime dateTime)
        {
            return dateTime;
        }

        var text = value.ToString();

        // SAP returns dates from SQLQueries as yyyyMMdd — "20200821", not "2020-08-21". General
        // parsing rejects that outright, so every statement line came back as DateTime.MinValue and
        // rendered as 01/01/0001, with the posting-date sort silently degrading to a no-op.
        if (DateTime.TryParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var compact))
        {
            return compact.Date;
        }

        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)
            ? parsed.Date
            : DateTime.MinValue;
    }

    /// <summary>
    /// Distinguishes "SAP gave no due date" from the <see cref="DateTime.MinValue"/> the row readers
    /// return for a missing or unparseable cell, so an absent date ages as unknown rather than as
    /// two thousand years overdue.
    /// </summary>
    private static DateTime? ToNullableDate(DateTime value) =>
        value == DateTime.MinValue ? null : value;

    private static int CalculateDaysOverdue(DateTime? dueDate, DateTime asAtDate)
    {
        if (!dueDate.HasValue)
        {
            return 0;
        }

        var days = (asAtDate.Date - dueDate.Value.Date).Days;
        return days > 0 ? days : 0;
    }

    private static int CalculateDaysOverdueFromTerms(DateTime docDate, int paymentTermsDays, DateTime asAtDate)
    {
        if (docDate == DateTime.MinValue)
        {
            return 0;
        }

        var effectiveDueDate = docDate.AddDays(paymentTermsDays);
        var days = (asAtDate.Date - effectiveDueDate.Date).Days;
        return days > 0 ? days : 0;
    }

    private sealed record StatementLedgerRow(
        DateTime PostingDate,
        int TransactionNumber,
        int TransType,
        string? OriginNumber,
        string? OffsetAccount,
        string? Details,
        string? JournalMemo,
        decimal Debit,
        decimal Credit,
        string? Currency,
        string? CreatedBy,
        int LineId);

    private sealed record StatementOpenItemRow(DateTime PostingDate, DateTime? DueDate, decimal Balance);
}
