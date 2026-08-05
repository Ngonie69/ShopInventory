using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

public interface IGLAccountLedgerService
{
    /// <summary>
    /// The postings against one G/L account over a date range.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The ledger could not be read. Carries a sentence fit to show the user.
    /// </exception>
    Task<GLAccountLedgerResponse> GetLedgerAsync(
        string accountCode,
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken = default);
}

public class GLAccountLedgerService(
    HttpClient httpClient,
    ILogger<GLAccountLedgerService> logger) : IGLAccountLedgerService
{
    // Written once, beside the controller route it has to match. A wrong path here does not fail
    // loudly — the API answers 404, and a service that caught its own exceptions would report that
    // as an empty ledger, which reads exactly like an account with no postings. Two route strings
    // in this project rotted that way for months, so this one throws.
    private const string LedgerPath = "api/glaccount/{0}/ledger";

    public async Task<GLAccountLedgerResponse> GetLedgerAsync(
        string accountCode,
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken = default)
    {
        var url = string.Format(
            CultureInfo.InvariantCulture,
            LedgerPath,
            Uri.EscapeDataString(accountCode))
            + $"?fromDate={fromDate:yyyy-MM-dd}&toDate={toDate:yyyy-MM-dd}";

        try
        {
            using var response = await httpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError(
                    "Failed to read the G/L ledger for {AccountCode}. Status: {StatusCode}, Error: {Error}",
                    accountCode,
                    response.StatusCode,
                    errorContent);

                throw new InvalidOperationException(ApiErrorResponse.GetFriendlyMessage(
                    response.StatusCode,
                    errorContent,
                    "We couldn't load this account's transactions right now. Please try again."));
            }

            GLAccountLedgerResponse? ledger;
            try
            {
                ledger = await response.Content.ReadFromJsonAsync<GLAccountLedgerResponse>(cancellationToken);
            }
            catch (JsonException ex)
            {
                // A shape mismatch between this model and the API's DTO. The raw message names a
                // JSON path and tells the user nothing, so it goes to the log and they get a
                // sentence.
                logger.LogError(
                    ex,
                    "The G/L ledger response for {AccountCode} did not match the expected shape",
                    accountCode);
                throw new InvalidOperationException(
                    "We couldn't read the transactions the server sent back. Please contact support if this continues.");
            }

            return ledger
                ?? throw new InvalidOperationException("The server returned an empty response for this account.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning(
                ex,
                "The G/L ledger for {AccountCode} exceeded the {TimeoutSeconds}s API budget",
                accountCode,
                httpClient.Timeout.TotalSeconds);
            throw new InvalidOperationException(
                "This account has more transactions than we could read in time. Try a shorter date range.");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            logger.LogError(ex, "Error reading the G/L ledger for {AccountCode}", accountCode);
            throw new InvalidOperationException(ApiErrorResponse.GetFriendlyMessage(
                ex,
                "We couldn't load this account's transactions right now. Please try again."));
        }
    }
}
