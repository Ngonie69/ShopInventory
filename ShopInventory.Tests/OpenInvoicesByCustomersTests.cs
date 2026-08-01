using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Features.Invoices.Queries.GetOpenInvoicesByCustomers;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the endpoint that replaced the customer portal's unbounded invoice reads.
/// </summary>
/// <remarks>
/// To show what a customer owes, the portal called the by-customer endpoint with no dates. That
/// falls through to <c>GetInvoicesByCustomerAsync(cardCode)</c> — every invoice the account has
/// ever had, paged until exhausted — and the portal then kept only the few still carrying a
/// balance. It did that once per linked account, and the dashboard, the invoices page and the
/// aging summary all went through it. On an account trading daily for years that is the whole
/// history pulled across to answer a question about the present.
///
/// The fix is to let SAP filter. <c>GetOpenInvoicesByCustomersAsync</c> is already proven to do
/// that (see <see cref="SapCustomerFanOutTests"/>); what these cover is that the query routes to
/// it, for the whole account set at once.
/// </remarks>
public class OpenInvoicesByCustomersTests
{
    [Fact]
    public async Task Open_invoices_for_many_accounts_are_one_call_not_one_per_account()
    {
        var calls = new List<IReadOnlyList<string>>();
        var handler = CreateHandler(calls, invoices: []);

        await handler.Handle(new GetOpenInvoicesByCustomersQuery(["MAIN01", "SUB01", "SUB02"]), default);

        var cardCodes = Assert.Single(calls);
        Assert.Equal(["MAIN01", "SUB01", "SUB02"], cardCodes);
    }

    [Fact]
    public async Task Duplicate_and_blank_card_codes_are_dropped_before_sap_sees_them()
    {
        var calls = new List<IReadOnlyList<string>>();
        var handler = CreateHandler(calls, invoices: []);

        await handler.Handle(
            new GetOpenInvoicesByCustomersQuery(["MAIN01", " main01 ", "", "  ", "SUB01"]),
            default);

        Assert.Equal(["MAIN01", "SUB01"], Assert.Single(calls));
    }

    [Fact]
    public async Task An_empty_account_set_never_reaches_sap()
    {
        var calls = new List<IReadOnlyList<string>>();
        var handler = CreateHandler(calls, invoices: []);

        var result = await handler.Handle(new GetOpenInvoicesByCustomersQuery([]), default);

        Assert.True(result.IsError);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task Invoices_come_back_with_the_account_they_belong_to()
    {
        // The portal groups the one response by card code, so CardCode has to survive the mapping.
        var handler = CreateHandler(
            new List<IReadOnlyList<string>>(),
            invoices:
            [
                new Invoice { DocEntry = 1, DocNum = 88401, CardCode = "MAIN01", DocTotal = 1240m, PaidToDate = 0m },
                new Invoice { DocEntry = 2, DocNum = 88402, CardCode = "SUB01", DocTotal = 500m, PaidToDate = 200m }
            ]);

        var result = await handler.Handle(
            new GetOpenInvoicesByCustomersQuery(["MAIN01", "SUB01"]),
            default);

        Assert.False(result.IsError);
        Assert.Equal(2, result.Value.Count);
        Assert.Collection(
            result.Value.Invoices!,
            first => Assert.Equal("MAIN01", first.CardCode),
            second => Assert.Equal("SUB01", second.CardCode));
    }

    private static GetOpenInvoicesByCustomersHandler CreateHandler(
        List<IReadOnlyList<string>> calls,
        List<Invoice> invoices)
    {
        var sap = StubProxy.For<ISAPServiceLayerClient>((method, args) =>
        {
            if (method.Name != nameof(ISAPServiceLayerClient.GetOpenInvoicesByCustomersAsync))
            {
                throw new InvalidOperationException(
                    $"ISAPServiceLayerClient.{method.Name} was not expected — the open-invoice read must not fan out.");
            }

            calls.Add(((IEnumerable<string>)args![0]!).ToList());
            return Task.FromResult(invoices);
        });

        return new GetOpenInvoicesByCustomersHandler(
            sap,
            Options.Create(new SAPSettings { Enabled = true }),
            NullLogger<GetOpenInvoicesByCustomersHandler>.Instance);
    }
}
