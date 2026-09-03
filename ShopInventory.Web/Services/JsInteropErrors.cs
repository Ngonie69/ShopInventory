using Microsoft.JSInterop;

namespace ShopInventory.Web.Services;

/// <summary>
/// Recovers the message a browser helper threw, for a page that wants to show it.
/// </summary>
/// <remarks>
/// Blazor formats a <see cref="JSException"/> as the JavaScript error's message followed by a blank
/// line and its stack, so the whole thing on a snackbar is a wall of frames. The first line is the
/// sentence <c>app.js</c> wrote — for a download that is the API's own refusal, forwarded by
/// <see cref="AuthenticatedDownloadProxy"/> — and everything after it belongs in the log.
/// </remarks>
internal static class JsInteropErrors
{
    private const int MaxLength = 300;

    /// <summary>
    /// The message <paramref name="exception"/> carries from JavaScript, or <paramref name="fallback"/>
    /// when it carries none or is not a JavaScript failure at all.
    /// </summary>
    public static string DescribeOrDefault(Exception exception, string fallback)
    {
        if (exception is not JSException)
        {
            return fallback;
        }

        var firstLine = exception.Message.Split('\n', 2)[0].Trim();

        if (string.IsNullOrWhiteSpace(firstLine))
        {
            return fallback;
        }

        // A message long enough to be a stack frame run together with the text, or a server body that
        // got past app.js's own cap, is not something to paste onto a snackbar whole.
        return firstLine.Length > MaxLength
            ? string.Concat(firstLine.AsSpan(0, MaxLength), "…")
            : firstLine;
    }
}
