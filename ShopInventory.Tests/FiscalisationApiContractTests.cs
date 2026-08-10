using System.Net;
using System.Text.Json;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Tests;

/// <summary>
/// Guards the parts of the Fiscalisation contract that fail silently rather than loudly.
/// </summary>
/// <remarks>
/// Everything here protects against a wrong answer that still looks like a right one: a retry that
/// duplicates an irreversible fiscal receipt, a QR code that scans but resolves to nothing, or an
/// enum serialised in a form the platform reads as a different value.
/// </remarks>
public class FiscalisationApiContractTests
{
    private static FiscalisationApiException Error(HttpStatusCode status, string? code)
        => new(status, code, "test");

    [Theory]
    // Rejected before FDMS saw it — the request provably had no effect.
    [InlineData(HttpStatusCode.TooManyRequests, "FdmsTimeout")]
    [InlineData(HttpStatusCode.TooManyRequests, "FdmsPreflightTimeout")]
    [InlineData(HttpStatusCode.TooManyRequests, "TooManyConcurrentRequests")]
    [InlineData(HttpStatusCode.TooManyRequests, "DeviceLockTimeout")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "FdmsRequestNotSent")]
    public void RetriesOnlyFailuresThatNeverReachedFdms(HttpStatusCode status, string code)
        => Assert.True(FiscalisationApiClient.IsSafeToRetry(Error(status, code)));

    [Theory]
    // A rate limit is not a statement about the document's fate.
    [InlineData(HttpStatusCode.TooManyRequests, "ApiKeyRateLimitExceeded")]
    // Idempotency conflicts mean a receipt may exist; resubmitting could duplicate it.
    [InlineData(HttpStatusCode.Conflict, "IDEMPOTENCY_IN_PROGRESS")]
    [InlineData(HttpStatusCode.Conflict, "IDEMPOTENCY_RECONCILIATION_REQUIRED")]
    [InlineData(HttpStatusCode.Conflict, "FdmsOperationIndeterminate")]
    // Already done, or rejected on its merits. Retrying changes nothing.
    [InlineData(HttpStatusCode.BadRequest, "AlreadyFiscalised")]
    [InlineData(HttpStatusCode.BadRequest, "ValidationFailed")]
    [InlineData(HttpStatusCode.BadRequest, "DryRun")]
    [InlineData(HttpStatusCode.Unauthorized, "RejectedByApiKey")]
    [InlineData(HttpStatusCode.InternalServerError, null)]
    public void NeverRetriesAnythingThatMayHaveReachedFdms(HttpStatusCode status, string? code)
        => Assert.False(FiscalisationApiClient.IsSafeToRetry(Error(status, code)));

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "IDEMPOTENCY_REPLAY_UNAVAILABLE", true)]
    [InlineData(HttpStatusCode.BadRequest, "FdmsOperationIndeterminate", true)]
    [InlineData(HttpStatusCode.BadRequest, "ValidationFailed", false)]
    [InlineData(HttpStatusCode.TooManyRequests, "FdmsTimeout", false)]
    public void FlagsTheFailuresThatMustBeReconciledRatherThanResubmitted(
        HttpStatusCode status, string code, bool expected)
        => Assert.Equal(expected, Error(status, code).RequiresReconciliation);

    [Fact]
    public void BackoffIsCappedSoARetryStormCannotStall()
    {
        // Grows, then flattens at the 5s cap plus jitter.
        var early = FiscalisationApiClient.ResolveRetryDelay(0, 500);
        var late = FiscalisationApiClient.ResolveRetryDelay(9, 500);

        Assert.InRange(early.TotalMilliseconds, 500, 651);
        Assert.InRange(late.TotalMilliseconds, 5000, 5151);
    }

    /// <summary>
    /// The platform registers no JsonStringEnumConverter, so it reads enums as integers. Emitting
    /// "CreditNote" instead of 1 would bind to the default and fiscalise a credit note as an invoice.
    /// </summary>
    [Fact]
    public void EnumsSerialiseAsIntegersNotNames()
    {
        var json = JsonSerializer.Serialize(
            new SubmitReceiptApiRequest { ReceiptType = ReceiptType.CreditNote, PaymentType = MoneyType.Card },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"receiptType\":1", json);
        Assert.Contains("\"paymentType\":1", json);
        Assert.DoesNotContain("CreditNote", json);
    }
}

/// <summary>
/// The QR payload is composed here rather than returned by the platform, and a malformed one still
/// renders and still scans — it just resolves to an invalid ZIMRA page. Nothing else would catch it.
/// </summary>
public class FiscalReceiptQrComposerTests
{
    // MD5 of the decoded bytes of "a2V5" is deterministic; the composer takes its first 16 hex chars.
    private const string Signature = "a2V5";

    [Fact]
    public void VerificationCodeIsSixteenUppercaseHexCharacters()
    {
        var code = FiscalReceiptQrComposer.TryCreateVerificationCode(Signature);

        Assert.NotNull(code);
        Assert.Equal(16, code!.Length);
        Assert.Matches("^[0-9A-F]{16}$", code);
    }

    [Fact]
    public void QrPayloadUsesZimraFieldWidths()
    {
        var code = FiscalReceiptQrComposer.TryCreateVerificationCode(Signature)!;

        var payload = FiscalReceiptQrComposer.BuildQrPayload(
            "https://fdms.zimra.co.zw/",
            deviceId: 22862,
            receiptDate: new DateTime(2026, 8, 10),
            receiptGlobalNo: 1234,
            verificationCode: code);

        // {qrUrl}/{deviceId:D10}{ddMMyyyy}{receiptGlobalNo:D10}{verificationCode}
        Assert.Equal($"https://fdms.zimra.co.zw/0000022862100820260000001234{code}", payload);
    }

    [Fact]
    public void TrailingSlashOnTheConfiguredUrlIsNotDoubled()
    {
        var withSlash = FiscalReceiptQrComposer.BuildQrPayload(
            "https://fdms.zimra.co.zw/", 1, new DateTime(2026, 1, 2), 3, "ABCD");
        var withoutSlash = FiscalReceiptQrComposer.BuildQrPayload(
            "https://fdms.zimra.co.zw", 1, new DateTime(2026, 1, 2), 3, "ABCD");

        Assert.Equal(withSlash, withoutSlash);
        Assert.DoesNotContain("zw//", withSlash);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64 !!")]
    public void AnUnusableSignatureYieldsNoCodeRatherThanThrowing(string? signature)
    {
        // A fiscalised receipt must never be reported as failed just because its QR could not be built.
        Assert.Null(FiscalReceiptQrComposer.TryCreateVerificationCode(signature));
        Assert.Null(FiscalReceiptQrComposer.BuildQrPayload("https://x/", 1, DateTime.UtcNow, 1, null));
        Assert.NotNull(FiscalReceiptQrComposer.ResolveUnavailableReason("https://x/", null));
    }

    [Fact]
    public void MissingQrUrlIsReportedSeparatelyFromAMissingSignature()
    {
        Assert.Contains("QR URL", FiscalReceiptQrComposer.ResolveUnavailableReason(null, "ABCD"));
        Assert.Contains("signature", FiscalReceiptQrComposer.ResolveUnavailableReason("https://x/", null));
        Assert.Null(FiscalReceiptQrComposer.ResolveUnavailableReason("https://x/", "ABCD"));
    }

    [Fact]
    public void VerificationCodeIsGroupedInFoursForDisplay()
        => Assert.Equal("A1B2-C3D4-E5F6-0718", FiscalReceiptQrComposer.FormatVerificationCode("A1B2C3D4E5F60718"));
}
