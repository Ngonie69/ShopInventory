using System.Net;

namespace ShopInventory.Services;

/// <summary>
/// A read from the Service Layer came back unsuccessful, with the status SAP answered.
/// </summary>
/// <remarks>
/// These reads used to throw a bare <see cref="Exception"/>, which left
/// <see cref="SapFailureClassifier"/> guessing from the wording — and the wording of SAP's own
/// "database down" answer (a 500 whose body says <c>Couldn't connect to server</c>) matched none of its
/// words. Every posting pass then counted an outage as a permanent failure: on 14 Sep 2026 a till sale
/// spent all six attempts in six minutes and stayed unposted for a month. The status is the evidence
/// that does not depend on wording, so it travels with the exception.
///
/// <para>The message is unchanged from the bare exception it replaces. The exception centre and
/// stored posting errors are matched on it.</para>
///
/// <para>Reads only. A read commits nothing, so a 5xx can always be asked again. A write's 5xx may
/// have created the document, which is <see cref="SapFailureClassifier.DefinitelyNotCommitted"/>'s
/// question, not this one's.</para>
/// </remarks>
public sealed class SapReadFailedException(HttpStatusCode statusCode, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}
