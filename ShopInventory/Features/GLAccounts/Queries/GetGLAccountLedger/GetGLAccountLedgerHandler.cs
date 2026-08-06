using System.Diagnostics;
using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Sap;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.GLAccounts.Queries.GetGLAccountLedger;

/// <summary>
/// The postings against one G/L account, read the same way the customer statement reads a business
/// partner's — <c>OJDT</c> joined to <c>JDT1</c> — but keyed on <c>JDT1."Account"</c> rather than
/// <c>JDT1."ShortName"</c>.
/// </summary>
public sealed class GetGLAccountLedgerHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetGLAccountLedgerHandler> logger
) : IRequestHandler<GetGLAccountLedgerQuery, ErrorOr<GLAccountLedgerResponseDto>>
{
    /// <summary>
    /// How many lines a ledger will show before it stops and says it stopped.
    /// </summary>
    /// <remarks>
    /// A busy control account holds far more than any one customer does, and the SQL executor
    /// otherwise walks every page until SAP runs out — 500 rows a call against a cluster whose
    /// latency is heavy-tailed. The cap is on the read, not on the rendering, so a wide date range
    /// costs a bounded number of round trips rather than a bounded amount of scrolling.
    /// </remarks>
    internal const int LineLimit = 5000;

    // Fixed codes. The account code and the dates are bound parameters, so these two statements are
    // the only two SAP query objects this feature will ever create, however many accounts are
    // viewed over however many date ranges. Interpolating any of those values instead would leave a
    // permanent OUQR row per request that nothing ever reuses — the leak the statement queries were
    // rewritten to stop, and OUQR is already carrying thousands of them.
    internal const string BalanceQueryCode = "GL_LEDGER_BALANCE";
    internal const string LedgerQueryCode = "GL_LEDGER_ROWS";

    /// <summary>
    /// Everything posted to the account before a date, as debits less credits.
    /// </summary>
    /// <remarks>
    /// Runs twice per request, under one query object: once with the period start, for the opening
    /// balance, and once with tomorrow's date, which makes it the balance as at today and so
    /// directly comparable with what SAP's own chart of accounts reports. That second call is the
    /// reconciliation, and it costs nothing beyond the round trip precisely because the date is a
    /// parameter rather than part of the text.
    /// </remarks>
    internal const string BalanceSql = """
SELECT
    SUM(T1."Debit") AS "TotalDebit",
    SUM(T1."Credit") AS "TotalCredit"
FROM OJDT T0
INNER JOIN JDT1 T1
    ON T0."TransId" = T1."TransId"
WHERE T1."Account" = :acctCode
  AND T0."RefDate" < :beforeDate
""";

    // ShortName is carried here where the statement carries ContraAct: on a control account the
    // business partner is the "who" of the line, and on a nominal account it is simply empty.
    internal const string LedgerSql = """
SELECT
    T0."RefDate" AS "PostingDate",
    T0."Number" AS "TransactionNumber",
    T0."TransType" AS "TransType",
    T0."BaseRef" AS "OriginNumber",
    T0."Memo" AS "JournalMemo",
    T0."CreatedBy" AS "CreatedBy",
    T1."Line_ID" AS "LineId",
    T1."ShortName" AS "PartnerCode",
    T1."ContraAct" AS "OffsetAccount",
    T1."LineMemo" AS "Details",
    T1."Debit" AS "Debit",
    T1."Credit" AS "Credit",
    T1."FCCurrency" AS "Currency"
FROM OJDT T0
INNER JOIN JDT1 T1
    ON T0."TransId" = T1."TransId"
WHERE T1."Account" = :acctCode
  AND T0."RefDate" >= :fromDate
  AND T0."RefDate" <= :toDate
ORDER BY T0."RefDate", T0."Number", T1."Line_ID"
""";

    public async Task<ErrorOr<GLAccountLedgerResponseDto>> Handle(
        GetGLAccountLedgerQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
        {
            return Errors.GLAccount.SapDisabled;
        }

        if (string.IsNullOrWhiteSpace(request.AccountCode))
        {
            return Errors.GLAccount.NotFound(request.AccountCode ?? string.Empty);
        }

        var today = DateTime.UtcNow.Date;
        // A month, not the statement's three. This is a G/L account: the same period on a control
        // account is an order of magnitude more lines than it is on one customer.
        var fromDate = (request.FromDate ?? new DateTime(today.Year, today.Month, 1)).Date;
        var toDate = (request.ToDate ?? today).Date;

        if (fromDate > toDate)
        {
            return Errors.GLAccount.InvalidDateRange;
        }

        var buildStarted = Stopwatch.GetTimestamp();

        try
        {
            var accountTask = GetAccountAsync(request.AccountCode, cancellationToken);

            // Awaited before the rest rather than fanned out with them. The two balance reads share
            // one query code, and the executor verifies-or-creates a code without holding a lock —
            // so on the very first call after a restart, two concurrent executions of the same
            // unverified code would both try to create it and one would fail. Sequencing the first
            // read leaves the code verified for the second.
            var openingBalance = await GetBalanceBeforeAsync(request.AccountCode, fromDate, cancellationToken);

            var balanceTodayTask = GetBalanceBeforeAsync(request.AccountCode, today.AddDays(1), cancellationToken);
            var ledgerRowsTask = GetLedgerRowsAsync(request.AccountCode, fromDate, toDate, cancellationToken);

            await Task.WhenAll(accountTask, balanceTodayTask, ledgerRowsTask);

            var (account, accountReadFailed) = accountTask.Result;
            if (account is null && !accountReadFailed)
            {
                return Errors.GLAccount.NotFound(request.AccountCode);
            }

            var ledgerRows = ledgerRowsTask.Result;
            var isTruncated = ledgerRows.Count > LineLimit;

            var response = new GLAccountLedgerResponseDto
            {
                AccountCode = account?.Code ?? request.AccountCode,
                AccountName = account?.Name,
                AccountType = account?.AccountType,
                Currency = account?.Currency,
                FromDate = fromDate,
                ToDate = toDate,
                GeneratedAt = DateTime.UtcNow,
                OpeningBalance = openingBalance,
                SapBalance = account?.Balance ?? 0m,
                ComputedBalanceToday = balanceTodayTask.Result,
                IsReconciled = account is not null,
                IsTruncated = isTruncated,
                LineLimit = LineLimit
            };

            response.ReconciliationDifference = response.IsReconciled
                ? response.SapBalance - response.ComputedBalanceToday
                : 0m;

            var runningBalance = openingBalance;
            foreach (var ledgerRow in ledgerRows.Take(LineLimit))
            {
                var line = MapLedgerLine(ledgerRow);
                runningBalance += line.Debit - line.Credit;
                line.Balance = runningBalance;
                response.Lines.Add(line);
            }

            response.TotalDebits = response.Lines.Sum(line => line.Debit);
            response.TotalCredits = response.Lines.Sum(line => line.Credit);
            response.ClosingBalance = runningBalance;

            logger.LogInformation(
                "G/L ledger for {AccountCode} built in {ElapsedMs:F0}ms with {LineCount} line(s), " +
                "reconciliation difference {Difference}",
                request.AccountCode,
                Stopwatch.GetElapsedTime(buildStarted).TotalMilliseconds,
                response.Lines.Count,
                response.ReconciliationDifference);

            if (response.IsReconciled && response.ReconciliationDifference != 0m)
            {
                // Worth its own line at warning: the same debits-less-credits sum feeds the customer
                // statement's opening balance, which is known to run light on at least one account.
                // An account that disagrees here is a lead on that, and the page says so too.
                logger.LogWarning(
                    "G/L account {AccountCode} does not reconcile: SAP reports {SapBalance} but the " +
                    "journal sums to {ComputedBalance} as at {Today:yyyy-MM-dd}",
                    request.AccountCode,
                    response.SapBalance,
                    response.ComputedBalanceToday,
                    today);
            }

            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving the G/L ledger for {AccountCode}", request.AccountCode);
            return Errors.GLAccount.SapError(ex.Message);
        }
    }

    /// <summary>
    /// The account's master record, or a flag saying the read failed rather than that the account
    /// is absent.
    /// </summary>
    /// <remarks>
    /// The distinction decides two different outcomes. A null account is a 404 from SAP and means
    /// the code does not exist, which is worth failing the request over. An exception is the
    /// reconciliation leg failing, and the ledger — the part the user came for — is still correct
    /// without it; the page then says the balance could not be checked rather than claiming a
    /// difference of zero, which is the one answer that would be actively misleading.
    /// </remarks>
    private async Task<(GLAccountDto? Account, bool ReadFailed)> GetAccountAsync(
        string accountCode,
        CancellationToken cancellationToken)
    {
        try
        {
            return (await sapClient.GetGLAccountByCodeAsync(accountCode, cancellationToken), false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Could not read G/L account {AccountCode} from SAP; the ledger will show without its reconciliation",
                accountCode);
            return (null, true);
        }
    }

    private async Task<decimal> GetBalanceBeforeAsync(
        string accountCode,
        DateTime beforeDate,
        CancellationToken cancellationToken)
    {
        var rows = await sapClient.ExecuteParameterisedSqlQueryAsync(
            BalanceQueryCode,
            "G/L Ledger Balance",
            BalanceSql,
            new Dictionary<string, string>
            {
                ["acctCode"] = accountCode,
                ["beforeDate"] = SapSqlRow.FormatDate(beforeDate)
            },
            cancellationToken);

        return rows.Count == 0
            ? 0m
            : SapSqlRow.GetDecimal(rows[0], "TotalDebit") - SapSqlRow.GetDecimal(rows[0], "TotalCredit");
    }

    /// <remarks>
    /// Reads one row more than the page will show, so "there were exactly <see cref="LineLimit"/>
    /// lines" and "there were more than that" are told apart exactly rather than guessed at.
    /// </remarks>
    private async Task<List<GLLedgerRow>> GetLedgerRowsAsync(
        string accountCode,
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken)
    {
        var rows = await sapClient.ExecuteParameterisedSqlQueryAsync(
            LedgerQueryCode,
            "G/L Ledger Rows",
            LedgerSql,
            new Dictionary<string, string>
            {
                ["acctCode"] = accountCode,
                ["fromDate"] = SapSqlRow.FormatDate(fromDate),
                ["toDate"] = SapSqlRow.FormatDate(toDate)
            },
            cancellationToken,
            maxRows: LineLimit + 1);

        return rows
            .Select(row => new GLLedgerRow(
                PostingDate: SapSqlRow.GetDateTime(row, "PostingDate"),
                TransactionNumber: SapSqlRow.GetInt32(row, "TransactionNumber"),
                TransType: SapSqlRow.GetInt32(row, "TransType"),
                OriginNumber: SapSqlRow.GetString(row, "OriginNumber"),
                PartnerCode: SapSqlRow.GetString(row, "PartnerCode"),
                OffsetAccount: SapSqlRow.GetString(row, "OffsetAccount"),
                Details: SapSqlRow.GetString(row, "Details"),
                JournalMemo: SapSqlRow.GetString(row, "JournalMemo"),
                Debit: SapSqlRow.GetDecimal(row, "Debit"),
                Credit: SapSqlRow.GetDecimal(row, "Credit"),
                Currency: SapSqlRow.GetString(row, "Currency"),
                CreatedBy: SapSqlRow.GetString(row, "CreatedBy"),
                LineId: SapSqlRow.GetInt32(row, "LineId")))
            // SAP is asked for this order and the running balance depends on it, so it is restated
            // here rather than trusted — a mis-ordered ledger is wrong in a way that still adds up.
            .OrderBy(row => row.PostingDate)
            .ThenBy(row => row.TransactionNumber)
            .ThenBy(row => row.LineId)
            .ToList();
    }

    private static GLAccountLedgerLineDto MapLedgerLine(GLLedgerRow row)
    {
        var (originCode, documentType) = SapJournalOrigin.Map(row.TransType);
        var documentNumber = string.IsNullOrWhiteSpace(row.OriginNumber)
            ? row.TransactionNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : row.OriginNumber;
        var description = !string.IsNullOrWhiteSpace(row.Details)
            ? row.Details
            : !string.IsNullOrWhiteSpace(row.JournalMemo)
                ? row.JournalMemo
                : documentType;

        return new GLAccountLedgerLineDto
        {
            Date = row.PostingDate,
            TransactionNumber = row.TransactionNumber,
            OriginCode = originCode,
            DocumentType = documentType,
            OriginNumber = row.OriginNumber,
            DocumentNumber = documentNumber,
            PartnerCode = row.PartnerCode,
            OffsetAccount = row.OffsetAccount,
            Description = description,
            Reference = row.JournalMemo,
            Debit = row.Debit,
            Credit = row.Credit,
            Currency = row.Currency,
            CreatedBy = row.CreatedBy
        };
    }

    private sealed record GLLedgerRow(
        DateTime PostingDate,
        int TransactionNumber,
        int TransType,
        string? OriginNumber,
        string? PartnerCode,
        string? OffsetAccount,
        string? Details,
        string? JournalMemo,
        decimal Debit,
        decimal Credit,
        string? Currency,
        string? CreatedBy,
        int LineId);
}
