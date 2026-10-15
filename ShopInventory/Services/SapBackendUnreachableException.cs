using System.Net;

namespace ShopInventory.Services;

/// <summary>
/// SAP answered a write, but only to say it could not reach its own database: a 5xx whose error
/// envelope reads <c>Failed to connect to kfldb port 40020 after 0 ms: Couldn't connect to server</c>.
/// </summary>
/// <remarks>
/// Not a refusal, though SAP's own envelope carries it. Writes used to read any envelope as
/// <see cref="SapRequestRejectedException"/>, which told the posting passes two wrong things about an
/// outage: that it was permanent, so each pass spent one of a sale's six attempts and the sale was
/// stranded within minutes; and that nothing was created, so the sale could be sent again without
/// first asking SAP for it. PR #750 fixed the same failure on the duplicate check that runs first.
///
/// <para>An <see cref="HttpRequestException"/>, because that is what it means to every caller:
/// <see cref="SapFailureClassifier.IsTransient"/> waits it out, and
/// <see cref="SapFailureClassifier.DefinitelyNotCommitted"/> does not claim the document is absent,
/// so the post marker stays and the next pass looks the document up before sending it again. A
/// connect failure probably means nothing was written, but "probably" is not the bar that question
/// is held to: getting it wrong puts a second invoice and a second fiscal receipt against one sale.</para>
///
/// <para>Recognised by <see cref="SapFailureClassifier.IsSapBackendUnreachable"/>, which decides on
/// the status as well as the words, so a business refusal that mentions connecting is still a
/// refusal.</para>
/// </remarks>
public sealed class SapBackendUnreachableException(string operation, HttpStatusCode statusCode, string sapMessage)
    : HttpRequestException(
        $"SAP could not reach its database to {operation}: {sapMessage}",
        null,
        statusCode)
{
    /// <summary>What was being attempted, in the words the log line uses.</summary>
    public string Operation { get; } = operation;

    /// <summary>SAP's own message, already pulled out of its error envelope.</summary>
    public string SapMessage { get; } = sapMessage;
}
