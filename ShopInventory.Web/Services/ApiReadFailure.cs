using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace ShopInventory.Web.Services;

/// <summary>
/// Says in one sentence why a read from the API came back with nothing, for a page to show as it is.
/// </summary>
/// <remarks>
/// <para>
/// Written for the reads that used to return null on any failure, which left a page able to say only
/// "check that the API is running". That sentence was shown with the API up and healthy, and it could not
/// tell an operator — or whoever they sent the screenshot to — which of four different things happened: the
/// Web could not connect, the API took too long, the API answered with an error, or the API answered and
/// the Web could not read what it sent. Each points somewhere different, so each is named.
/// </para>
/// <para>
/// Unlike <see cref="ApiErrorResponse.GetFriendlyMessage(HttpStatusCode?, string?, string, string?, string?)"/>,
/// which turns a refusal into advice for the person who pressed a button, this keeps the status code and the
/// exception's own words. A list that failed to load has no button to advise about; what is useful is the
/// fact, and these pages are only shown to staff.
/// </para>
/// </remarks>
internal static class ApiReadFailure
{
    // Long enough for any sentence the API writes into a problem's detail, short enough that a plain-text
    // body — a proxy's error page, a stack trace — cannot fill the notice.
    private const int MaxDetailLength = 300;

    /// <summary>The API answered, with a status that is not a success.</summary>
    public static string ForStatus(HttpStatusCode status, string? responseBody)
    {
        var code = (int)status;
        var reason = ReasonPhrases.GetReasonPhrase(code);
        var answered = string.IsNullOrEmpty(reason)
            ? $"The API answered {code}"
            : $"The API answered {code} {reason}";

        // Empty fallback, so that a body with nothing to say — an IIS or proxy HTML page, an empty 503 while
        // an app pool starts — leaves the status standing alone rather than being padded with a guess.
        var detail = ApiErrorResponse.GetFriendlyMessage(status, responseBody, fallbackMessage: string.Empty);

        return string.IsNullOrWhiteSpace(detail)
            ? $"{answered}, with no explanation."
            : $"{answered}: {Clip(detail)}";
    }

    /// <summary>The request did not come back with an answer the Web could use.</summary>
    /// <param name="exception">What the request or the read of its reply threw.</param>
    /// <param name="timeout">The client's timeout, so that a timeout can say how long it waited.</param>
    public static string ForException(Exception exception, TimeSpan timeout) =>
        exception switch
        {
            // HttpClient reports its own timeout as a cancellation with a TimeoutException inside it, and
            // a caller's cancellation as a bare one. Only the first is the API being slow.
            TaskCanceledException { InnerException: TimeoutException } =>
                timeout == Timeout.InfiniteTimeSpan
                    ? "The API did not answer in time."
                    : $"The API did not answer within {timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} seconds.",

            OperationCanceledException =>
                "The request was cancelled before the API answered.",

            // The API answered and said it succeeded, and the Web's copy of the reply's shape disagrees with
            // it. The message names the JSON path that failed, which is the whole diagnosis.
            JsonException json =>
                $"The API answered, but the Web could not read its reply: {Clip(json.Message)}",

            // No status means no answer at all: refused, reset, unresolvable, a certificate the Web does
            // not trust. The message says which, and names the host.
            HttpRequestException { StatusCode: null } http =>
                $"The Web could not reach the API: {Clip(http.Message)}",

            HttpRequestException { StatusCode: { } status } http =>
                $"The API answered {(int)status}: {Clip(http.Message)}",

            _ => $"{exception.GetType().Name}: {Clip(exception.Message)}"
        };

    private static string Clip(string text)
    {
        var flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= MaxDetailLength ? flat : flat[..MaxDetailLength] + "…";
    }
}
