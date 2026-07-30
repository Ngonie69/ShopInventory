using ShopInventory.DTOs;
using ShopInventory.Features.ExceptionCenter;
using ShopInventory.Features.ExceptionCenter.Queries.GetExceptionCenter;

namespace ShopInventory.Tests;

/// <summary>
/// The exception center's value rests on the claim that forty failed rows are usually
/// two or three problems. That only holds if the classifier puts documents that share a
/// cause in the same bucket, keeps unrelated causes apart, and never leans on the parts
/// of an error message that vary per document (ids, references, amounts, dates).
/// </summary>
public sealed class ExceptionCenterClusteringTests
{
    [Fact]
    public void PostingPeriodRejectionsFromDifferentDocumentsShareOneCause()
    {
        var first = ExceptionCenterErrorClassifier.Classify(
            "SAP company KEFALOS rejected invoice dates DocDate=2026-07-30, TaxDate=2026-07-30, DocDueDate=2026-08-29, Series=71 " +
            "because at least one document date is outside the configured posting period. SAP error: Posting Date deviates from defined range",
            "SAP Posting");

        var second = ExceptionCenterErrorClassifier.Classify(
            "SAP company KEFALOS rejected invoice dates DocDate=2026-07-29, TaxDate=2026-07-29, DocDueDate=2026-08-28, Series=71 " +
            "because at least one document date is outside the configured posting period. SAP error: Posting Date deviates from defined range",
            "SAP Posting");

        Assert.Equal(first.Signature, second.Signature);
        Assert.Equal("sap-posting-period", first.Signature);
        Assert.Equal(ExceptionCenterErrorClassifier.Families.Sap, first.Family);
        Assert.NotEmpty(first.Guidance);
    }

    [Fact]
    public void UnrelatedCausesDoNotCollapseTogether()
    {
        var postingPeriod = ExceptionCenterErrorClassifier.Classify(
            "Posting Date deviates from defined range", "SAP Posting");

        var stock = ExceptionCenterErrorClassifier.Classify(
            "Insufficient stock in warehouse CHV for item CHZ-001", "SAP Posting");

        var connectivity = ExceptionCenterErrorClassifier.Classify(
            "SAP circuit breaker is open. Retry after 45 seconds.", "SAP Posting");

        var signatures = new[] { postingPeriod.Signature, stock.Signature, connectivity.Signature };
        Assert.Equal(3, signatures.Distinct().Count());
        Assert.Equal(ExceptionCenterErrorClassifier.Families.Stock, stock.Family);
        Assert.Equal(ExceptionCenterErrorClassifier.Families.Connectivity, connectivity.Family);
    }

    [Fact]
    public void UnrecognisedErrorsGroupOnShapeNotOnTheirVaryingParts()
    {
        var first = ExceptionCenterErrorClassifier.Classify(
            "Widget service returned status 17 for document 4821 at 2026-07-30T09:14:02Z", "SAP Posting");

        var second = ExceptionCenterErrorClassifier.Classify(
            "Widget service returned status 23 for document 9107 at 2026-07-30T11:52:44Z", "SAP Posting");

        var different = ExceptionCenterErrorClassifier.Classify(
            "Gadget service refused the mapping for document 4821", "SAP Posting");

        Assert.Equal(first.Signature, second.Signature);
        Assert.StartsWith("unclassified-", first.Signature);
        Assert.NotEqual(first.Signature, different.Signature);
    }

    [Fact]
    public void UnrecognisedErrorsStillCarryAReadableLabel()
    {
        var classification = ExceptionCenterErrorClassifier.Classify(
            "Widget service returned status 17 for document 4821. Stack trace follows and runs on for a very long time indeed.",
            "SAP Posting");

        Assert.Equal("Widget service returned status 17 for document 4821", classification.Label);
        Assert.DoesNotContain("Stack trace", classification.Label);
    }

