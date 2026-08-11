using System.Net;

namespace ShopInventory.Services.Fiscalisation;

/// <summary>
/// A non-success response from the Fiscalisation platform.
/// </summary>
/// <remarks>
/// <see cref="ErrorCode"/> is the field that decides what to do next, not <see cref="StatusCode"/>.
/// A 429 may be safe to retry or not depending on the code; a 409 never is.
/// </remarks>
public class FiscalisationApiException : Exception
{
    public FiscalisationApiException(
        HttpStatusCode statusCode,
        string? errorCode,
        string detail,
        bool hasProblemDocument = true)
        : base(detail)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
        HasProblemDocument = hasProblemDocument;
    }

    public HttpStatusCode StatusCode { get; }

    public string? ErrorCode { get; }

    /// <summary>
    /// Whether the platform explained itself. The platform answers every failure it recognises with a
    /// problem document, so false means the response carried nothing to read — which is what a route this
    /// platform build does not have looks like from here: the request never reaches the API-key
    /// middleware, and the 400 comes back empty.
    /// </summary>
    /// <remarks>
    /// Defaults to true so that an exception constructed by hand describes a platform that answered
    /// properly, which is the ordinary case.
    /// </remarks>
    public bool HasProblemDocument { get; }

    /// <summary>
    /// Whether the platform is telling us the document's fate is unknown and must be reconciled with
    /// a receipt check rather than resubmitted. Resubmitting one of these risks a duplicate receipt,
    /// and a duplicate fiscal receipt cannot be withdrawn.
    /// </summary>
    public bool RequiresReconciliation =>
        StatusCode == HttpStatusCode.Conflict
        || string.Equals(ErrorCode, "FdmsOperationIndeterminate", StringComparison.OrdinalIgnoreCase);
}
