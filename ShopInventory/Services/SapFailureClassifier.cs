using System.Net;

namespace ShopInventory.Services;

public static class SapFailureClassifier
{
    public static bool IsTransient(Exception exception, CancellationToken cancellationToken = default)
    {
        if (exception is OperationCanceledException)
        {
            return !cancellationToken.IsCancellationRequested;
        }

        if (exception is SapCircuitOpenException ||
            exception is HttpRequestException ||
            exception is TimeoutException)
        {
            return true;
        }

        // SAP answered, and the answer was a refusal. Retrying cannot change it, and the type says
        // so where the message cannot be trusted to: "SAP refused to create the invoice" contains
        // the word this classifier looks for to spot a *connection* refused, so a rejection read as
        // transient and never spent an attempt. The type check has to come first.
        if (exception is SapRequestRejectedException)
        {
            return false;
        }

        return ContainsAvailabilitySignal(exception.GetBaseException().Message);
    }

    public static bool ContainsAvailabilitySignal(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var normalized = message.ToLowerInvariant();
        return normalized.Contains("circuit") ||
               normalized.Contains("timeout") ||
               normalized.Contains("timed out") ||
               normalized.Contains("connection") ||
               normalized.Contains("network") ||
               normalized.Contains("unavailable") ||
               normalized.Contains("service unavailable") ||
               normalized.Contains("temporarily") ||
               normalized.Contains("refused") ||
               normalized.Contains("name or service") ||
               normalized.Contains("502") ||
               normalized.Contains("503") ||
               normalized.Contains("504");
    }

    /// <summary>
    /// A rejection that says the stock is not there. Retrying cannot clear it — the document has
    /// to be re-cut or the warehouse reconciled — so the work belongs in front of a person rather
    /// than back on the queue.
    /// </summary>
    public static bool IsPermanentStockRejection(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var normalized = message.ToLowerInvariant();
        return (normalized.Contains("insufficient") && (normalized.Contains("stock") || normalized.Contains("quantity")))
               || normalized.Contains("not enough")
               || normalized.Contains("negative inventory")
               || normalized.Contains("quantity falls");
    }

    /// <summary>
    /// True only when SAP certainly did not create the document the failure was raised for.
    /// </summary>
    /// <remarks>
    /// The question a posting service has to answer before it retries: may this sale be sent again,
    /// or might SAP already hold an invoice for it? Getting it wrong in one direction leaves a sale
    /// uninvoiced; in the other it puts a second invoice — and a second fiscal receipt — against one
    /// sale, which cannot be withdrawn from ZIMRA and needs a manual credit note.
    ///
    /// <para>
    /// So the list is short and deliberately closed. Only three things prove nothing was created:
    /// the client refused to send it, SAP rejected the posting dates, and SAP answered the post with
    /// a refusal. Everything else — a timeout, a dropped connection, a reply that could not be read —
    /// leaves the document's existence unknown, and unknown must be treated as "it may exist".
    /// </para>
    ///
    /// <para>
    /// Note what is <i>not</i> here: a bare <see cref="Exception"/>. CreateInvoiceAsync throws one
    /// when it cannot deserialise SAP's reply, and that happens after SAP has committed.
    /// </para>
    /// </remarks>
    public static bool DefinitelyNotCommitted(Exception? exception) =>
        exception is ArgumentException
                  or SapPostingPeriodException
                  or SapRequestRejectedException;

    public static bool IsTransientStatusCode(HttpStatusCode statusCode)
    {
        return statusCode == HttpStatusCode.RequestTimeout ||
               statusCode == HttpStatusCode.TooManyRequests ||
               statusCode == HttpStatusCode.BadGateway ||
               statusCode == HttpStatusCode.ServiceUnavailable ||
               statusCode == HttpStatusCode.GatewayTimeout ||
               (int)statusCode >= 500;
    }
}