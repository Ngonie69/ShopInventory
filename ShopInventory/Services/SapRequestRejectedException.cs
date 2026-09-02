using System.Net;

namespace ShopInventory.Services;

/// <summary>
/// SAP answered, and the answer was a refusal: a business rule, a bad request, an approver who may not
/// decide. Distinct from a transport failure so a handler can say "SAP refused" with SAP's own words
/// rather than "try again later", and so <see cref="SapFailureClassifier"/> never retries it.
/// </summary>
public sealed class SapRequestRejectedException : Exception
{
    public SapRequestRejectedException(string operation, HttpStatusCode statusCode, string sapMessage)
        : base($"SAP refused to {operation}: {sapMessage}")
    {
        Operation = operation;
        StatusCode = statusCode;
        SapMessage = sapMessage;
    }

    /// <summary>What was being attempted, in the words the log line uses.</summary>
    public string Operation { get; }

    public HttpStatusCode StatusCode { get; }

    /// <summary>SAP's own message, already pulled out of its error envelope.</summary>
    public string SapMessage { get; }
}
