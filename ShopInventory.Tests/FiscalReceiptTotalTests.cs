using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Tests;

/// <summary>
/// Pins the amount a fiscal receipt declares against the amount the customer actually handed over.
///
/// These are two different calculations meeting at one number. A receipt line carries a per-UNIT
/// price in whole cents, so multiplying it back out by quantity is not the same as the sale's own
/// figure, which rounds tax once over the whole line. Re-deriving the receipt total from the lines
/// therefore declared something the till never charged — 100 x $1.99 is taken as $229.85 and was
/// declared as $230.00 — and a receipt is a statement to the revenue authority, not a summary.
///
/// So the total is the sale's own. The per-line breakdown stays in cents, as a printed receipt must,
/// and is allowed not to re-add to the penny.
/// </summary>
public sealed class FiscalReceiptTotalTests
{
    /// <summary>Captures what would have gone to the platform.</summary>
    private sealed class CapturingClient : IFiscalisationApiClient
    {
        public SubmitReceiptApiRequest? Submitted { get; private set; }

        public Task<SubmitReceiptApiResponse> SubmitReceiptAsync(
            SubmitReceiptApiRequest request, CancellationToken cancellationToken = default)
        {
            Submitted = request;
            return Task.FromResult(new SubmitReceiptApiResponse());
        }

