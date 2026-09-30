using System.Net;
using System.Net.Http.Json;
using ErrorOr;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Common.Http;

namespace ShopInventory.Web.Features.FiscalPrintForms;

/// <summary>
/// The one way the portal calls the fiscal document type endpoints, so a refusal reads the same wherever it
/// happens: in the API's own words where it gave some.
/// </summary>
internal static class FiscalPrintFormsApi
{
    public const string Base = "api/fiscalisation-settings/print-forms";

    public static string For(string cardCode) => $"{Base}/{Uri.EscapeDataString(cardCode.Trim())}";

    /// <summary>Sends the request and reads a <typeparamref name="T"/> back.</summary>
    public static async Task<ErrorOr<T>> SendAsync<T>(
        HttpClient httpClient,
        ILogger logger,
        HttpMethod method,
        string url,
        object? body,
        string what,
        CancellationToken cancellationToken)
    {
        var failed = $"Could not {what}.";

        try
        {
            using var response = await SendRawAsync(httpClient, method, url, body, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return await RefusedAsync(response, logger, url, what, cancellationToken);
            }

            var result = await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken);
            return result is null ? Errors.FiscalPrintForm.Failed(failed) : result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not {What} at {Url}", what, url);
            return Errors.FiscalPrintForm.Failed(failed);
        }
    }

    /// <summary>Sends a request that answers with no body.</summary>
    public static async Task<ErrorOr<Success>> SendAsync(
        HttpClient httpClient,
        ILogger logger,
        HttpMethod method,
        string url,
        string what,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendRawAsync(httpClient, method, url, null, cancellationToken);
            return response.IsSuccessStatusCode
                ? Result.Success
                : await RefusedAsync(response, logger, url, what, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not {What} at {Url}", what, url);
            return Errors.FiscalPrintForm.Failed($"Could not {what}.");
        }
    }

    private static Task<HttpResponseMessage> SendRawAsync(
        HttpClient httpClient, HttpMethod method, string url, object? body, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return httpClient.SendAsync(request, cancellationToken);
    }

    private static async Task<Error> RefusedAsync(
        HttpResponseMessage response, ILogger logger, string url, string what, CancellationToken cancellationToken)
    {
        var detail = await ProblemDetailReader.ReadMessageAsync(response, cancellationToken);
        logger.LogWarning("Could not {What}: {Url} answered {StatusCode}. {Detail}", what, url, (int)response.StatusCode, detail);

        return Errors.FiscalPrintForm.Failed(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Your session has expired. Refresh and try again.",
            HttpStatusCode.Forbidden when detail is null => "This account is not permitted to do that.",
            _ => detail ?? $"Could not {what}."
        });
    }
}
