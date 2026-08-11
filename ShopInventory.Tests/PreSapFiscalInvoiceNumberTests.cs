using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Services;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Tests;

/// <summary>
/// The invoice number a pre-SAP receipt is fiscalised under is part of the platform's idempotency
/// key, and it shares a namespace with every SAP DocNum for the same taxpayer.
/// </summary>
/// <remarks>
/// A bare numeric external reference is the hazard: when SAP later issues that same number to an
/// unrelated invoice, the two collide on one key. The second is then either refused or — worse, on
/// the submit path — answered with the first one's archived receipt, which is a silent wrong answer
/// on an irreversible operation. Prefixing lifts pre-SAP receipts out of that namespace.
/// </remarks>
public class PreSapFiscalInvoiceNumberTests
{
    private static FiscalizationService Service(string prefix = "SI-")
        => new(
            new UnusedClient(),
            new UnusedConfigCache(),
            Options.Create(new FiscalisationSettings { PreSapInvoiceNoPrefix = prefix }),
            NullLogger<FiscalizationService>.Instance);

    [Theory]
    [InlineData("10234", "SI-10234")]
    [InlineData("7", "SI-7")]
    [InlineData("  4821  ", "SI-4821")]
    public void NumericReferencesArePrefixedOutOfTheSapDocNumNamespace(string reference, string expected)
        => Assert.Equal(expected, Service().BuildPreSapInvoiceNo(reference));

    [Theory]
    // The generated references already start with a letter, so they pass through byte-identical and
    // anything already fiscalised under them keeps its fiscal identity.
    [InlineData("DESKTOP-20260810120000-a1b2c3d4")]
    [InlineData("DS-20260810120000-a1b2c3d4")]
    [InlineData("SO-CONV-SO1234-a1b2c3d4")]
    [InlineData("SI-10234")]
    public void NonNumericReferencesArePassedThroughUnchanged(string reference)
        => Assert.Equal(reference, Service().BuildPreSapInvoiceNo(reference));

    [Fact]
    public void TheSameReferenceAlwaysProducesTheSameInvoiceNumber()
    {
        // Stability across retries is what the server-derived idempotency key depends on.
        var service = Service();

        Assert.Equal(
            service.BuildPreSapInvoiceNo("10234"),
            service.BuildPreSapInvoiceNo("10234"));
    }

    [Fact]
    public async Task AMissingReferenceIsRejectedRatherThanFiscalisedAnonymously()
    {
        var service = Service();

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.FiscalizePreSapInvoiceAsync(new ShopInventory.DTOs.InvoiceDto(), "  "));
    }

    private sealed class UnusedClient : IFiscalisationApiClient
    {
        public Task<SubmitReceiptApiResponse> SubmitSapReceiptAsync(
            SapFiscaliseReceiptApiRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No network call expected.");

        public Task<SubmitReceiptApiResponse> SubmitReceiptAsync(
            SubmitReceiptApiRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No network call expected.");

        public Task<SubmitReceiptApiResponse> IngestSignedReceiptAsync(
            IngestSignedReceiptApiRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No network call expected.");

        public Task<CheckFiscalisedReceiptApiResponse> CheckReceiptAsync(
            int deviceId, string invoiceNo, ReceiptType receiptType, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No network call expected.");

        public Task<FiscalConfigApiResponse> GetFiscalConfigAsync(
            int deviceId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No network call expected.");

        public Task<FiscalStatusApiResponse> GetFiscalStatusAsync(
            int deviceId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No network call expected.");
    }

    private sealed class UnusedConfigCache : IFiscalDeviceConfigCache
    {
        public Task<FiscalConfigApiResponse?> TryGetAsync(
            int deviceId, CancellationToken cancellationToken = default)
            => Task.FromResult<FiscalConfigApiResponse?>(null);
    }
}
