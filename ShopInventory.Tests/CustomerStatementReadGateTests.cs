using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.DTOs;
using ShopInventory.Features.Statements.Queries.GetCustomerStatement;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// A statement for a customer with several linked accounts reads three things per account and the
/// payment terms. It used to start all of them at once, which for a consolidated customer is more
/// than the six SAP slots the whole API has; now it keeps at most two in flight, and still reads
/// everything it needs.
/// </summary>
public sealed class CustomerStatementReadGateTests
{
    private static readonly string[] Accounts = ["ABS001", "ABS002", "ABS003", "ABS004", "ABS005"];

    [Fact]
    public async Task A_consolidated_customers_statement_keeps_at_most_two_sap_reads_in_flight()
    {
        var sap = new CountingSap();
        var handler = new GetCustomerStatementHandler(
            BusinessPartner(),
            sap.Client,
            StatementBuildCaches.Fresh(),
            NullLogger<GetCustomerStatementHandler>.Instance);

        var result = await handler.Handle(
            new GetCustomerStatementQuery("ABS001", new DateTime(2026, 5, 1), new DateTime(2026, 5, 31), Accounts),
            CancellationToken.None);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : string.Empty);

        // 3 reads per account and the payment terms: 16 for five accounts, none of them skipped.
        Assert.Equal(3 * Accounts.Length + 1, sap.Reads);
        Assert.Equal(GetCustomerStatementHandler.MaxConcurrentSapReads, sap.MostInFlight);

        // Every account's opening balance still counts: 5 x (300 - 100).
        Assert.Equal(1000m, result.Value.OpeningBalance);
    }

    private static IBusinessPartnerService BusinessPartner() =>
        StubProxy.For<IBusinessPartnerService>((method, _) => method.Name switch
        {
            nameof(IBusinessPartnerService.GetBusinessPartnerByCodeAsync) => Task.FromResult<BusinessPartnerDto?>(
                new BusinessPartnerDto { CardCode = "ABS001", CardName = "Absolute", Currency = "USD", PayTermGrpCode = 3 }),
            _ => throw new InvalidOperationException(method.Name)
        });

    /// <summary>Every read takes a moment, as SAP's do, and the most running at once is recorded.</summary>
    private sealed class CountingSap
    {
        private int _inFlight;
        private int _mostInFlight;
        private int _reads;

        public int MostInFlight => _mostInFlight;

        public int Reads => _reads;

        public ISAPServiceLayerClient Client => StubProxy.For<ISAPServiceLayerClient>((method, args) => method.Name switch
        {
            nameof(ISAPServiceLayerClient.GetPaymentTermsByCodeAsync) =>
                Read(() => (PaymentTermsDto?)new PaymentTermsDto { GroupNumber = 3, PaymentTermsGroupName = "30 days" }),
            nameof(ISAPServiceLayerClient.ExecuteParameterisedSqlQueryAsync) =>
                Read(() => Rows((string)args![0]!)),
            _ => throw new InvalidOperationException(method.Name)
        });

        private async Task<T> Read<T>(Func<T> answer)
        {
            Interlocked.Increment(ref _reads);
            var now = Interlocked.Increment(ref _inFlight);
            int seen;
            while (now > (seen = Volatile.Read(ref _mostInFlight)) &&
                   Interlocked.CompareExchange(ref _mostInFlight, now, seen) != seen)
            {
            }

            try
            {
                await Task.Delay(100);
                return answer();
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        private static List<Dictionary<string, object?>> Rows(string queryCode) => queryCode switch
        {
            GetCustomerStatementHandler.OpeningBalanceQueryCode =>
                [new Dictionary<string, object?> { ["TotalDebit"] = 300m, ["TotalCredit"] = 100m }],
            _ => []
        };
    }
}
