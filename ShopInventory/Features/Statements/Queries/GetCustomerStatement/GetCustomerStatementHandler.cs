using System.Globalization;
using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Statements.Queries.GetCustomerStatement;

public sealed class GetCustomerStatementHandler(
    IBusinessPartnerService businessPartnerService,
    IInvoiceService invoiceService,
    ISAPServiceLayerClient sapClient,
    ILogger<GetCustomerStatementHandler> logger
) : IRequestHandler<GetCustomerStatementQuery, ErrorOr<CustomerStatementResponseDto>>
{
    public async Task<ErrorOr<CustomerStatementResponseDto>> Handle(
        GetCustomerStatementQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var fromDate = (request.FromDate ?? DateTime.UtcNow.AddMonths(-3)).Date;
            var toDate = (request.ToDate ?? DateTime.UtcNow).Date;
            if (fromDate > toDate)
            {
                return Errors.Statement.RetrievalFailed("The statement start date cannot be after the end date.");
            }

            var customer = await businessPartnerService.GetBusinessPartnerByCodeAsync(request.CardCode, cancellationToken);
            if (customer is null)
            {
                return Errors.Statement.CustomerNotFound(request.CardCode);
            }

            var statementCardCodes = BuildStatementCardCodes(request.CardCode, request.CardCodes);
            var paymentTermsTask = customer.PayTermGrpCode.HasValue
                ? sapClient.GetPaymentTermsByCodeAsync(customer.PayTermGrpCode.Value, cancellationToken)
                : Task.FromResult<PaymentTermsDto?>(null);
            var openingBalanceTask = GetOpeningBalanceAsync(statementCardCodes, fromDate, cancellationToken);
            var ledgerRowsTask = GetLedgerRowsAsync(statementCardCodes, fromDate, toDate, cancellationToken);

            await Task.WhenAll(paymentTermsTask, openingBalanceTask, ledgerRowsTask);

            var paymentTerms = paymentTermsTask.Result;
            var openingBalance = openingBalanceTask.Result;
            var ledgerRows = ledgerRowsTask.Result;

            var response = new CustomerStatementResponseDto
            {
                Customer = new StatementCustomerDto
                {
                    CardCode = customer.CardCode ?? request.CardCode,
                    CardName = customer.CardName ?? string.Empty,
                    Email = customer.Email,
                    Phone = customer.Phone1,
                    Currency = customer.Currency,
                    AccountStructure = statementCardCodes.Count > 1 ? "Multi" : "Single",
                    PaymentTermsName = paymentTerms?.PaymentTermsGroupName,
                    PaymentTermsDays = paymentTerms is null
                        ? null
                        : (paymentTerms.NumberOfAdditionalMonths * 30) + paymentTerms.NumberOfAdditionalDays
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
            response.Aging = await BuildAgingSummaryAsync(statementCardCodes, paymentTerms, cancellationToken);

            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving customer statement for {CardCode}", request.CardCode);
            return Errors.Statement.RetrievalFailed(ex.Message);
        }
    }

    private async Task<decimal> GetOpeningBalanceAsync(
        IReadOnlyList<string> cardCodes,
        DateTime fromDate,
        CancellationToken cancellationToken)
    {
        var inClause = BuildInClause(cardCodes);
        var sqlText = $@"
SELECT
    SUM(T1.""Debit"") AS ""TotalDebit"",
    SUM(T1.""Credit"") AS ""TotalCredit""
FROM OJDT T0
INNER JOIN JDT1 T1
    ON T0.""TransId"" = T1.""TransId""
WHERE T1.""ShortName"" IN ({inClause})
  AND T0.""RefDate"" < '{fromDate:yyyy-MM-dd}'";

        var rows = await sapClient.ExecuteRawSqlQueryAsync(
            CreateQueryCode("StmtOpen"),
            "Statement Opening Balance",
            sqlText,
            cancellationToken);

        if (rows.Count == 0)
        {
            return 0m;
        }

        var row = rows[0];
        return GetDecimal(row, "TotalDebit") - GetDecimal(row, "TotalCredit");
    }

    private async Task<List<StatementLedgerRow>> GetLedgerRowsAsync(
        IReadOnlyList<string> cardCodes,
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken)
    {
        var inClause = BuildInClause(cardCodes);
        var sqlText = $@"
SELECT
    T0.""RefDate"" AS ""PostingDate"",
        T0.""Number"" AS ""TransactionNumber"",
    T0.""TransType"" AS ""TransType"",
    T0.""BaseRef"" AS ""OriginNumber"",
    T0.""Memo"" AS ""JournalMemo"",
    T0.""CreatedBy"" AS ""CreatedBy"",
    T1.""Line_ID"" AS ""LineId"",
    T1.""ContraAct"" AS ""OffsetAccount"",
    T1.""LineMemo"" AS ""Details"",
    T1.""Debit"" AS ""Debit"",
    T1.""Credit"" AS ""Credit"",
    T1.""FCDebit"" AS ""DebitFC"",
    T1.""FCCredit"" AS ""CreditFC"",
    T1.""FCCurrency"" AS ""Currency""
FROM OJDT T0
INNER JOIN JDT1 T1
    ON T0.""TransId"" = T1.""TransId""
WHERE T1.""ShortName"" IN ({inClause})
  AND T0.""RefDate"" >= '{fromDate:yyyy-MM-dd}'
  AND T0.""RefDate"" <= '{toDate:yyyy-MM-dd}'
    ORDER BY T0.""RefDate"", T0.""Number"", T1.""Line_ID"";";

        var rows = await sapClient.ExecuteRawSqlQueryAsync(
            CreateQueryCode("StmtRows"),
            "Statement Ledger Rows",
            sqlText,
            cancellationToken);

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

    private async Task<StatementAgingSummaryDto> BuildAgingSummaryAsync(
        IReadOnlyList<string> cardCodes,
        PaymentTermsDto? paymentTerms,
        CancellationToken cancellationToken)
    {
        var paymentTermsDays = paymentTerms is null
            ? 0
            : (paymentTerms.NumberOfAdditionalMonths * 30) + paymentTerms.NumberOfAdditionalDays;
        var bucketSize = paymentTermsDays > 0 ? paymentTermsDays : 30;

        var invoiceTasks = cardCodes.Select(invoiceService.GetInvoicesByCustomerAsync).ToArray();
        await Task.WhenAll(invoiceTasks);

        var openInvoices = invoiceTasks
            .SelectMany(task => task.Result)
            .Where(invoice => !string.Equals(invoice.DocStatus, "X", StringComparison.OrdinalIgnoreCase))
            .Select(invoice => new OpenInvoiceRow(
                DocDate: ParseDate(invoice.DocDate),
                DueDate: ParseNullableDate(invoice.DocDueDate),
                Balance: invoice.DocTotal - invoice.PaidToDate))
            .Where(invoice => invoice.Balance > 0)
            .ToList();

        var aging = new StatementAgingSummaryDto
        {
            Bucket1Label = $"1-{bucketSize} Days",
            Bucket2Label = $"{bucketSize + 1}-{bucketSize * 2} Days",
            Bucket3Label = $"{bucketSize * 2 + 1}-{bucketSize * 3} Days",
            Bucket4Label = $"Over {bucketSize * 3} Days"
        };

        foreach (var invoice in openInvoices)
        {
            var daysOverdue = paymentTermsDays > 0
                ? CalculateDaysOverdueFromTerms(invoice.DocDate, paymentTermsDays)
                : CalculateDaysOverdue(invoice.DueDate);

            if (daysOverdue <= 0)
            {
                aging.Current += invoice.Balance;
            }
            else if (daysOverdue <= bucketSize)
            {
                aging.Days1To30 += invoice.Balance;
            }
            else if (daysOverdue <= bucketSize * 2)
            {
                aging.Days31To60 += invoice.Balance;
            }
            else if (daysOverdue <= bucketSize * 3)
            {
                aging.Days61To90 += invoice.Balance;
            }
            else
            {
                aging.Over90Days += invoice.Balance;
            }
        }

        aging.Total = aging.Current + aging.Days1To30 + aging.Days31To60 + aging.Days61To90 + aging.Over90Days;
        return aging;
    }

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
            46 => ("PY", "Outgoing Payment"),
            _ => (transType.ToString(CultureInfo.InvariantCulture), $"Transaction {transType}")
        };
    }

    private static string BuildInClause(IEnumerable<string> cardCodes) =>
        string.Join(", ", cardCodes.Select(cardCode => $"'{cardCode.Replace("'", "''")}'"));

    private static string CreateQueryCode(string prefix) =>
        ($"{prefix}{Guid.NewGuid():N}")[..20];

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

        return DateTime.TryParse(value.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)
            ? parsed.Date
            : DateTime.MinValue;
    }

    private static DateTime ParseDate(string? value)
    {
        if (DateTime.TryParse(value, out var parsed))
        {
            return parsed.Date;
        }

        return DateTime.MinValue;
    }

    private static DateTime? ParseNullableDate(string? value)
    {
        return DateTime.TryParse(value, out var parsed) ? parsed.Date : null;
    }

    private static int CalculateDaysOverdue(DateTime? dueDate)
    {
        if (!dueDate.HasValue)
        {
            return 0;
        }

        var days = (DateTime.Today - dueDate.Value.Date).Days;
        return days > 0 ? days : 0;
    }

    private static int CalculateDaysOverdueFromTerms(DateTime docDate, int paymentTermsDays)
    {
        if (docDate == DateTime.MinValue)
        {
            return 0;
        }

        var effectiveDueDate = docDate.AddDays(paymentTermsDays);
        var days = (DateTime.Today - effectiveDueDate.Date).Days;
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

    private sealed record OpenInvoiceRow(DateTime DocDate, DateTime? DueDate, decimal Balance);
}