        public Task<SubmitReceiptApiResponse> SubmitSapReceiptAsync(
            SapFiscaliseReceiptApiRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No SAP receipt expected.");

        public Task<SubmitReceiptApiResponse> IngestSignedReceiptAsync(
            IngestSignedReceiptApiRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No signed receipt expected.");

        // Preflight changes nothing and is advisory, so a double that is not testing it answers
        // "no objection" rather than throwing — the callers treat an unreachable preflight the same way.
        public Task<PreflightReceiptApiResponse> PreflightReceiptAsync(
            SubmitReceiptApiRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new PreflightReceiptApiResponse { Valid = true });

        public Task<PreflightReceiptApiResponse> PreflightSignedReceiptAsync(
            IngestSignedReceiptApiRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new PreflightReceiptApiResponse { Valid = true });

        public Task<CheckFiscalisedReceiptApiResponse> CheckReceiptAsync(
            int deviceId, string invoiceNo, ReceiptType receiptType, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No receipt check expected.");

        public Task<FiscalConfigApiResponse> GetFiscalConfigAsync(
            int deviceId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No config call expected.");

        public Task<FiscalConfigApiResponse> GetFiscalConfigWithApiKeyAsync(
            string? apiKey, int deviceId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No config call expected.");

        public Task<FiscalStatusApiResponse> GetFiscalStatusAsync(
            int deviceId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No status call expected.");
    }

    private sealed class NoConfigCache : IFiscalDeviceConfigCache
    {
        public Task<FiscalConfigApiResponse?> TryGetAsync(
            int deviceId, CancellationToken cancellationToken = default)
            => Task.FromResult<FiscalConfigApiResponse?>(null);
    }

    private static readonly TaxSettings Tax = new()
    {
        VatRate = 0.155m,
        RatesByTaxCode = new(StringComparer.OrdinalIgnoreCase) { ["O01"] = 0.155m, ["O0"] = 0m }
    };

    /// <summary>
    /// Builds the receipt the way a till sale does: net unit price, VAT rounded once per line, and a
    /// tax-inclusive per-unit price on the line.
    /// </summary>
    private static async Task<(decimal Charged, decimal Declared)> SubmitAsync(
        params (decimal UnitPrice, decimal Quantity, string TaxCode)[] basket)
    {
        var lines = basket.Select((b, i) =>
        {
            // Rounded to money, as the handler does: a line total is an amount someone pays, and the
            // column that stores it is decimal(18,2).
            var lineTotal = Math.Round(b.Quantity * b.UnitPrice, 2, MidpointRounding.AwayFromZero);
            return new
            {
                Dto = new InvoiceLineDto
                {
                    LineNum = i + 1,
                    ItemCode = $"ITEM-{i + 1}",
                    Quantity = b.Quantity,
                    UnitPrice = b.UnitPrice,
                    GrossPrice = Math.Round(b.UnitPrice * (1 + Tax.RateFor(b.TaxCode)), 2, MidpointRounding.AwayFromZero),
                    LineTotal = lineTotal,
                    TaxCode = b.TaxCode
                },
                Net = lineTotal,
                Vat = Tax.VatOn(lineTotal, b.TaxCode)
            };
        }).ToList();

        var charged = lines.Sum(l => l.Net) + lines.Sum(l => l.Vat);

        var client = new CapturingClient();
        var service = new FiscalizationService(
            client,
            new NoConfigCache(),
            Options.Create(new FiscalisationSettings { Enabled = true, DefaultTaxId = 517 }),
            NullLogger<FiscalizationService>.Instance);

        await service.FiscalizePreSapInvoiceAsync(
            new InvoiceDto
            {
                DocDate = "2026-08-14",
                DocCurrency = "USD",
                DocTotal = charged,
                Lines = lines.Select(l => l.Dto).ToList()
            },
            "VEND-20260814-0001");

        Assert.NotNull(client.Submitted);
        return (charged, client.Submitted.PaymentAmount);
    }

    // The reviewer's own baskets. Every one of these declared a different amount than it charged.
    [Theory]
    [InlineData(1.99, 100)]
    [InlineData(2.50, 12)]
    [InlineData(0.50, 20)]
    [InlineData(1.99, 4)]
    [InlineData(4.99, 3)]
    [InlineData(0.85, 6)]
    [InlineData(0.03, 12)]
    public async Task The_receipt_declares_what_was_charged(double unitPrice, int quantity)
    {
        var (charged, declared) = await SubmitAsync(((decimal)unitPrice, quantity, "O01"));

        Assert.Equal(charged, declared);
    }

    [Fact]
    public async Task A_weighed_line_declares_what_was_charged()
    {
        var (charged, declared) = await SubmitAsync((3.45m, 1.234m, "O01"));

        Assert.Equal(charged, declared);
    }

    [Fact]
    public async Task A_mixed_rate_basket_declares_what_was_charged()
    {
        var (charged, declared) = await SubmitAsync(
            (1.99m, 4, "O01"),
            (8.75m, 2, "O0"));

        Assert.Equal(charged, declared);
    }

    [Fact]
    public async Task The_headline_case_is_the_amount_taken_not_the_lines_multiplied_out()
    {
        // 100 x $1.99: net 199.00, VAT rounded once over the line 30.85, so 229.85 is taken. The line
        // price is 1.99 x 1.155 = 2.29845, which as a cent-granular unit price is 2.30 — multiplying
        // that back out gives 230.00, fifteen cents that were never charged.
        var (charged, declared) = await SubmitAsync((1.99m, 100, "O01"));

        Assert.Equal(229.85m, charged);
        Assert.Equal(229.85m, declared);
    }

    [Fact]
    public async Task A_caller_that_states_no_total_still_gets_one_derived_from_its_lines()
    {
        // The fallback for any caller that does not carry a document total. It has to keep working:
        // declaring nothing would be worse than declaring the lines multiplied out.
        var client = new CapturingClient();
        var service = new FiscalizationService(
            client,
            new NoConfigCache(),
            Options.Create(new FiscalisationSettings { Enabled = true, DefaultTaxId = 517 }),
            NullLogger<FiscalizationService>.Instance);

        await service.FiscalizePreSapInvoiceAsync(
            new InvoiceDto
            {
                DocDate = "2026-08-14",
                DocCurrency = "USD",
                DocTotal = 0m,
                Lines = [new InvoiceLineDto { LineNum = 1, ItemCode = "A", Quantity = 2, UnitPrice = 5m }]
            },
            "VEND-20260814-0002");

        Assert.NotNull(client.Submitted);
        Assert.Equal(10m, client.Submitted.PaymentAmount);
    }
}