    [Fact]
    public void DuplicateDocumentsAreNotAdvertisedAsRetryable()
    {
        var classification = ExceptionCenterErrorClassifier.Classify(
            "A/R Invoice for reference INV-8841 already exists in SAP", "SAP Posting");

        Assert.Equal("duplicate-document", classification.Signature);
        // Retrying a duplicate is the one thing that makes it worse, so the guidance
        // has to point at the lookup instead.
        Assert.Contains("client request id", classification.Guidance, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingErrorTextIsItsOwnCauseRatherThanUnclassifiedNoise()
    {
        var blank = ExceptionCenterErrorClassifier.Classify(null, "SAP Posting");
        var whitespace = ExceptionCenterErrorClassifier.Classify("   ", "Sync Retry");

        Assert.Equal("no-error-recorded", blank.Signature);
        Assert.Equal(blank.Signature, whitespace.Signature);
    }

    [Fact]
    public void FiscalizationFailuresAreSeparatedFromSapPostingFailures()
    {
        var fiscal = ExceptionCenterErrorClassifier.Classify(
            "REVMax fiscal device rejected the receipt: certificate expired", "REVMax");

        Assert.Equal("fiscalization", fiscal.Signature);
        Assert.Equal(ExceptionCenterErrorClassifier.Families.Fiscal, fiscal.Family);
    }

    [Fact]
    public void HanaDateLiteralFailuresAreCalledOutByCode()
    {
        var classification = ExceptionCenterErrorClassifier.Classify(
            "SAP SQLQueries execution failed with -2028 (invalid date format)", "SAP Posting");

        Assert.Equal("sap-hana-date-literal", classification.Signature);
        Assert.Contains("yyyy-MM-dd", classification.Guidance);
    }

    /// <summary>
    /// The triage split is on whether anything will reattempt the item on a timer, not on
    /// whether a human is allowed to press retry. A held transfer is retryable by hand
    /// precisely because nothing else will touch it, and calling that "self-healing" would
    /// hide it from the view an operator opens on.
    /// </summary>
    [Fact]
    public void WorkNothingReattemptsOnATimerNeedsAHuman()
    {
        var heldTransfer = new ExceptionCenterItemDto
        {
            Source = ExceptionCenterSources.PendingInventoryTransferPost,
            Status = "Failed",
            LastError = "SAP rejected the transfer",
            RetryCount = 0,
            MaxRetries = 0,
            CanRetry = true,
            OccurredAtUtc = DateTime.UtcNow.AddHours(-3)
        };

        var failedCallback = new ExceptionCenterItemDto
        {
            Source = ExceptionCenterSources.PaymentCallback,
            Status = "Failed",
            LastError = "Declined by issuer",
            MaxRetries = 0,
            CanRetry = false
        };

        GetExceptionCenterHandler.Enrich(heldTransfer, DateTime.UtcNow);
        GetExceptionCenterHandler.Enrich(failedCallback, DateTime.UtcNow);

        Assert.Equal("Blocked", heldTransfer.Triage);
        Assert.Equal("Blocked", failedCallback.Triage);
    }

    [Fact]
    public void QueueWorkWithAttemptsLeftIsLeftToRetryItself()
    {
        var retrying = new ExceptionCenterItemDto
        {
            Source = ExceptionCenterSources.InvoiceQueue,
            Status = "Failed",
            LastError = "SAP circuit breaker is open. Retry after 30 seconds.",
            RetryCount = 1,
            MaxRetries = 3,
            CanRetry = true,
            NextRetryAtUtc = DateTime.UtcNow.AddMinutes(4)
        };

        var exhausted = new ExceptionCenterItemDto
        {
            Source = ExceptionCenterSources.InvoiceQueue,
            Status = "Failed",
            LastError = "SAP circuit breaker is open. Retry after 30 seconds.",
            RetryCount = 3,
            MaxRetries = 3,
            CanRetry = true
        };

        var stalled = new ExceptionCenterItemDto
        {
            Source = ExceptionCenterSources.InvoiceQueue,
            Status = "Processing",
            ProcessingStartedAtUtc = DateTime.UtcNow.AddHours(-2),
            RetryCount = 1,
            MaxRetries = 3
        };

        GetExceptionCenterHandler.Enrich(retrying, DateTime.UtcNow);
        GetExceptionCenterHandler.Enrich(exhausted, DateTime.UtcNow);
        GetExceptionCenterHandler.Enrich(stalled, DateTime.UtcNow);

        Assert.Equal("Retrying", retrying.Triage);
        Assert.False(retrying.IsRetryOverdue);
        Assert.Equal("Blocked", exhausted.Triage);
        Assert.Equal("Stalled", stalled.Triage);
    }

    [Fact]
    public void ARetryScheduledLongAgoIsFlaggedOverdue()
    {
        var overdue = new ExceptionCenterItemDto
        {
            Source = ExceptionCenterSources.InvoiceQueue,
            Status = "Failed",
            LastError = "Connection refused",
            RetryCount = 1,
            MaxRetries = 3,
            CanRetry = true,
            NextRetryAtUtc = DateTime.UtcNow.AddHours(-4)
        };

        GetExceptionCenterHandler.Enrich(overdue, DateTime.UtcNow);

        // Still Retrying by status, but the queue processor plainly is not draining it.
        Assert.True(overdue.IsRetryOverdue);
        Assert.Equal("Retrying", overdue.Triage);
    }
}
