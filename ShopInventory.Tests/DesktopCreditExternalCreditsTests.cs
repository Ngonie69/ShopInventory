using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Features.DesktopCreditNotes;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using Xunit;

namespace ShopInventory.Tests;

/// <summary>
/// Pins how a desktop credit learns what ZIMRA already holds against its receipt from the other routes
/// onto it. Driven through the real <see cref="RevmaxClient"/>, so the device's JSON is read the way
/// production reads it — the reference to the original arrives as an untyped object.
/// </summary>
public sealed class DesktopCreditExternalCreditsTests
{
    private const int Device = 22862;
    private const int Receipt = 216877;

    private static DesktopSaleEntity Sale(int? sapDocEntry = 4242, DesktopSaleConsolidationStatus status =
        DesktopSaleConsolidationStatus.Consolidated) => new()
    {
        ExternalReferenceId = "GRC-FAC-20260911-286EEC7389FD", CardCode = "COR007", Currency = "USD",
        DocDate = new DateTime(2026, 9, 11, 0, 0, 0, DateTimeKind.Utc), SapDocEntry = sapDocEntry,
        ConsolidationStatus = status
    };

    [Fact]
    public async Task Credits_filed_under_SAP_memo_numbers_count_only_when_they_reference_this_receipt()
    {
        var device = new FakeDevice
        {
            ["91000"] = NotFound,
            ["91001"] = Credit(91001, globalNo: 217001, total: -144.34m, refDevice: Device, refGlobal: Receipt),
            // The same receipt credited under another reference: a different original.
            ["91002"] = Credit(91002, globalNo: 217002, total: -50m, refDevice: Device, refGlobal: 216999),
            // Another taxpayer's device answering the same number.
            ["91003"] = Credit(91003, globalNo: 55, total: -80m, refDevice: 3180, refGlobal: Receipt, device: 3180),
            // An invoice of ours that happens to carry the memo's number.
            ["91004"] = Invoice(91004),
            // The reference as strings, which FDMS clients do send.
            ["91005"] = Credit(91005, globalNo: 217005, total: -10m, refDevice: Device, refGlobal: Receipt, quoted: true)
        };
        var credits = Lookup(device, [91000, 91001, 91002, 91003, 91004, 91005, 91001]);

        var history = await credits.FindAsync(Sale(), Device, Receipt, default);

        Assert.Equal(154.34m, history.Amount);
        Assert.Equal(2, history.Credits.Count);
        Assert.Contains("SAP credit memo 91001 (receipt 217001, USD 144.34)", history.Credits);
    }

    [Fact]
    public async Task A_sale_with_no_SAP_invoice_is_not_checked_so_a_till_credits_while_SAP_is_down()
    {
        var credits = new DesktopCreditExternalCredits(StubProxy.Unused<ISAPServiceLayerClient>(),
            StubProxy.Unused<IRevmaxClient>(), NullLogger<DesktopCreditExternalCredits>.Instance);

        var history = await credits.FindAsync(Sale(sapDocEntry: null, DesktopSaleConsolidationStatus.Pending),
            Device, Receipt, default);

        Assert.Same(DesktopCreditExternalHistory.None, history);
    }

    [Fact]
    public async Task SAP_that_cannot_be_asked_refuses_rather_than_reporting_no_credits()
    {
        var sap = StubProxy.For<ISAPServiceLayerClient>((_, _) => throw new HttpRequestException("SAP is down"));
        var credits = new DesktopCreditExternalCredits(sap, StubProxy.Unused<IRevmaxClient>(),
            NullLogger<DesktopCreditExternalCredits>.Instance);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            credits.FindAsync(Sale(), Device, Receipt, default));

        Assert.Contains("not known whether ZIMRA already holds credits", refusal.Message);
    }

    [Fact]
    public async Task A_device_that_cannot_answer_for_a_memo_refuses_rather_than_skipping_it()
    {
        var credits = Lookup(new FakeDevice { ["91001"] = """{"Code":"0","Message":"Init error -1","Data":""}""" }, [91001]);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            credits.FindAsync(Sale(), Device, Receipt, default));

        Assert.Contains("SAP credit memo 91001", refusal.Message);
    }

    [Fact]
    public async Task A_credit_note_whose_reference_cannot_be_read_refuses_rather_than_being_skipped()
    {
        var unreadable = Credit(91001, globalNo: 217001, total: -10m, refDevice: Device, refGlobal: Receipt)
            .Replace("\"receiptGlobalNo\": " + Receipt, "\"globalNumber\": " + Receipt);
        var credits = Lookup(new FakeDevice { ["91001"] = unreadable }, [91001]);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            credits.FindAsync(Sale(), Device, Receipt, default));

        Assert.Contains("could not be read", refusal.Message);
    }

    private const string NotFound = """{"Code":"0","Message":"Invoice not Found","Data":""}""";

    private static string Credit(int number, long globalNo, decimal total, int refDevice, int refGlobal,
        int device = Device, bool quoted = false)
    {
        string N(long n) => quoted ? $"\"{n}\"" : n.ToString();
        return $$"""
            {
              "Code": "1", "Message": "Success", "DeviceID": "{{device}}", "FiscalDay": "525",
              "Data": {
                "receiptType": "CreditNote", "receiptCurrency": "USD", "receiptGlobalNo": {{globalNo}},
                "invoiceNo": "{{device}}-{{number}}",
                "creditDebitNote": { "receiptID": 9912345, "deviceID": {{N(refDevice)}}, "receiptGlobalNo": {{N(refGlobal)}}, "fiscalDayNo": 524 },
                "receiptLinesTaxInclusive": true, "receiptLines": [], "receiptTotal": {{total.ToString(System.Globalization.CultureInfo.InvariantCulture)}}
              }
            }
            """;
    }

    private static string Invoice(int number) => $$"""
        {
          "Code": "1", "Message": "Success", "DeviceID": "{{Device}}",
          "Data": { "receiptType": "FiscalInvoice", "receiptCurrency": "USD", "receiptGlobalNo": 216000,
                    "invoiceNo": "{{Device}}-{{number}}", "creditDebitNote": null, "receiptTotal": 99.00 }
        }
        """;

    private static DesktopCreditExternalCredits Lookup(FakeDevice device, int[] memoDocNums)
    {
        var sap = StubProxy.For<ISAPServiceLayerClient>((method, args) =>
            method.Name == nameof(ISAPServiceLayerClient.GetCreditNotesByCustomerAsync) && args!.Length == 4
                ? Task.FromResult(memoDocNums.Select(n => new SAPCreditNote { DocNum = n, CardCode = (string)args[0]! }).ToList())
                : throw new InvalidOperationException($"ISAPServiceLayerClient.{method.Name} was not expected."));
        var settings = Options.Create(new RevmaxSettings { BaseUrl = "https://revmax.invalid", DefaultRefDeviceId = Device });
        var client = new RevmaxClient(new HttpClient(device), settings, NullLogger<RevmaxClient>.Instance);
        return new DesktopCreditExternalCredits(sap, client, NullLogger<DesktopCreditExternalCredits>.Instance);
    }

    /// <summary>Answers GetInvoice by number, as the device would; every number not listed is not found.</summary>
    private sealed class FakeDevice : HttpMessageHandler, IEnumerable<KeyValuePair<string, string>>
    {
        private readonly Dictionary<string, string> receipts = [];
        public string this[string number] { set => receipts[number] = value; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var number = Uri.UnescapeDataString(request.RequestUri!.Segments[^1]);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(receipts.GetValueOrDefault(number, NotFound), Encoding.UTF8, "application/json")
            });
        }

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => receipts.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
