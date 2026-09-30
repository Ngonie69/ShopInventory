using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Tests;

/// <summary>
/// Pins the tax id and tax percentage each receipt line sends the platform to a pair the live device
/// accepts.
///
/// The platform matches a line's id and percentage together against the device's taxes, and reads an
/// absent percentage as exempt, and FDMS behind it refuses a percentage written with more than two decimals. The platform client sent ids only, so from the cut-over on 30 September
/// 2026 every till sale was refused: "Line 1 taxId 515 with taxPercent exempt/null is not active in
/// FDMS config". The shipped mappings are read from appsettings.json, so a mapping that drifts off the
/// device fails here rather than at a till.
/// </summary>
public sealed class FiscalLineTaxPairTests
{
    /// <summary>
    /// The live device's taxes as the platform listed them in the RCPT025 refusal of INV4925
    /// (30 September 2026). A null percentage is exempt.
    /// </summary>
    private static readonly (int TaxId, decimal? Percent)[] DeviceTaxes =
    [
        (2, 0.00m),
        (3, null),
        (514, 5.00m),
        (515, 15.50m)
    ];

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
            => throw new NotSupportedException();

        public Task<SubmitReceiptApiResponse> IngestSignedReceiptAsync(
            IngestSignedReceiptApiRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PreflightReceiptApiResponse> PreflightReceiptAsync(
            SubmitReceiptApiRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new PreflightReceiptApiResponse { Valid = true });

        public Task<PreflightReceiptApiResponse> PreflightSignedReceiptAsync(
            IngestSignedReceiptApiRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new PreflightReceiptApiResponse { Valid = true });

        public Task<CheckFiscalisedReceiptApiResponse> CheckReceiptAsync(
            int deviceId, string invoiceNo, ReceiptType receiptType, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<FiscalConfigApiResponse> GetFiscalConfigAsync(
            int deviceId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<FiscalConfigApiResponse> GetFiscalConfigWithApiKeyAsync(
            string? apiKey, int deviceId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<int>> GetKnownDeviceIdsWithApiKeyAsync(
            string? apiKey, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<FiscalStatusApiResponse> GetFiscalStatusAsync(
            int deviceId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class NoConfigCache : IFiscalDeviceConfigCache
    {
        public Task<FiscalConfigApiResponse?> TryGetAsync(
            int deviceId, CancellationToken cancellationToken = default)
            => Task.FromResult<FiscalConfigApiResponse?>(null);
    }

    /// <summary>The Fiscalisation and Tax sections exactly as the API ships them.</summary>
    private static (FiscalisationSettings Fiscal, TaxSettings Tax) ShippedSettings()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ShopInventory", "appsettings.json")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(directory.FullName, "ShopInventory", "appsettings.json"))
            .Build();

        var fiscal = configuration.GetSection(FiscalisationSettings.SectionName).Get<FiscalisationSettings>()!;
        var tax = configuration.GetSection(TaxSettings.SectionName).Get<TaxSettings>()!;
        fiscal.Enabled = true;
        return (fiscal, tax);
    }

    private static async Task<LineApiRequest> SubmitOneLineAsync(InvoiceLineDto line)
    {
        var (fiscal, tax) = ShippedSettings();
        var client = new CapturingClient();
        var service = new FiscalizationService(
            client,
            new NoConfigCache(),
            Options.Create(fiscal),
            Options.Create(tax),
            NullLogger<FiscalizationService>.Instance);

        line.LineNum = 1;
        line.ItemCode = "ICC001";
        line.ItemDescription = "Icecream Cone Choc Dip";
        line.Quantity = 1m;
        line.UnitPrice = 0.42m;
        line.GrossPrice = 0.49m;

        await service.FiscalizePreSapInvoiceAsync(
            new InvoiceDto { DocDate = "2026-09-30", DocCurrency = "USD", DocTotal = 0.49m, Lines = [line] },
            "KEF-FAC-20260930-TEST");

        Assert.NotNull(client.Submitted);
        return Assert.Single(client.Submitted.Lines);
    }

    /// <summary>The platform's own rule: the id and the percentage, rounded to cents, name one device tax.</summary>
    private static void AssertDeviceAccepts(LineApiRequest line)
    {
        var accepted = DeviceTaxes.Any(tax =>
            tax.TaxId == line.TaxId
            && (tax.Percent.HasValue && line.TaxPercent.HasValue
                ? decimal.Round(tax.Percent.Value, 2) == decimal.Round(line.TaxPercent.Value, 2)
                : tax.Percent.HasValue == line.TaxPercent.HasValue));

        Assert.True(accepted,
            $"taxId {line.TaxId} with taxPercent {line.TaxPercent?.ToString() ?? "exempt/null"} is not a tax on the device.");

        // FDMS types the field decimal(5,2) and refuses a longer scale even when the digits are zeros:
        // 15.500 was answered "provided value TaxPercent do not satisfy decimal(5,2)" on INV4925's retry.
        // The platform forwards the number as written, so the check is on the JSON, not the value.
        if (line.TaxPercent.HasValue)
        {
            var written = JsonSerializer.Serialize(line.TaxPercent.Value);
            var decimals = written.Contains('.') ? written.Length - written.IndexOf('.') - 1 : 0;
            Assert.True(decimals <= 2, $"taxPercent is written {written}, which FDMS refuses as not decimal(5,2).");
        }
    }

    [Theory]
    [InlineData("O01", 515, 15.5)]
    [InlineData("O1", 515, 15.5)]
    [InlineData("O8", 515, 15.5)]
    [InlineData("O0", 2, 0)]
    // Zero Rated and Non-Claimable in SAP, confirmed zero-rated on 2026-09-30. Until then both were
    // unmapped and charged and declared at the standard rate.
    [InlineData("O3", 2, 0)]
    [InlineData("O4", 2, 0)]
    public async Task A_till_line_declares_its_tax_id_with_the_rate_it_was_charged(
        string taxCode, int expectedTaxId, double expectedPercent)
    {
        var line = await SubmitOneLineAsync(new InvoiceLineDto { TaxCode = taxCode });

        Assert.Equal(expectedTaxId, line.TaxId);
        Assert.Equal((decimal)expectedPercent, line.TaxPercent);
        AssertDeviceAccepts(line);
    }

    [Fact]
    public async Task A_line_with_no_tax_code_is_standard_rated_on_both_counts()
    {
        var line = await SubmitOneLineAsync(new InvoiceLineDto());

        AssertDeviceAccepts(line);
    }

    [Fact]
    public async Task A_line_read_back_from_SAP_takes_its_code_from_the_VAT_group()
    {
        // SAP returns the code in VatGroup and leaves TaxCode null. Reading TaxCode alone declared a
        // zero-rated line under the standard id.
        var line = await SubmitOneLineAsync(new InvoiceLineDto { VatGroup = "O0" });

        Assert.Equal(2, line.TaxId);
        Assert.Equal(0m, line.TaxPercent);
        AssertDeviceAccepts(line);
    }
}
